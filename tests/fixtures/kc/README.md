# KC dataset — benchmark scenarios

Five dataset files for the KC1–KC5 scenarios. Each file is an array of cases `{id, input, expected}`.

| File | Scenario | Endpoint | Metrics |
|---|---|---|---|
| `kc1.json` | KC1 — Requirement analysis | `POST /requirement` | tokens, cost, entities, endpoints, acceptance criteria |
| `kc2.json` | KC2 — Code generation | `POST /code` | tokens, cost, files, must-have class/route |
| `kc3.json` | KC3 — Test generation | `POST /test` | tokens, cost, happy/edge/error counts, framework |
| `kc4.json` | KC4 — Pipeline end to end | `POST /pipeline` | total tokens/cost, iterations, final status, QA score |
| `kc5.json` | KC5 — QA consistency | `POST /qa` | tokens, cost, score, isConsistent, issue count |

KC2/KC3/KC5 depend on an earlier stage's output; `specFixture` / `codeFixture` / `testsFixture` point
to recorded snapshots (refresh them by recording real responses).

## Status

No harness reads these files yet. They are the seed for the `greenfield-csharp` eval suite
(`tests/AgentOs.Evals`, plan item 1.8 in [docs/plans/agentic-sdlc-v2.md](../../../docs/plans/agentic-sdlc-v2.md)),
which adds hidden tests and a committed baseline. Don't change the `expected` shape without updating
that harness.

## Live reproducibility bench (today)

`tests/AgentOs.Tests/Smoke/KcLiveBenchTests.cs` runs the full pipeline against a **real** model and
derives KC1–KC5 metrics from the per-agent metric rows. It never runs in CI and costs real money:

```bash
RUN_LIVE_LLM=1 KC_LIVE_N=10 dotnet test --project tests/AgentOs.Tests --filter-class "*KcLiveBenchTests"
# KC_LIVE_BUILD=1 also compiles the generated code/tests (compile-pass for KC2/KC3)
```

Configure a key first (user-secrets or environment — see [docs/SETUP.md](../../../docs/SETUP.md)).
