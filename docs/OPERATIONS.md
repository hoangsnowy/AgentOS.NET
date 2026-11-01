# Operations — metrics, dashboards, alerts and SLOs

How to watch a running AgentOS.NET: what it measures, the service-level objectives, how to import the dashboards
and alert rules, and what to do when an alert fires. Shipped as plan item **E1.10**
([enterprise-readiness](plans/enterprise-readiness.md)).

| Asset | Path |
|---|---|
| Grafana dashboards (3) | [`deploy/observability/grafana/*.json`](../deploy/observability/grafana) + provisioning files |
| Prometheus recording + alert rules | [`deploy/observability/prometheus/alerts.yml`](../deploy/observability/prometheus/alerts.yml) (unit tests: `alerts.test.yml`) |
| Prometheus scrape example | [`deploy/observability/prometheus/prometheus.yml`](../deploy/observability/prometheus/prometheus.yml) |
| OpenTelemetry Collector config | [`deploy/observability/otel-collector.yaml`](../deploy/observability/otel-collector.yaml) |
| Azure Monitor workbook | [`deploy/observability/azure/agentos-workbook.json`](../deploy/observability/azure/agentos-workbook.json) |
| Azure alerts + App Insights (Bicep) | [`infra/observability/observability.module.bicep`](../infra/observability/observability.module.bicep), behind `enableObservability` |

## 1. How telemetry leaves the app

Both hosts (Api + Web) call `AddServiceDefaults()` (`src/AgentOs.ServiceDefaults`), which registers OpenTelemetry
metrics, traces and logs. Nothing is exported until you configure a destination:

| Destination | Set on the Api + Web | Then |
|---|---|---|
| **Prometheus via the OTel Collector** | `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`, `OTEL_SERVICE_NAME=api` / `web` | Prometheus scrapes the collector (`:8889`). |
| **Prometheus direct** (≥ 3.0, `--web.enable-otlp-receiver`) | `OTEL_EXPORTER_OTLP_ENDPOINT=http://prometheus:9090/api/v1/otlp`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, `OTEL_SERVICE_NAME` | No collector needed. |
| **Azure Monitor / Application Insights** | `APPLICATIONINSIGHTS_CONNECTION_STRING` (azd sets it when provisioned with `enableObservability`) | Metrics land in `customMetrics`, spans in `requests` / `dependencies`. |
| **Aspire dashboard** (dev) | set automatically by the AppHost | `http://localhost:17050`. |

`OTEL_SERVICE_NAME` becomes the Prometheus `job` label every dashboard and alert groups by; keep it stable.
Optionally set `OTEL_METRIC_EXPORT_INTERVAL` (ms, default 60000) — 15000 makes the dashboards livelier.

## 2. Metric inventory

Prometheus names below follow the standard OTLP → Prometheus translation: dots become underscores, the unit is
appended (`s` → `_seconds`, `By` → `_bytes`, `1` on a gauge → `_ratio`; `{annotations}` are dropped), counters end
in `_total`, histograms expose `_bucket` / `_sum` / `_count`. Every name in the AgentOS.NET section was verified
end-to-end by exporting from the real code into Prometheus 3.7, and `ObservabilityAssetsTests` fails the build when a
dashboard or rule references a series no instrument emits.

### 2.1 AgentOS.NET instruments

