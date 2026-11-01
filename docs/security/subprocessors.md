# AgentOS.NET — Sub-processors and what leaves the box

> Verified against `main` @ `28b05ed`. AgentOS.NET is self-hosted software: **the operator** (you) decides which
> third parties are used and holds the contracts with them. This page lists every external service the code can
> send data to, when it does, and what is sent, so you can build your own sub-processor list. It is not a list of
> companies the AgentOS.NET project shares data with — the project receives nothing from your deployment.

Nothing below is contacted unless it is configured. A fresh standalone run (`dotnet run --project src/AgentOs.Web`
in Development) contacts no third party: offline LLM fallback serves canned output and modules use no-op stores.

## 1. LLM providers

The provider is chosen per agent in **Settings → Agent profiles** (code defaults; `Llm:ForceProvider` routes all
agents to one). Registered providers: `src/AgentOs.Modules.Llm/LlmModule.cs:45-170`,
`src/AgentOs.Modules.RemoteAgent/RemoteAgentModule.cs:34`.

| Provider (name) | Sub-processor | Endpoint | Whose key | When data leaves | What is sent |
|---|---|---|---|---|---|
| **Claude** (`Claude`, alias `Anthropic`) | Anthropic | Anthropic SDK default (`https://api.anthropic.com`) — `src/AgentOs.Modules.Llm/SdkChatClients.cs:22` | Tenant's key; platform key only if the tenant set none (`LlmModule.cs:176-190`) | Every agent call routed to Claude | System prompt (agent prompt + workspace rules + repo guidance), user content (fenced ticket text, code, test output, review findings), tool schemas; for the Designer agent (Design gate, [ADR-0016](../adr/0016-design-gate.md)) also **screenshots of the app under test** as image blocks (`src/AgentOs.Modules.Llm/LlmMessages.cs`); completions + usage come back |
| **Azure OpenAI** (`AzureOpenAI`, also used by `MAF`) | Microsoft (Azure OpenAI in the region **you** chose for the resource) | Tenant-set `https://<resource>.openai.azure.com` (`LlmSettings.cs:70-73`) | Tenant's key / pool | Every call routed to Azure / MAF | Same as Claude (MAF: system + user only, no tools — `MafChatClient.cs:88-96`) |
| **OpenAI** (`OpenAI`) | OpenAI | Fixed `https://api.openai.com/v1` (`VendorProviders.cs:40-48`) | Tenant's key | Calls routed to OpenAI | Same as Claude |
| **Gemini** (`Gemini`, alias `Google`) | Google | Fixed Gemini OpenAI-compatible endpoint (`VendorProviders.cs:40-48`) | Tenant's key | Calls routed to Gemini | Same as Claude |
| **xAI** (`xAI`, alias `Grok`) | xAI | Fixed `https://api.x.ai/v1` (`VendorProviders.cs:40-48`) | Tenant's key | Calls routed to xAI | Same as Claude |
| **OpenAI-compatible** (`OpenAICompatible`, aliases `Ollama`, `vLLM`, `Local`) | Whoever runs that server — **none** if it is your own hardware (vLLM, Ollama, LM Studio); the gateway / Azure AI Foundry operator otherwise | Platform-set `Llm:OpenAiCompatible:Endpoint`, http or https (`LlmSettings.cs:99-108`) | Platform key (optional) | Calls routed to it | Same as Claude |
| **RemoteAgent** (`RemoteAgent`, aliases `IDE`, `Remote`) | **None for AgentOS.NET.** The member's own `claude` / `codex` CLI runs on the member's machine under **the member's own subscription** and vendor terms | Paired runner over SignalR (`src/AgentOs.Modules.RemoteAgent/RemoteAgentLlmClient.cs:1-19`) | No server key — no vendor key crosses the wire (`IRemoteAgentBroker.cs:35-68`) | Every call routed to RemoteAgent | Server → member's machine: system + user prompt, model id, file manifest + attached ticket files. Then the CLI sends what it chooses to its vendor. Cost is recorded as $0 (not billed) |
| **Offline** | none | in-process | — | Development only (locked off in Production) | nothing leaves |

Notes:

- **No retention / region switches exist in code** (searched `retention|zdr|region`). Zero-data-retention,
  regional processing and training opt-outs are **vendor-account settings** — configure them in each vendor
  console / contract. For Azure OpenAI the resource region is the processing region.
