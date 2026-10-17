# ADR-0008 — Shared memory and knowledge graph

- **Status:** Proposed (2026-10-17) — implemented in plan Phase 3
- **Deciders:** AgentOS maintainers
- **Plan:** [Agentic SDLC v2 — Phase 3](../plans/agentic-sdlc-v2.md#phase-3--shared-context-memory-34-weeks) (owner decision D-B: pgvector built-in)

## Context

Agents share nothing but the typed objects of one run: no history, no project knowledge, no record of
how a similar ticket was solved, no structural view of the code. Every call re-sends whole artifacts,
so tokens grow with iterations, and a lesson learned in one run is lost by the next.

Requirements from the owner: shared context memory across agents (and delegated agents such as Claude
Code / Codex), a knowledge graph, token savings, **measured** benefit, current technology, and **every
layer visualised** in the desktop. Enterprise constraints: tenant isolation, one database to operate,
runs on Azure Database for PostgreSQL flexible server and air-gapped installs, no unbounded cost per
write.

What we learned from the field (2025–2026):

- **Temporal knowledge graphs** (Zep's Graphiti) store facts as edges with bi-temporal validity windows
  and invalidate outdated facts instead of overwriting them; retrieval is hybrid (semantic + BM25 +
  graph traversal) with **no LLM call at query time**. Its cost is per-write LLM extraction, and it
  needs Neo4j/FalkorDB.
- **GraphRAG** builds communities (Leiden) and summarises them with an LLM — strong for global
  questions, expensive to index. **LazyGraphRAG** defers LLM work to query time and indexes with cheap
  NLP, at ~0.1% of the indexing cost.
- **pgvector 0.8** gives HNSW with `halfvec` (16-bit, ~50% memory at near-zero recall loss), binary
  quantisation for re-ranked first passes, and iterative index scans so filtered (per-tenant/repo) k-NN
  still returns k rows. **Postgres 18** adds async I/O and native `uuidv7()`.
- **Embeddings with Matryoshka dimensions** (e.g. Voyage `voyage-code-4` for code, `voyage-4` for text)
  can be truncated to 1024/512 dims with small quality loss; a reranker (e.g. Voyage `rerank-3-lite`)
  lifts precision on the fused candidate list.
- **Microsoft.Extensions.AI** `IEmbeddingGenerator` and **Microsoft.Extensions.VectorData** (GA) give
  provider-neutral embedding and vector-store contracts in .NET.
- Anthropic's **memory tool** and **context editing** (clearing old tool results) let a Claude agent
  read/write memory through a tool and keep long sessions inside the window.

## Decision

### One module, four memory layers, one knowledge graph

`AgentOs.Modules.Memory` (schema `memory`), contract `IMemoryStore` in Domain. Every read and write goes
through the same governance seam as tools (policy → action → evidence) and emits `MemoryRead` /
`MemoryWrite` events.

| Layer | What | Lifetime |
|---|---|---|
| **Run blackboard** | artifacts of a run (spec, files, tests, QA reports), content-addressed (`sha256`) and passed **by handle** (`spec@v2`, `file:src/X.cs@<sha>`, `qa#3`) | run (+archive) |
| **Project memory** | conventions, decisions/ADRs, facts, lessons, preferences, imported docs | long-lived, decays |
| **Episodic memory** | finished work items: ticket → plan → PR → outcome | long-lived |
| **Code knowledge** | repo map (files, symbols, signatures, references) per commit | per commit |

The **knowledge graph** spans all four: every memory item, artifact, symbol, ticket and PR is a node;
relations are typed, bi-temporal edges.

### Graph model (Graphiti-inspired, in Postgres)

```sql
-- ids are uuidv7 (time-ordered, Postgres 18 native); every row carries tenant_id (RLS + EF filter)
memory.nodes (id uuid, tenant_id text, repo_id text null, kind text,   -- Repo|Module|File|Symbol|Ticket|PR|Run|Artifact|Decision|Lesson|Convention|Fact|Doc|Person|Test
              key text,            -- natural key, e.g. 'sym:AgentOs.Modules.Llm.CostCalculator.Calculate'
              title text, body text, attrs jsonb,
              embedding halfvec(1024) null, tsv tsvector,
              confidence real, pinned bool, last_used_utc timestamptz,
              valid_from timestamptz, valid_to timestamptz null,          -- world time: when it was true
              recorded_at timestamptz, retracted_at timestamptz null,     -- system time: when we learned/retracted it
              source_run uuid null, source_ref text null)                 -- provenance (run, PR, comment, file@sha)
memory.edges (id uuid, tenant_id text, src uuid, dst uuid, rel text,       -- calls|implements|depends_on|contains|touches|solved_by|produced|covers|learned_from|governs|supersedes|mentions
              weight real, attrs jsonb,
              valid_from timestamptz, valid_to timestamptz null, recorded_at timestamptz,
              source_run uuid null, source_ref text null)
memory.episodes, memory.run_items (handle, sha256, kind, version, blob_ref), memory.retrievals (trace per agent call)
```

- **Facts are invalidated, never overwritten:** a contradicting fact closes the old edge's
  `valid_to` and adds a `supersedes` edge — so the UI can show the graph **as of any date**.
- **Indexes:** HNSW on `embedding halfvec_cosine_ops` (iterative scan on for filtered queries), GIN on
  `tsv`, B-tree on `(tenant_id, repo_id, kind, key)` and `(tenant_id, src, rel)` / `(tenant_id, dst, rel)`.
- No graph database: one Postgres to operate, back up, secure and isolate. Graph traversal is a
  recursive CTE for cold paths and an **in-process CSR adjacency** (per tenant+repo, `FrozenDictionary`
  key → index, `int[]` offsets/targets, invalidated by version) for hot paths. Graphiti/Zep, Mem0 and
  Letta remain possible as `IMemoryStore` **plugins**, not dependencies.

### Building the graph — deterministic first, LLM last (LazyGraphRAG economics)

| Source | Extractor | LLM? |
|---|---|---|
| C# code | Roslyn (symbols, calls, inheritance, references) | no |
| Other languages | tree-sitter | no |
| Git / PRs / issues | provider APIs: `Ticket -solved_by-> PR -touches-> File` | no |
| ADRs / docs / wiki (via MCP) | Markdown structure + noun-phrase extraction, chunked | no |
| Runs | the blackboard itself (`Run -produced-> Artifact`, `Test -covers-> Symbol`) | no |
| Lessons, decisions | **reflection** after a run on failures, QA issues, review comments (small model, e.g. Haiku) | yes — candidates only |

Reflection output is a *candidate*: auto-accepted above a confidence threshold, otherwise queued for
human curation. **Communities** (Leiden over the code + knowledge graph) give module/topic clusters;
their summaries are generated lazily, on first use, and cached — never a full LLM pass at index time.

### Retrieval — no LLM at query time, bounded tokens

1. **Seed:** k-NN on `halfvec` (binary-quantised first pass + exact re-rank) ∥ lexical (Postgres FTS
   `ts_rank_cd` by default; ParadeDB `pg_search` BM25 when self-hosted) → **Reciprocal Rank Fusion**
   with recency decay and pin boost.
2. **Expand:** personalised PageRank (push approximation, ε-bounded) from the seeds over the CSR,
   1–2 hops, edge types weighted per task (coding favours `calls/touches/covers`; requirements favour
   `governs/decision/lesson`); only edges valid *now*.
3. **Re-rank** (optional): cross-encoder reranker on the top ~50.
4. **Pack:** min-heap + greedy knapsack on value/tokens under the call's memory budget; stable items go
   into the cached prompt prefix (prompt caching), volatile ones after it.
5. **Trace:** the whole pipeline (scores per stage, chosen vs dropped, tokens) is stored in
   `memory.retrievals` — this powers the Retrieval Inspector and the metrics.

### Embeddings, caching, dedupe

- `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI). Providers are a UI
  setting: Voyage (`voyage-code-4` for code, `voyage-4` for text), Azure OpenAI
  (`text-embedding-3-large`), or local ONNX for air-gapped installs. Stored at **1024 dims as
  `halfvec`** (Matryoshka truncation); the model id + dims are recorded per row, so a model change
  re-embeds in the background instead of mixing spaces.
- `HybridCache` keyed by content hash → unchanged text is never re-embedded.
- Near-duplicate merge on write with **SimHash + LSH** buckets (O(1) expected), not pairwise compare.

### Access for every agent

- Built-in agents: `IRunContext` (handles) + `IMemoryRetriever` inside the prompt builder.
- **MCP tools** on `/mcp`: `memory.search`, `memory.write`, `graph.neighbors`, `graph.path`,
  `repo_map`, `find_symbol`, `context.get(handle)` — so **Claude Code / Codex sessions read and write
  the same memory** as AgentOS agents.
- Anthropic **memory tool** backend mapped onto the store; **context editing** enabled for long
  delegated sessions.

### Everything is visualised (Memory app in the desktop)

| View | Shows |
|---|---|
| **Graph Explorer** | the knowledge graph in **WebGL** (sigma.js v3 + graphology, ForceAtlas2 layout in a Web Worker): colour = kind, size = degree/usage, edge style = relation; filter by repo/kind/relation, search, expand neighbours, community hulls, **time slider** ("as of" date from the bi-temporal model), click → provenance |
| **Retrieval Inspector** | per agent call: query → seeds (vector / lexical / fused scores) → expansion path lit up on the graph → packed items as token bars against the budget → what the answer actually used |
| **Run Blackboard** | timeline of artifacts per iteration and their versions, diffs between versions, which agent read/wrote which handle, tokens saved by passing handles |
| **Knowledge base** | memory items table: search, edit, pin, forget, provenance; **curation queue** for reflection candidates |
| **Code Map** | module/dependency graph with Leiden communities, symbol search, "who touches this" |
| **Memory metrics** | hit rate, retrieved tokens, tokens saved, cache hits, memory-on vs off eval deltas, growth and decay |

Every graph view has an equivalent table/list (keyboard + screen-reader path), honours
`prefers-reduced-motion` (static layout), uses design tokens, and is designed in Claude Design first.
JavaScript libraries are vendored into `wwwroot/lib` (pinned, MIT) and loaded as ES modules through
Blazor JS interop — no runtime CDN, so it works air-gapped.

### Tenancy — the knowledge base is per tenant

- **Every row belongs to exactly one tenant** (`tenant_id` on nodes, edges, items, episodes, run items,
  retrieval traces, blobs). There is **no global knowledge base** and no cross-tenant retrieval,
  similarity or graph edge — an edge can only join two nodes of the same tenant (enforced by a check
  on write and a test).
- Within a tenant, knowledge is scoped further: **tenant → repo/project → role** (a convention can apply
  to the whole tenant, one repo, or only the Coding agent).
- **Platform starter packs** (curated, e.g. ".NET conventions") are templates a tenant admin can
  *import*; the import copies them into the tenant as its own rows — never shared rows.
- Per-tenant **settings**: embedding provider + key (BYO), memory on/off, token budgets, retention,
  auto-accept threshold for reflection — all registry settings (ADR-0011).
- Per-tenant **quotas and cost**: embedding and reflection spend is metered as run metrics and counts
  against the tenant's budget gate.
- **Export / purge:** a tenant can export its knowledge base (JSON + graph) and an offboarded tenant is
  purged completely (rows, blobs, caches) — an auditable, tested operation.
- Caches are keyed by tenant: the CSR adjacency is per `(tenant, repo)`, `HybridCache` keys include the
  tenant, and MCP memory tools resolve the tenant from the caller's token, never from arguments.
- Every query filters by tenant **first** (HNSW iterative scan keeps filtered k-NN complete), and the
  Graph Explorer, Retrieval Inspector and metrics only ever show the signed-in tenant.

### Policies

- **Write policy:** only reflection, curated user edits, deterministic extractors and explicit
  `memory.write` (policy-gated tool) write; third-party text is stored as `untrusted` and is fenced
  when retrieved into a prompt.
- **Redaction:** secrets and PII are scrubbed before embedding/writing.
- **Retention/decay:** score decays with time since `last_used_utc`; pinned items don't decay;
  retracted items stay for audit, invisible to retrieval. Per-tenant retention is a setting.
- **Isolation:** `tenant_id` on every row, EF global filter **and** Postgres row-level security
  (`app.tenant_id` set per connection; the platform/migration role is the only one that bypasses it);
  two-tenant isolation tests on real Postgres. RLS is rolled out platform-wide first (plan 0.9), so the
  memory module inherits it rather than inventing it.
- **Budgets:** max memory tokens per call and per run are settings; retrieval never exceeds them.

## Consequences

- **+** One database, one governance path, tenant isolation reused; no graph DB to operate.
- **+** Indexing is mostly deterministic and cheap; LLM cost is limited to reflection and lazy
  community summaries.
- **+** The bi-temporal model makes the graph auditable and time-travelable in the UI.
- **+** Delegated agents share memory through MCP, which is the point of "shared context".
- **−** Graph algorithms (PPR, Leiden, CSR cache) are ours to build and test; mitigated by benchmarks in
  the DS&A playbook and property tests.
- **−** BM25 quality on Azure flexible server is Postgres FTS, not true BM25 (`pg_search` is not
  available there); acceptable because vector + graph carry most of the recall — measured in the eval.
- **−** Visualising large graphs needs a JS dependency (sigma.js + graphology); vendored, pinned, and
  isolated behind one interop module.
- **Gate:** memory ships enabled only if the eval shows memory-on beats memory-off (pass@1 ↑ and/or
  tokens ↓); the graph expansion step ships only if it beats plain hybrid retrieval.