| Meter | Instrument (Prometheus) | Type | Tags (labels) | Meaning |
|---|---|---|---|---|
| `AgentOs.Llm` | `gen_ai.client.token.usage` (`gen_ai_client_token_usage_*`) | histogram `{token}` | `gen_ai.system`, `gen_ai.request.model`, `gen_ai.agent.name`, `gen_ai.token.type` = input/output | GenAI semconv. **input includes cache reads + writes.** |
| | `gen_ai.client.operation.duration` (`gen_ai_client_operation_duration_seconds_*`) | histogram `s` | system, model, agent | One provider call (a tool-calling call includes its tool loop). |
| | `agentos.llm.cost.usd` (`agentos_llm_cost_usd_*`) | histogram `{USD}` | system, model, agent | Cost per call. `_sum` = spend. RemoteAgent = 0 (member's own subscription). |
| | `agentos.llm.calls` (`agentos_llm_calls_total`) | counter | system, model, agent | Successful calls. |
| | `agentos.llm.tokens` (`agentos_llm_tokens_total`) | counter | system, model, agent, `agentos.token.class` = `fresh_input` · `cache_read` · `cache_write` · `output` · `reasoning` | **Owner directive 3 split.** The first four sum to all tokens; `reasoning` ⊂ `output` — never stack it. |
| | `agentos.llm.errors` (`agentos_llm_errors_total`) | counter | system, model, agent, `error.type` = `rate_limited` · `transient` · `auth` · `bad_request` · `other` | Failed attempts: a 429 the key pool then failed over, and a non-retryable failure (`bad_request`, `other`) that propagates. RemoteAgent: runner timeout = `transient`, the member's plan limit = `rate_limited`, anything else (no runner, CLI failure) = `other`. |
| `AgentOs.Pipeline` | `agentos.pipeline.runs` (`agentos_pipeline_runs_total`) | counter | `agentos.run.kind` = `pipeline` · `workflow`, `agentos.run.outcome` = `done` · `max_iterations` · `failed` · `cancelled` · `budget_blocked` · `refused` | Every run attempt and how it ended. `budget_blocked` / `refused` never started. Workflow runs end `done`, `failed` or `cancelled` — never `max_iterations` (a graph's loop caps end the loop, not the run), so QA convergence is a pipeline-only KPI. |
| | `agentos.pipeline.run.duration` (`agentos_pipeline_run_duration_seconds_*`) | histogram `s` | kind, outcome | Wall time of runs that started. |
| | `agentos.budget.blocked` (`agentos_budget_blocked_total`) | counter | `agentos.budget.scope` = `run` · `agent`, `gen_ai.agent.name` (agent scope) | Refusals by the enforced monthly cap. |
| | `agentos.budget.utilization` (`agentos_budget_utilization_ratio`) | observable gauge `1` | `agentos.tenant.id` | Month-to-date spend ÷ cap from the tenant's last budget evaluation in that process (run start, Board agent call); **0** when no cap is set or once the UTC month rolled over. Reported for 15 min after the last evaluation, then the series ends — so a removed cap, a new month or a replica that stopped seeing the tenant can never hold an old over-cap value. |
| | `agentos.budget.enforced` (`agentos_budget_enforced`) | observable gauge `{flag}` | `agentos.tenant.id` | 1 when the tenant's cap is enforced (over-cap runs refused), else 0. Same lifetime as utilization; a separate series (not a label) so toggling enforcement never strands an old series. |
| `AgentOs.Tools` | `agentos.evidence.inline_writes` (`agentos_evidence_inline_writes_total`) | counter | — | Evidence queue full → row written on the caller's path (slower, lossless). |
| | `agentos.evidence.lost` (`agentos_evidence_lost_total`) | counter | — | Evidence row lost after every retry. **Must stay 0** (E0.3). |
| `AgentOs.Health` | `agentos.health.status` (`agentos_health_status`) | gauge | `agentos.health.check` = `self` · `postgres` · `keycloak` · `overall` | 1 healthy · 0.5 degraded · 0 unhealthy, published every 60 s (each publish runs every check, so not more often). |

Traces: the `AgentOs.Llm` ActivitySource emits one `chat {model}` client span per provider attempt (attributes
`gen_ai.*`, `gen_ai.usage.*` incl. `cache_read.input_tokens` / `cache_creation.input_tokens`, `agentos.cost.usd`,
`agentos.tenant.id`, `error.type` on failures).

### 2.2 Built-in meters wired in ServiceDefaults

| Source | Key instruments (Prometheus) |
|---|---|
| ASP.NET Core hosting | `http_server_request_duration_seconds_*` (`http_route`, `http_request_method`, `http_response_status_code`), `http_server_active_requests` |
| Kestrel | `kestrel_active_connections`, `kestrel_queued_connections`, `kestrel_rejected_connections_total`, `kestrel_connection_duration_seconds_*`, `kestrel_upgraded_connections`, TLS handshake |
| SignalR (`Microsoft.AspNetCore.Http.Connections`) | `signalr_server_active_connections` (`signalr_transport`), `signalr_server_connection_duration_seconds_*` (`signalr_connection_status`) |
| Blazor (`…Components`, `…Components.Server.Circuits`) | `aspnetcore_components_circuit_active`, `aspnetcore_components_circuit_connected`, `aspnetcore_components_circuit_duration_seconds_*`, render-diff / event-handling durations |
| Diagnostics, routing, rate limiting, auth | `aspnetcore_diagnostics_exceptions_total`, `aspnetcore_routing_match_attempts_total`, `aspnetcore_rate_limiting_requests_total` (`aspnetcore_rate_limiting_result`), authentication / authorization counters |
| HttpClient | `http_client_request_duration_seconds_*` (`server_address`, `http_response_status_code`, `error_type`), `http_client_active_requests`, `http_client_open_connections` |
| Npgsql (`Npgsql`) | `db_client_operation_duration_seconds_*`, `db_client_connection_count` (`db_client_connection_state`), `db_client_connection_max`, `db_client_connection_npgsql_pending_requests`, `db_client_connection_npgsql_timeouts_total` |
| .NET runtime (`System.Runtime`) | `dotnet_process_cpu_time_seconds_total`, `dotnet_process_memory_working_set_bytes`, `dotnet_gc_pause_time_seconds_total`, `dotnet_gc_collections_total`, `dotnet_thread_pool_queue_length_total`, `dotnet_exceptions_total` |

### 2.3 Health checks

| Endpoint | Checks | Use |
|---|---|---|
| `/alive` | `self` (tag `live`) | Liveness probe — restart the container when it fails. |
| `/health` | `self` + `postgres` (when `ConnectionStrings:DefaultConnection` is set) + `keycloak` (OIDC discovery, outside Development) | Readiness probe + the outside-in availability probe. JSON body names each check. |

Every check is also the `agentos_health_status` gauge, so an unhealthy dependency alerts even when nobody probes.

### 2.4 Known gaps

| Gap | Why it isn't here yet |
|---|---|
| Paired runners connected (count, disconnects) | Lives in `Modules.RemoteAgent`, which another work stream owns; until then `signalr_server_active_connections` on the Web includes the `/hubs/remote-agent` connections, and a disconnect shows as a failed call on that runner. |
| Board / Autopilot ticket runs in `agentos.pipeline.runs` | The ticket flow is a separate work stream; Board agents do report LLM usage, budget refusals (`scope=agent`) and evidence. |
| Per-tenant LLM spend as a metric label | Deliberately not a label on the high-volume LLM instruments (cardinality); per-tenant spend is in Insights and the budget gauge. |

## 3. SLOs

| SLO | SLI | Target | Window | Alerts |
|---|---|---|---|---|
| **Availability** | Non-5xx HTTP responses ÷ all responses (`/health`, `/alive` excluded), per host | **99.5%** (error budget 0.5% ≈ 3 h 36 min of total outage) | 30 days rolling | `AgentOsAvailabilitySloFastBurn` (14.4× burn over 1 h + 5 m), `AgentOsAvailabilitySloSlowBurn` (6× over 6 h + 30 m), `AgentOsHigh5xxRate`, `AgentOsHealthProbeDown` |
| **Run reliability** | Started runs that did not fail ÷ started runs (`done` + `max_iterations` + `failed`) | **95%** | 30 days | `AgentOsRunReliabilitySloBurn` (6× over 1 h + 6 h), `AgentOsFailedRunSpike` |
| **HTTP latency** | p95 of short requests (Blazor `/_blazor`, `/hubs/*`, `/pipeline*`, `/mcp`, health excluded — their duration is a connection's lifetime) | **≤ 1 s** | 5-minute windows, 99% of windows over 30 days | `AgentOsHttpLatencyHigh` |
| **LLM call latency** | p95 `gen_ai.client.operation.duration` per model | **≤ 120 s** | 15-minute windows | `AgentOsLlmLatencyHigh` |
| **Run latency** | p95 wall time of completed runs | **≤ 30 min** | 1-hour windows | `AgentOsRunsSlow` |
| **Budget** | Tenants with month-to-date spend ≥ cap and **no** enforcement; evidence rows lost | **0** | always | `AgentOsBudgetOverCap`, `AgentOsBudgetNearCap` (80%), `AgentOsBudgetBlockingRuns`, `AgentOsEvidenceLost` |

QA convergence (`done` ÷ (`done` + `max_iterations`)) is a product KPI shown on the Runs dashboard, not an SLO: a run
that stops at `NMax` behaved correctly.

The recording rules `agentos:http_error_ratio:rate{5m,30m,1h,6h}`, `agentos:run_failure_ratio:rate{1h,6h}`,
`agentos:http_request_duration_seconds:p95_5m` and `agentos:llm_call_duration_seconds:p95_15m` are the SLIs.

## 4. Import the dashboards and rules

### Grafana + Prometheus

1. **Collector**: run `otel/opentelemetry-collector-contrib` with `deploy/observability/otel-collector.yaml`, and point
   the hosts at it (§1). Or enable Prometheus' OTLP receiver and skip the collector.
2. **Prometheus**: merge the jobs from `deploy/observability/prometheus/prometheus.yml`, copy `alerts.yml` next to it
   and list it under `rule_files`. For `AgentOsHealthProbeDown`, run `prom/blackbox-exporter` and set the two
   `/health` URLs. Wire your Alertmanager (receivers / routes on the `severity` label: `critical` pages,
   `warning` tickets).
3. **Grafana** — either:
   - *UI*: Dashboards → New → Import → upload each `deploy/observability/grafana/agentos-*.json`, choose your
     Prometheus data source (the dashboards use a `datasource` variable, so any Prometheus-compatible source works); or
   - *Provisioning*: mount `deploy/observability/grafana/provisioning` at `/etc/grafana/provisioning` and the
     `deploy/observability/grafana` folder at `/var/lib/grafana/dashboards/agentos`; edit the data source `url`
     in `provisioning/datasources/agentos-prometheus.yaml`. The dashboards appear in an **AgentOS.NET** folder.

| Dashboard | uid | What it answers |
|---|---|---|
| AgentOS.NET — LLM tokens, cost & latency | `agentos-llm` | Tokens per billing class and per agent, cache hit ratio, cost by agent / model (charts + tables), latency p50/p95 per model and agent, errors by type, 429s per provider. |
| AgentOS.NET — Runs, budget & evidence | `agentos-runs` | Run-reliability SLO, QA convergence, runs by outcome / kind, run time p50/p95, budget used per tenant, refusals, evidence lost / inline. |
| AgentOS.NET — HTTP, SignalR & runtime | `agentos-http` | Availability + latency SLOs, requests by status class, slowest routes, Kestrel, Blazor circuits, SignalR, outbound HTTP, Postgres pool, health checks, CPU / memory / GC / thread pool. |

Validate after editing:

```bash
promtool check rules deploy/observability/prometheus/alerts.yml
promtool test rules  deploy/observability/prometheus/alerts.test.yml
promtool check config deploy/observability/prometheus/prometheus.yml
dotnet test --project tests/AgentOs.Tests --filter-class "*ObservabilityAssetsTests"
```

### Azure (azd)

```bash
azd env set AGENTOS_ENABLE_OBSERVABILITY true
azd env set AGENTOS_ALERT_EMAIL ops@example.com
azd env set AGENTOS_HEALTH_PROBE_URL https://web.<your-aca-domain>/health   # optional: availability test
azd provision && azd deploy
```

This deploys workspace-based Application Insights on the existing Log Analytics workspace, the **AgentOS.NET —
operations** workbook (Azure portal → Application Insights → Workbooks), an e-mail action group, log-search alerts
(`AgentOsBudgetOverCap`, `AgentOsFailedRunSpike`, `AgentOsEvidenceLost`, `AgentOsHealthCheckUnhealthy`,
`AgentOsHigh5xxRate`, `AgentOsLlmRateLimitStorm`), a server-exceptions metric alert and, with a probe URL, a
3-region availability test + `AgentOsHealthProbeDown` metric alert. `azd` passes the connection string to both
container apps as `APPLICATIONINSIGHTS_CONNECTION_STRING` (empty when observability is off, which disables export).
To import the workbook by hand instead: Workbooks → New → Advanced editor → paste
`deploy/observability/azure/agentos-workbook.json`.

## 5. Runbooks

Every alert's `runbook_url` points here. First move for any alert: open the matching dashboard at the alert's start
time, and the structured logs (`LlmCallCompleted`, budget warnings, evidence errors carry tenant + run ids).

<a id="runbook-agentosbudgetovercap"></a>
### AgentOsBudgetOverCap (critical)

A tenant's month-to-date LLM spend reached its monthly cap. The alert description says whether the cap is enforced
(`agentos_budget_enforced`): enforced → server-token runs and Board agent calls are refused (members' paired computers
still work); not enforced → spend keeps growing. The gauge is the latest evaluation per replica for 15 minutes, so
after raising / removing the cap the alert clears once every replica has re-evaluated the tenant or 15 minutes pass.
1. Runs dashboard → *Budget used by tenant*; LLM dashboard → *Cost by agent × model* to see what drove it.
2. Confirm with the tenant owner: raise the cap, or turn on enforcement — Insights → budget (*Monthly cap (USD)* +
   the enforce toggle; stored per tenant as `budget/monthly-cap-usd` / `budget/enforce`). An unenforced tenant over cap is a cost incident: enforce first, discuss after.
3. If spend jumped abnormally (a loop, a runaway schedule), pause the tenant's scheduled runs and check
   `max_iterations` outcomes.

<a id="runbook-agentosbudgetnearcap"></a>
### AgentOsBudgetNearCap (warning)

80% of the cap is used. Tell the tenant owner before runs start failing; check whether the month's run rate will
cross the cap (LLM dashboard → *Cost / hour by agent*).

<a id="runbook-agentosbudgetblockingruns"></a>
### AgentOsBudgetBlockingRuns (warning)

The budget gate refused runs (`scope=run`) or Board agent calls (`scope=agent`). Expected while a tenant is over an
enforced cap — see AgentOsBudgetOverCap. If no tenant is over cap, the budget store is returning a wrong spend:
check `run_metrics` for the tenant and the `budget evaluation failed` warnings.

<a id="runbook-agentosfailedrunspike"></a>
### AgentOsFailedRunSpike (critical)

More than 25% (and at least 3) of started runs failed in 30 minutes.
1. LLM dashboard → *Failed attempts by error type*: rate limits / auth / provider outage explain most spikes.
2. Logs: search `LlmException` and the agent name; a malformed-output burst after a model or prompt change points at
   the prompt (Settings → Prompts → restore the previous version).
3. If one tenant dominates, check its provider settings and key.

<a id="runbook-agentosrunreliabilitysloburn"></a>
### AgentOsRunReliabilitySloBurn (warning)

Failures are consuming the 95% run-reliability budget 6× faster than sustainable over 1 h and 6 h. Same diagnosis as
AgentOsFailedRunSpike; this is the slower, steadier version — usually one agent or model that degraded.

<a id="runbook-agentosrunsslow"></a>
### AgentOsRunsSlow (warning)

p95 completed-run time exceeds 30 minutes. Check *Call latency p95 by agent* (a slow model), *Runs by outcome*
(more `max_iterations` = more QA loops) and the thread-pool queue on the HTTP dashboard. Agent time limits live in
Settings → Autopilot.

<a id="runbook-agentosevidencelost"></a>
### AgentOsEvidenceLost (critical)

Tool-call evidence rows were dropped after every persistence retry — the governance trail has a hole.
1. The error log names tenant + tool for each lost row; record them for the audit.
2. Check Postgres health (`AgentOsHealthCheckUnhealthy`, pool panel) — lost evidence almost always means the
   database rejected or timed out writes.
3. The in-process counter (`BufferedToolInvocationLog.LostCount`) resets on restart; the metric history does not —
   use the Runs dashboard's *Evidence lost* panel for the incident window.

<a id="runbook-agentosevidencequeuesaturated"></a>
### AgentOsEvidenceQueueSaturated (warning)

The evidence queue has been full for 15 minutes, so rows are written inline on the tool-call path: nothing is lost,
but tool calls are slower. Look for a tool-call flood (a looping agent) or slow Postgres writes.

<a id="runbook-agentoshealthprobedown"></a>
### AgentOsHealthProbeDown (critical)

The outside-in probe of `/health` fails. `/health` is 503 when a readiness check is unhealthy — read its JSON body:
`postgres` (database unreachable / credentials), `keycloak` (OIDC discovery unreachable). If the endpoint does not
answer at all, check the container (Container Apps revision / pod status, `/alive`), ingress and TLS certificate.

<a id="runbook-agentoshealthcheckunhealthy"></a>
### AgentOsHealthCheckUnhealthy (critical)

The in-process gauge reports a dependency check at 0. `postgres`: connectivity, credentials, connection limits
(`max_connections`), disk. `keycloak`: the IdP is down or the metadata address changed — sign-in fails for everyone.

<a id="runbook-agentostelemetrymissing"></a>
### AgentOsTelemetryMissing (warning)

No `agentos_health_status` series for 10 minutes from one job (`job` label set: that host stopped exporting while
the others still do) or from any job (no `job` label): either the host(s) are down (check the probe) or export broke —
`OTEL_EXPORTER_OTLP_ENDPOINT` changed, the collector is down, or the Prometheus scrape of the collector fails (`up`).

<a id="runbook-agentoshigh5xxrate"></a>
### AgentOsHigh5xxRate (critical)

More than 5% of requests to one host return 5xx for 10 minutes. HTTP dashboard → *Slowest routes* and *Unhandled
exceptions*; logs by `traceId`. Recent deploy → roll back the revision. Dependency down → see the health runbooks.

<a id="runbook-agentosavailabilitysloburn"></a>
### AgentOsAvailabilitySloFastBurn / SlowBurn

The availability error budget (0.5% / 30 days) is burning 14.4× (fast: 2% of the budget per hour) or 6× (slow) too
fast. Treat fast burn like an outage (same steps as AgentOsHigh5xxRate); slow burn is a ticket — find the route
contributing most 5xx and fix it before the budget is gone.

<a id="runbook-agentoshttplatencyhigh"></a>
### AgentOsHttpLatencyHigh (warning)

p95 of short requests is above 1 s. Check the thread-pool queue and CPU (sync-over-async, starvation), the Postgres
pool and command p95, and outbound HTTP p95 (a slow Keycloak or GitHub call on the request path).

<a id="runbook-agentospostgrespoolexhausted"></a>
### AgentOsPostgresPoolExhausted (warning)

Requests wait for a Postgres connection for 5 minutes. Look for long-running queries (`pg_stat_activity`), raise
`Maximum Pool Size` in the connection string only after ruling out leaks, and check the database's
`max_connections` against replicas × pool size.

<a id="runbook-agentosllmratelimitstorm"></a>
### AgentOsLlmRateLimitStorm (critical)

More than 20% of attempts against one provider are HTTP 429. The key pool fails over and honours `Retry-After`, so
runs slow down before they fail.
1. Add keys to the pool (Settings → LLM providers) or raise the provider quota (Azure OpenAI TPM / Anthropic tier).
2. Lower concurrency (Settings → Platform → Run limits) or move agents to another provider / model (Settings → Agent
   profiles).

<a id="runbook-agentosllmauthfailures"></a>
### AgentOsLlmAuthFailures (warning)

A provider rejected a key (401/403). A key was revoked, expired or mistyped. Find which (logs: `Auth on a key`),
replace it in Settings → LLM providers; other keys in the pool keep serving meanwhile.

<a id="runbook-agentosllmerrorratehigh"></a>
### AgentOsLlmErrorRateHigh (warning)

More than 10% of attempts fail for reasons other than 429. `transient`: provider outage — check its status page,
consider switching provider. `bad_request`: a model or parameter the provider rejects (a new model id, a
temperature on a reasoning model — see `ModelCapabilities`). `other`: read the exception in the logs.

<a id="runbook-agentosllmlatencyhigh"></a>
### AgentOsLlmLatencyHigh (warning)

A model's p95 call time exceeds 120 s. Provider-side slowness (status page), very large prompts (*Tokens / min by
class* — fresh input growing means the prompt cache stopped hitting), or long tool loops in one agent.