- **Prompt caching** is metered (cache read / write tokens) but AgentOS.NET sends no explicit cache-control
  markers; any caching is the vendor's default behaviour.
- **Plugins** can register further providers (`LlmClientFactory.cs:148-163`) — they become sub-processors of the
  operator who installs them.

## 2. Other services

| Service | Sub-processor | Used for | When | What leaves | Code |
|---|---|---|---|---|---|
| **GitHub** (github.com or a GitHub Enterprise host on the allowlist) | GitHub (Microsoft) | Read tickets / issues / repo files / instruction files; push agent branches; open PRs, comments, issues | A workspace is connected and an agent runs | Generated code + tests, PR title/body, issue text, review comments; the tenant's PAT in the request header | `src/AgentOs.Modules.Integration/GitHubPrService.cs:127-170`; `Sources/GitHubSourceProvider.cs` |
| **Azure DevOps** (`dev.azure.com`, `*.visualstudio.com`) | Microsoft | Repos (boards not supported yet) | A workspace is connected | Same class as GitHub; PAT as Basic auth | `src/AgentOs.Modules.Integration/Sources/AzureDevOpsSourceProvider.cs`; `AzureDevOpsRestClient.cs:25-44` |
| **Azure** (Container Apps, Flexible Server for PostgreSQL, Key Vault, Log Analytics) | Microsoft | Hosting, database, connection-string secrets, container logs | When deployed with `azd up` | Everything the application stores (database), container stdout logs | `infra/`, [DEPLOYMENT.md](../DEPLOYMENT.md) |
| **Azure Key Vault** (optional) | Microsoft | Wrapping the DataProtection key ring | `DataProtection:KeyVaultKeyId` set | Wrap / unwrap calls on key material only | `src/AgentOs.Modules.AppConfig/DataProtectionExtensions.cs:82-87` |
| **E-mail (SMTP relay)** | Your SMTP provider | Tenant invitations (AgentOS.NET); Keycloak's own mail (verify e-mail, password reset) | Invitation sent; Keycloak flows | Recipient address, tenant id, role, accept link with token | `src/AgentOs.Modules.Tenants/Email/InvitationEmail.cs:12-39`; `infra/keycloak/agentic-realm.json:15-22` |
| **Keycloak** | none (self-hosted in your deployment) | Identity | always | — | `infra/keycloak` |
| **OTLP collector / Application Insights** (optional) | The collector's operator / Microsoft | Logs, metrics, traces | `OTEL_EXPORTER_OTLP_ENDPOINT` or `APPLICATIONINSIGHTS_CONNECTION_STRING` set | Log messages (masked for common key formats on the OTel path only), tenant id, trace id, HTTP metadata, LLM span attributes (model, tokens, cost — no prompt bodies) | `src/AgentOs.ServiceDefaults/Extensions.cs:79-100`; `src/AgentOs.Api/Program.cs:91-93` |
| **Upstream MCP servers** (optional) | Whoever runs the server | Extra agent tools | Configured in `Mcp:Servers` and called by an agent | Tool arguments chosen by the model | `src/AgentOs.Modules.Mcp/McpClientHost.cs` |
| **Container registry** (`mcr.microsoft.com`) | Microsoft | Pulling the build-sandbox SDK image | Build verifier in Container mode | Image pull only — the sandbox itself runs with `--network none` | `src/AgentOs.Modules.Integration/BuildVerifierOptions.cs:28-35` |

## 3. Keeping data on your own infrastructure

To process no customer content outside your boundary:

1. Route every agent to **OpenAI-compatible** pointing at a model you host, or to **RemoteAgent** if members'
   own subscriptions are acceptable to you (then the member's vendor is their sub-processor, not yours).
2. Use GitHub Enterprise Server / Azure DevOps Server on an allowlisted host (`Workspaces:AllowedHosts`).
3. Self-host Postgres, Keycloak, SMTP and the OTLP collector; leave `APPLICATIONINSIGHTS_CONNECTION_STRING` unset.
4. Wrap the key ring with a certificate you hold (`DataProtection:CertificatePath`) — see [hardening.md](hardening.md).

Keep this page in sync when a provider or integration is added (owner directive 10, "Docs stay true").
