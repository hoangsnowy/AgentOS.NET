// Ticket files (ADR-0015) — the server-driven (runner_shell) path. There the LLM loop runs on the server and drives the
// member's computer one shell command at a time, so there is no CLI run directory to drop the files in first. Instead
// the files go ahead of the loop as one "write_files" tool call: the runner makes a fresh folder under its work root,
// fetches the files (ReadRunFile, the same pull as a CLI run) into <folder>/.agentos/files/, re-checks every path and
// SHA-256, and answers with the folder's absolute path — which the agent's prompt then names. It passes the same
// approval gate as every runner tool call, and the delivery is recorded as evidence.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Files;

namespace AgentOs.Modules.RemoteAgent;

/// <inheritdoc cref="IRunnerFileDelivery"/>
internal sealed class RunnerFileDeliveryService(IRemoteAgentBroker broker, IServiceProvider services) : IRunnerFileDelivery
{
    /// <summary>The runner tool that writes ticket files (no shell involved).</summary>
    public const string ToolName = "write_files";

    /// <summary>Fetching and writing even the largest allowed set takes well under this.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<RunnerFileDelivery> DeliverAsync(string tenantId, string memberId, IReadOnlyList<RunFile> files, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) { return new RunnerFileDelivery(true, null, null); }
        var target = new RunnerTarget(tenantId, memberId ?? string.Empty);
        if (!broker.HasRunnerFor(target))
        {
            return new RunnerFileDelivery(false, null, "No computer is connected to receive the ticket's files.");
        }
        var id = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var call = new RunnerToolCall(id, id, ToolName, JsonSerializer.Serialize(new { files = RunFileEvidence.Manifest(files) }));
        broker.StageFiles(id, files);
        RunnerFileDelivery result;
        int delivered;
        try
        {
            var reply = await broker.DispatchToolCallAsync(call, target, Timeout, ct).ConfigureAwait(false);
            result = reply.Ok && !string.IsNullOrWhiteSpace(reply.JsonOutput)
                ? new RunnerFileDelivery(true, reply.JsonOutput.Trim(), null)
                : new RunnerFileDelivery(false, null, reply.Error ?? "The computer didn't say where it put the files.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            result = new RunnerFileDelivery(false, null, ex.Message);
        }
        finally
        {
            // How many files the runner fetched to the end is known once nothing more can be fetched.
            delivered = broker.UnstageFiles(id);
        }
        if (result.Ok && delivered != files.Count)
        {
            result = new RunnerFileDelivery(false, null, $"The computer fetched {delivered} of {files.Count} files.");
        }
        await RunFileEvidence.RecordAsync(services, tenantId, id, files, delivered,
            result.Folder is { } folder ? folder : "the member's computer", result.Error, started).ConfigureAwait(false);
        return result;
    }
}
