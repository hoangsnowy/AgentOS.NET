// Epic E2 — Adapter that exposes a Domain.Tools.ITool as a Microsoft.Extensions.AI AIFunction so
// FunctionInvokingChatClient can route LLM-emitted tool_use blocks back into the registered ITool.
// The AIFunction surface (Name, Description, JsonSchema) comes straight from ToolDefinition; the
// InvokeCoreAsync override serializes the model-emitted arguments back into the ITool's string
// Input and runs it through the shared IToolGateway (policy gate + evidence), returning the
// textual Output for the next LLM turn. All governance lives in the gateway (M1) so the
// remote-session path enforces the same policy + evidence. This adapter is model-facing, so its gateway
// also shapes the result for the next turn (2.7, ToolGatewayResult.ForModel); the evidence keeps the full output.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using Microsoft.Extensions.AI;

namespace AgentOs.Modules.Llm;

internal sealed class AIToolFunction : AIFunction
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ITool _tool;
    private readonly string _tenantId;
    private readonly string? _runId;
    private readonly JsonElement _schema;
    // Concrete type (not IToolGateway): this instance is constructed locally, not injected, so the
    // concrete type avoids an interface dispatch (CA1859). DI consumers (M4) still use IToolGateway.
    private readonly DefaultToolGateway _gateway;

    public AIToolFunction(ITool tool, string tenantId, string? runId)
        : this(tool, tenantId, runId, policy: null, log: null)
    {
    }

    public AIToolFunction(ITool tool, string tenantId, string? runId, IToolPolicy? policy, IToolInvocationLog? log)
        : this(tool, tenantId, runId, policy, log, settings: null)
    {
    }

    public AIToolFunction(
        ITool tool, string tenantId, string? runId, IToolPolicy? policy, IToolInvocationLog? log,
        AgentOs.Domain.Configuration.ISettings? settings)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tool = tool;
        _tenantId = string.IsNullOrWhiteSpace(tenantId) ? "anonymous" : tenantId;
        _runId = runId;
        // All policy + evidence enforcement lives in the gateway so every path (here + the remote
        // session executor) governs identically.
        // This adapter feeds the model, so the gateway shapes the result (2.7); its evidence keeps the full output.
        _gateway = new DefaultToolGateway(policy, log, settings, shapeForModel: true);

        var def = tool.Definition;
        def.Validate();
        // ToolDefinition stores schema as a string for transport; parse it once at construction.
        _schema = JsonDocument.Parse(def.JsonInputSchema).RootElement.Clone();
    }

    public override string Name => _tool.Definition.Name;
    public override string Description => _tool.Definition.Description;
    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var inputDict = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var kvp in arguments)
        {
            inputDict[kvp.Key] = kvp.Value;
        }

        var inputJson = JsonSerializer.Serialize(inputDict, SerializerOptions);
        var request = new ToolInvocationRequest(
            ToolName: _tool.Definition.Name,
            CallId: Guid.NewGuid().ToString("N"),
            Input: inputJson,
            TenantId: _tenantId,
            RunId: _runId);

        var result = await _gateway.InvokeAsync(_tool, request, cancellationToken).ConfigureAwait(false);
        return result.ForModel;
    }
}
