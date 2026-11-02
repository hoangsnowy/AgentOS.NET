// GAP-04: work in flight on a runner that was revoked (its pairing revoked, its member removed, its tenant purged) fails
// at once with this — a plain-language reason the Board shows — instead of hanging until the dispatch timeout. It is an
// InvalidOperationException so every caller that already handles "no runner" (RemoteAgentLlmClient, RunnerShellTool)
// reports it the same way.

using System;

namespace AgentOs.Modules.RemoteAgent;

/// <summary>The runner a request or tool call was dispatched to was revoked before it answered.</summary>
public sealed class RunnerRevokedException : InvalidOperationException
{
    /// <summary>With the default plain-language reason.</summary>
    public RunnerRevokedException() : base(InProcessRemoteAgentBroker.RevokedMessage) { }

    /// <summary>With <paramref name="message"/> as the reason.</summary>
    public RunnerRevokedException(string message) : base(message) { }

    /// <summary>With <paramref name="message"/> as the reason and the exception that caused it.</summary>
    public RunnerRevokedException(string message, Exception innerException) : base(message, innerException) { }
}
