// M1 — durable IToolInvocationLog. The gateway that writes evidence is a singleton, so this resolves a
// per-operation DI scope (like EfAppConfigStore) to reach the scoped ToolsDbContext. Appends are
// best-effort: a persistence failure must never break the tool call, so it is swallowed + logged.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.Tools.Persistence;
using AgentOs.Modules.Tools.Persistence.Entities;
using AgentOs.SharedKernel.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tools.Evidence;

internal sealed class EfToolInvocationLog : IToolInvocationLog
{
    private readonly IServiceProvider _rootProvider;
    private readonly ILogger<EfToolInvocationLog> _logger;
    private readonly INpgsqlConnectionFactory? _connectionFactory;

    public EfToolInvocationLog(
        IServiceProvider rootProvider,
        ILogger<EfToolInvocationLog> logger,
        INpgsqlConnectionFactory? connectionFactory = null)
    {
        _rootProvider = rootProvider;
        _logger = logger;
        _connectionFactory = connectionFactory;
    }

    public async Task AppendAsync(ToolInvocationEvidence entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await using var scope = _rootProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToolsDbContext>();
            db.ToolInvocations.Add(new ToolInvocationEvidenceEntity
            {
                Id = Guid.NewGuid(),
                CallId = entry.CallId,
                ToolName = entry.ToolName,
                TenantId = entry.TenantId,
                RunId = entry.RunId,
                SessionId = entry.SessionId,
                Input = entry.Input,
                Output = entry.Output,
                IsError = entry.IsError,
                StartedUtc = entry.StartedUtc,
                FinishedUtc = entry.FinishedUtc,
                OutputChars = entry.OutputChars,
                ShapedOutputChars = entry.ShapedOutputChars,
                MaxOutputTokens = entry.MaxOutputTokens,
                Origin = entry.Origin,
                AgentRole = entry.AgentRole,
                RunnerId = entry.RunnerId,
                PolicySource = entry.PolicySource,
                Denied = entry.Denied,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) { Handle(ex); }
        catch (System.Data.Common.DbException ex) { Handle(ex); }
        catch (InvalidOperationException ex) { Handle(ex); }

        // Evidence is best-effort — never break the tool call on a persistence failure.
        void Handle(Exception e) =>
            _logger.LogWarning(e, "Failed to persist tool-invocation evidence for {ToolName}", entry.ToolName);
    }

    public async Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Array.Empty<ToolInvocationEvidence>();
        }

        if (_connectionFactory is not null)
        {
            return await ListRecentViaDapperAsync(tenantId, limit, cancellationToken).ConfigureAwait(false);
        }

        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToolsDbContext>();
        var rows = await db.ToolInvocations
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.StartedUtc)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToEvidence).ToList();
    }

    // Dapper fast-path: tool_invocations under the "tools" schema; PascalCase columns are quoted and
    // map straight back onto the entity, then to the domain evidence record. The (TenantId, StartedUtc)
    // index serves the filter + ordering.
    private async Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentViaDapperAsync(
        string tenantId, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT "CallId", "ToolName", "TenantId", "RunId", "SessionId",
                   "Input", "Output", "IsError", "StartedUtc", "FinishedUtc",
                   "OutputChars", "ShapedOutputChars", "MaxOutputTokens",
                   "Origin", "AgentRole", "RunnerId", "PolicySource", "Denied"
            FROM tools.tool_invocations
            WHERE "TenantId" = @tenantId
            ORDER BY "StartedUtc" DESC
            LIMIT @limit
            """;

        await using var conn = await _connectionFactory!.OpenWithTenantAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ToolInvocationEvidenceEntity>(
            new CommandDefinition(sql, new { tenantId, limit = Math.Max(1, limit) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToEvidence).ToList();
    }

    /// <summary>EF LINQ (its RLS interceptor applies): the recent rows of one origin and/or one session.</summary>
    public async Task<IReadOnlyList<ToolInvocationEvidence>> ListRecentAsync(
        string tenantId, ToolEvidenceFilter filter, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Array.Empty<ToolInvocationEvidence>();
        }
        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToolsDbContext>();
        var query = db.ToolInvocations.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (filter.Origin is { } origin) { query = query.Where(x => x.Origin == origin); }
        if (filter.SessionId is { } sessionId) { query = query.Where(x => x.SessionId == sessionId); }
        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToEvidence).ToList();
    }

    /// <summary>One grouped query: per agent role, the distinct runs, the calls and the denials made on members' computers.</summary>
    public async Task<IReadOnlyList<ToolCallsByRole>> SummarizeMemberComputerAsync(
        string tenantId, DateTimeOffset? sinceUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Array.Empty<ToolCallsByRole>();
        }
        await using var scope = _rootProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToolsDbContext>();
        var query = db.ToolInvocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Origin == ToolEvidenceOrigins.MemberComputer);
        if (sinceUtc is { } since) { query = query.Where(x => x.StartedUtc >= since); }
        var groups = await query
            .GroupBy(x => x.AgentRole ?? string.Empty)
            .Select(g => new
            {
                Role = g.Key,
                Runs = g.Select(x => x.RunId ?? x.CallId).Distinct().Count(),
                Calls = g.Count(),
                Denials = g.Count(x => x.Denied),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return groups
            .Select(g => new ToolCallsByRole(g.Role, g.Runs, g.Calls, g.Denials))
            .OrderByDescending(g => g.ToolCalls).ThenBy(g => g.AgentRole, StringComparer.Ordinal)
            .ToList();
    }

    private static ToolInvocationEvidence ToEvidence(ToolInvocationEvidenceEntity x) => new(
        x.CallId, x.ToolName, x.TenantId, x.RunId, x.Input, x.Output, x.IsError,
        x.StartedUtc, x.FinishedUtc, x.SessionId, x.OutputChars, x.ShapedOutputChars, x.MaxOutputTokens)
    {
        Origin = x.Origin,
        AgentRole = x.AgentRole,
        RunnerId = x.RunnerId,
        PolicySource = x.PolicySource,
        Denied = x.Denied,
    };
}
