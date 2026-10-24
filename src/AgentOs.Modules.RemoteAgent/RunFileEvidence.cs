// Ticket files (ADR-0015) — governance: every delivery of ticket files to a member's computer is evidence, like a tool
// call. One entry per run (tool name "ticket_files"): which files (path, size, SHA-256), the run's request id, the
// session, and how many the runner fetched to the end. The task's live feed gets one line too. Best-effort — evidence
// never fails the run. Not an LLM call, so Insights (tokens, cost) is untouched.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Domain.Sessions;
using AgentOs.Domain.Tools;
using AgentOs.SharedKernel.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>Records a ticket-file delivery in the evidence log and on the task's feed.</summary>
internal static class RunFileEvidence
{
    /// <summary>The evidence entry's tool name.</summary>
    public const string ToolName = "ticket_files";

    /// <summary>The manifest the runner receives for <paramref name="files"/>.</summary>
    public static IReadOnlyList<RunFileEntry> Manifest(IReadOnlyList<RunFile> files) =>
        files.Select(f => new RunFileEntry(f.Path, f.Size, f.Sha256)).ToList();

    /// <summary>The evidence input: the run id and every file with its size and SHA-256.</summary>
    public static string Describe(string requestId, IReadOnlyList<RunFile> files) => JsonSerializer.Serialize(new
    {
        runId = requestId,
        files = files.Select(f => new { path = f.Path, size = f.Size, sha256 = f.Sha256 }),
    });

    public static async Task RecordAsync(IServiceProvider services, string tenantId, string requestId, IReadOnlyList<RunFile> files,
        int delivered, string where, string? error, DateTimeOffset started)
    {
        var sessionId = AmbientIdentity.Current?.SessionId;
        var ok = error is null && delivered == files.Count;
        var output = ok
            ? $"Delivered {delivered} of {files.Count} ticket file(s) to {where}."
            : $"Delivered {delivered} of {files.Count} ticket file(s) to {where}" + (error is null ? "." : $": {error}");
        try
        {
            if (services.GetService<IToolInvocationLog>() is { } log)
            {
                await log.AppendAsync(new ToolInvocationEvidence(requestId, ToolName, tenantId, null, Describe(requestId, files), output,
                    !ok, started, DateTimeOffset.UtcNow, sessionId?.ToString("D"))).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex; // evidence is best-effort: a lost entry never fails the run
        }
        if (sessionId is { } id && services.GetService<ISessionRunFeed>() is { } feed)
        {
            feed.Publish(new SessionRunEvent(tenantId, id, SessionRunEventKind.Step,
                ok ? $"The agent had the {files.Count} file(s) attached to this ticket in its working folder."
                   : $"Only {delivered} of {files.Count} file(s) attached to this ticket reached the computer"
                     + (error is null ? " — if this keeps happening, update the AgentOS.NET helper on it." : $": {error}"),
                DateTimeOffset.UtcNow));
        }
    }
}
