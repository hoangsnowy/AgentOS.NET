// EF Core impl: saves PipelineResult (jsonb) + RunMetric rows, reads back + lists summaries.
// Writes stamp TenantId from ITenantContext; reads are filtered by the DbContext's global query
// filter so a request only ever sees its own tenant's runs. The cost summary additionally takes a
// Dapper fast-path (server-side GROUP BY) when a real Npgsql connection is wired; the EF stream-fold
// remains the fallback for the in-memory test provider / no-DB boot (where connectionFactory is null).
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using AgentOs.Modules.Pipeline.Metrics;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Domain.Pipeline;
using AgentOs.Modules.Pipeline.Persistence.Entities;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Pipeline.Persistence.Repositories;

internal sealed class PipelineRunRepository(
    PipelineDbContext db,
    ITenantContext tenant,
    INpgsqlConnectionFactory? connectionFactory = null) : IPipelineRunRepository
{
    // Ambient-tenant write: the API request path has a populated ITenantContext, so stamp from it.
    public Task SaveAsync(PipelineRunRecord record, CancellationToken ct = default)
        => SaveAsync(record, tenant.TenantId, ct);

    // Tenant-explicit write: the caller (e.g. a Blazor circuit, which has a blank ITenantContext) supplies
    // the owning tenant directly, so the run + its run_metrics rows are billed to the right tenant.
    public async Task SaveAsync(PipelineRunRecord record, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var result = record.Result;

        var entity = new PipelineRunEntity
        {
            Id = record.Id,
            TenantId = tenantId,
            UserStoryText = result.UserStory.Description,
            Status = result.Status.ToString(),
            TotalCostUsd = result.TotalMetrics.CostUsd,
            TotalTokensIn = result.TotalMetrics.InputTokens,
            TotalTokensOut = result.TotalMetrics.OutputTokens,
            IterationCount = result.IterationCount,
            CreatedAtUtc = record.CreatedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            ResultJson = JsonSerializer.Serialize(result, PersistenceJson.Options),
        };

        foreach (var m in record.Metrics)
        {
            entity.Metrics.Add(new RunMetricEntity
            {
                RunId = record.Id,
                TenantId = tenantId,
                KcId = m.KcId,
                Iteration = m.Iteration,
                AgentName = m.AgentName,
                Model = m.Model,
                Provider = m.Provider,
                TokensIn = m.TokensIn,
                TokensOut = m.TokensOut,
                CacheReadTokens = m.CacheReadTokens,
                CacheWriteTokens = m.CacheWriteTokens,
                ReasoningTokens = m.ReasoningTokens,
                LatencyMs = m.LatencyMs,
                CostUsd = m.CostUsd,
                Success = m.Success,
                ErrorMessage = m.ErrorMessage,
                TimestampUtc = m.Timestamp,
            });
        }

        db.PipelineRuns.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveAgentRunAsync(AgentRunRecord record, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var entity = new PipelineRunEntity
        {
            Id = record.Id,
            TenantId = tenantId,
            UserStoryText = record.Label,
            Status = record.Success ? "Completed" : "Failed",
            TotalCostUsd = record.Calls.Sum(c => c.CostUsd),
            TotalTokensIn = record.Calls.Sum(c => c.InputTokens),
            TotalTokensOut = record.Calls.Sum(c => c.OutputTokens),
            IterationCount = 0,
            CreatedAtUtc = record.StartedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            // No pipeline artifacts: the run detail deserializes this to null and reads as not found.
            ResultJson = "null",
        };
        foreach (var c in record.Calls)
        {
            entity.Metrics.Add(new RunMetricEntity
            {
                RunId = record.Id,
                TenantId = tenantId,
                KcId = "board",
                Iteration = 0,
                AgentName = string.IsNullOrWhiteSpace(c.AgentRole) ? record.Agent : c.AgentRole,
                Model = c.Model,
                Provider = c.Provider,
                TokensIn = c.InputTokens,
                TokensOut = c.OutputTokens,
                CacheReadTokens = c.CacheReadTokens,
                CacheWriteTokens = c.CacheWriteTokens,
                ReasoningTokens = c.ReasoningTokens,
                LatencyMs = c.Latency.TotalMilliseconds,
                CostUsd = c.CostUsd,
                Success = c.Success,
                ErrorMessage = c.Error,
                TimestampUtc = c.At,
            });
        }
        db.PipelineRuns.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PipelineRunRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await db.PipelineRuns
            .Include(x => x.Metrics)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null)
        {
            return null;
        }

        var result = JsonSerializer.Deserialize<PipelineResult>(entity.ResultJson, PersistenceJson.Options);
        if (result is null)
        {
            return null;
        }

        var metrics = entity.Metrics
            .OrderBy(m => m.Id)
            .Select(m => new RunMetric(
                entity.Id.ToString(),
                m.KcId,
                m.Iteration,
                m.AgentName,
                m.Model,
                m.Provider,
                m.TokensIn,
                m.TokensOut,
                m.LatencyMs,
                m.CostUsd,
                m.Success,
                m.ErrorMessage,
                m.TimestampUtc,
                m.CacheReadTokens,
                m.CacheWriteTokens,
                m.ReasoningTokens))
            .ToList();

        return new PipelineRunRecord(
            entity.Id,
            result,
            metrics,
            entity.CreatedAtUtc,
            entity.CompletedAtUtc ?? entity.CreatedAtUtc);
    }

    public async Task<IReadOnlyList<PipelineRunSummary>> ListAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        return await db.PipelineRuns
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .Select(x => new PipelineRunSummary(
                x.Id,
                x.Status,
                x.TotalCostUsd,
                x.IterationCount,
                x.CreatedAtUtc,
                x.CompletedAtUtc,
                x.UserStoryText.Length > 120 ? x.UserStoryText.Substring(0, 120) : x.UserStoryText))
            .ToListAsync(ct);
    }

    // Tenant-explicit list: bypass the ITenantContext-driven global query filter (a Blazor circuit has no
    // HttpContext, so ITenantContext is blank) and scope to the tenant the caller passed in. Mirrors
    // GetCostSummaryForTenantAsync — Dapper fast-path when a real Npgsql connection is wired, EF fallback
    // for the in-memory test provider / no-DB boot — so the desktop Overview / run history is correct.
    public async Task<IReadOnlyList<PipelineRunSummary>> ListForTenantAsync(
        string tenantId, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (connectionFactory is not null)
        {
            return await ListForTenantViaDapperAsync(tenantId, limit, offset, ct).ConfigureAwait(false);
        }

        return await db.PipelineRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .Select(x => new PipelineRunSummary(
                x.Id,
                x.Status,
                x.TotalCostUsd,
                x.IterationCount,
                x.CreatedAtUtc,
                x.CompletedAtUtc,
                x.UserStoryText.Length > 120 ? x.UserStoryText.Substring(0, 120) : x.UserStoryText))
            .ToListAsync(ct);
    }

    // Dapper fast-path for the tenant run list: one round-trip, no EF materialization. PascalCase columns
    // are quoted (no snake_case convention); LEFT(..,120) mirrors the EF substring preview.
    private async Task<IReadOnlyList<PipelineRunSummary>> ListForTenantViaDapperAsync(
        string tenantId, int limit, int offset, CancellationToken ct)
    {
        const string sql = """
            SELECT
                "Id"             AS Id,
                "Status"         AS Status,
                "TotalCostUsd"   AS TotalCostUsd,
                "IterationCount" AS IterationCount,
                "CreatedAtUtc"   AS CreatedAtUtc,
                "CompletedAtUtc" AS CompletedAtUtc,
                LEFT("UserStoryText", 120) AS UserStoryPreview
            FROM pipeline.pipeline_runs
            WHERE "TenantId" = @tenantId
            ORDER BY "CreatedAtUtc" DESC, "Id" DESC
            OFFSET @offset LIMIT @limit
            """;

        await using var conn = await connectionFactory!.OpenWithTenantAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<RunSummaryRow>(
            new CommandDefinition(sql, new { tenantId, limit, offset }, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => new PipelineRunSummary(
            r.Id, r.Status, r.TotalCostUsd, r.IterationCount, r.CreatedAtUtc, r.CompletedAtUtc, r.UserStoryPreview))];
    }

    public async Task<CostSummary> GetCostSummaryForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        // Fast path: let Postgres do the aggregation in one round-trip instead of streaming every
        // metric row to the app and folding it here. Only available when a real connection is wired.
        if (connectionFactory is not null)
        {
            return await GetCostSummaryViaDapperAsync(tenantId, since, ct).ConfigureAwait(false);
        }

        // Tenant-explicit: bypass the ITenantContext-driven global query filter (a Blazor circuit has
        // no HttpContext, so ITenantContext is blank) and scope to the tenant the caller passed in.
        var q = db.RunMetrics
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId);
        if (since is { } cutoff)
        {
            q = q.Where(m => m.TimestampUtc >= cutoff);
        }

        // Stream the (tenant-, since-) filtered rows once and fold them into small per-key
        // accumulators, instead of materializing every row into a List and then grouping it four ways
        // in memory. Memory is O(distinct keys), not O(rows); the (TenantId, TimestampUtc) index serves
        // the filter. We project to the cost columns only (not the row entity / its JSON blob), and
        // bucket by agent / provider / model / day in a single pass. (Pushing the GROUP BY fully into
        // SQL would also cut transfer, but EF's GROUP BY translation isn't portable to the in-memory
        // test provider — this keeps the logic provider-agnostic.)
        var byAgent = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var byProvider = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var byModel = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var byDay = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var runIds = new HashSet<Guid>();
        var total = default(Acc);

        var stream = q
            .Select(m => new
            {
                m.RunId,
                m.AgentName,
                m.Provider,
                m.Model,
                m.TokensIn,
                m.TokensOut,
                m.CacheReadTokens,
                m.CacheWriteTokens,
                m.CostUsd,
                m.TimestampUtc,
            })
            .AsAsyncEnumerable();

        await foreach (var m in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            total.Add(m.CostUsd, m.TokensIn, m.TokensOut, m.CacheReadTokens, m.CacheWriteTokens);
            runIds.Add(m.RunId);
            Bump(byAgent, m.AgentName, m.CostUsd, m.TokensIn, m.TokensOut, m.CacheReadTokens, m.CacheWriteTokens);
            Bump(byProvider, m.Provider, m.CostUsd, m.TokensIn, m.TokensOut, m.CacheReadTokens, m.CacheWriteTokens);
            Bump(byModel, m.Model, m.CostUsd, m.TokensIn, m.TokensOut, m.CacheReadTokens, m.CacheWriteTokens);
            var day = m.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Bump(byDay, day, m.CostUsd, m.TokensIn, m.TokensOut, m.CacheReadTokens, m.CacheWriteTokens);
        }

        if (total.Calls == 0)
        {
            return CostSummary.Empty;
        }

        return new CostSummary(
            total.CostUsd, total.TokensIn, total.TokensOut, total.Calls, runIds.Count,
            ByCostDesc(byAgent), ByCostDesc(byProvider), ByCostDesc(byModel),
            [.. byDay.Select(kv => kv.Value.ToBucket(kv.Key)).OrderBy(b => b.Key, StringComparer.Ordinal)])
        {
            TotalCacheReadTokens = total.CacheReadTokens,
            TotalCacheWriteTokens = total.CacheWriteTokens,
        };
    }

    public async Task<decimal> GetSpendForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Fast path: a single server-side SUM, index-covered by (TenantId, TimestampUtc). The budget gate
        // needs only this number, so it skips the four extra GROUP BY round-trips the full cost summary does.
        if (connectionFactory is not null)
        {
            var sql = $"""SELECT COALESCE(SUM("CostUsd"), 0) FROM pipeline.run_metrics {CostWhere}""";
            await using var conn = await connectionFactory.OpenWithTenantAsync(ct).ConfigureAwait(false);
            return await conn.ExecuteScalarAsync<decimal>(
                new CommandDefinition(sql, new { tenantId, since }, cancellationToken: ct)).ConfigureAwait(false);
        }

        // Tenant-explicit EF fallback (in-memory test provider / no-DB boot): bypass the ITenantContext-driven
        // global query filter (blank on a Blazor circuit) and let the provider sum.
        var q = db.RunMetrics.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == tenantId);
        if (since is { } cutoff)
        {
            q = q.Where(m => m.TimestampUtc >= cutoff);
        }
        return await q.SumAsync(m => m.CostUsd, ct).ConfigureAwait(false);
    }

    public async Task<InsightsExtra> GetInsightsExtraForTenantAsync(
        string tenantId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Fast path: three server-side GROUP BYs — latency-by-agent + iteration distribution over
        // run_metrics, runs-per-day over pipeline_runs — each index-covered. One round-trip each.
        if (connectionFactory is not null)
        {
            return await GetInsightsExtraViaDapperAsync(tenantId, since, ct).ConfigureAwait(false);
        }

        // Tenant-explicit EF fallback (in-memory test provider / no-DB): fold the filtered rows once,
        // bypassing the blank-in-a-Blazor-circuit global query filter. Memory is O(distinct keys).
        var metrics = db.RunMetrics.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == tenantId);
        if (since is { } cutoff)
        {
            metrics = metrics.Where(m => m.TimestampUtc >= cutoff);
        }

        var latSum = new Dictionary<string, double>(StringComparer.Ordinal);
        var latCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        var iterCalls = new Dictionary<int, int>();
        var iterRuns = new Dictionary<int, HashSet<Guid>>();

        await foreach (var m in metrics
            .Select(m => new { m.RunId, m.AgentName, m.LatencyMs, m.Iteration })
            .AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            latSum[m.AgentName] = latSum.GetValueOrDefault(m.AgentName) + m.LatencyMs;
            latCalls[m.AgentName] = latCalls.GetValueOrDefault(m.AgentName) + 1;
            iterCalls[m.Iteration] = iterCalls.GetValueOrDefault(m.Iteration) + 1;
            if (!iterRuns.TryGetValue(m.Iteration, out var set))
            {
                set = [];
                iterRuns[m.Iteration] = set;
            }
            set.Add(m.RunId);
        }

        var runsQ = db.PipelineRuns.IgnoreQueryFilters().AsNoTracking().Where(r => r.TenantId == tenantId);
        if (since is { } cut2)
        {
            runsQ = runsQ.Where(r => r.CreatedAtUtc >= cut2);
        }
        var runsByDay = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var created in runsQ.Select(r => r.CreatedAtUtc)
            .AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            var day = created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            runsByDay[day] = runsByDay.GetValueOrDefault(day) + 1;
        }

        return new InsightsExtra(
            [.. latCalls.Select(kv => new AgentLatencyBucket(
                    kv.Key, kv.Value == 0 ? 0 : latSum[kv.Key] / kv.Value, kv.Value))
                .OrderByDescending(b => b.Calls)],
            [.. iterCalls.Select(kv => new IterationBucket(
                    kv.Key, iterRuns.TryGetValue(kv.Key, out var s) ? s.Count : 0, kv.Value))
                .OrderBy(b => b.Iteration)],
            [.. runsByDay.Select(kv => new DayRunsBucket(kv.Key, kv.Value))
                .OrderBy(b => b.Day, StringComparer.Ordinal)]);
    }

    private static void Bump(Dictionary<string, Acc> map, string key, decimal cost, int tokensIn, int tokensOut, int cacheRead, int cacheWrite)
    {
        // CollectionsMarshal would avoid the double lookup, but a plain get/set keeps it simple; the
        // map is keyed by a bounded set (agent / provider / model names, or days).
        map.TryGetValue(key, out var acc);
        acc.Add(cost, tokensIn, tokensOut, cacheRead, cacheWrite);
        map[key] = acc;
    }

    private static List<CostBucket> ByCostDesc(Dictionary<string, Acc> map) =>
        [.. map.Select(kv => kv.Value.ToBucket(kv.Key)).OrderByDescending(b => b.CostUsd)];

    // Mutable fold accumulator for one bucket (or the grand total).
    private struct Acc
    {
        public decimal CostUsd;
        public int TokensIn;
        public int TokensOut;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public int Calls;

        public void Add(decimal cost, int tokensIn, int tokensOut, int cacheRead, int cacheWrite)
        {
            CostUsd += cost;
            TokensIn += tokensIn;
            TokensOut += tokensOut;
            CacheReadTokens += cacheRead;
            CacheWriteTokens += cacheWrite;
            Calls++;
        }

        public readonly CostBucket ToBucket(string key) => new(key, CostUsd, TokensIn, TokensOut, Calls, CacheReadTokens, CacheWriteTokens);
    }

    // ---- Dapper fast-path (server-side aggregation) ----

    // run_metrics is the unqualified table under the default "pipeline" schema; columns keep their
    // PascalCase property names (no snake_case convention), so they must be quoted in raw SQL. The
    // @since param is cast to timestamptz so Postgres can type a null parameter in the IS NULL guard.
    private const string CostWhere =
        """WHERE "TenantId" = @tenantId AND (@since::timestamptz IS NULL OR "TimestampUtc" >= @since::timestamptz)""";

    private async Task<CostSummary> GetCostSummaryViaDapperAsync(
        string tenantId, DateTimeOffset? since, CancellationToken ct)
    {
        var parms = new { tenantId, since };

        await using var conn = await connectionFactory!.OpenWithTenantAsync(ct).ConfigureAwait(false);

        var totalsSql = $"""
            SELECT
                COALESCE(SUM("CostUsd"), 0)   AS CostUsd,
                COALESCE(SUM("TokensIn"), 0)::int  AS TokensIn,
                COALESCE(SUM("TokensOut"), 0)::int AS TokensOut,
                COALESCE(SUM("CacheReadTokens"), 0)::bigint  AS CacheReadTokens,
                COALESCE(SUM("CacheWriteTokens"), 0)::bigint AS CacheWriteTokens,
                COUNT(*)::int                 AS Calls,
                COUNT(DISTINCT "RunId")::int  AS RunCount
            FROM pipeline.run_metrics
            {CostWhere}
            """;
        var totals = await conn.QuerySingleAsync<TotalsRow>(
            new CommandDefinition(totalsSql, parms, cancellationToken: ct)).ConfigureAwait(false);
        if (totals.Calls == 0)
        {
            return CostSummary.Empty;
        }

        var byAgent = await GroupByColumnAsync(conn, "AgentName", parms, ct).ConfigureAwait(false);
        var byProvider = await GroupByColumnAsync(conn, "Provider", parms, ct).ConfigureAwait(false);
        var byModel = await GroupByColumnAsync(conn, "Model", parms, ct).ConfigureAwait(false);
        var byDay = await GroupByDayAsync(conn, parms, ct).ConfigureAwait(false);

        return new CostSummary(
            totals.CostUsd, totals.TokensIn, totals.TokensOut, totals.Calls, totals.RunCount,
            byAgent, byProvider, byModel, byDay)
        {
            TotalCacheReadTokens = totals.CacheReadTokens,
            TotalCacheWriteTokens = totals.CacheWriteTokens,
        };
    }

    private static async Task<List<CostBucket>> GroupByColumnAsync(
        DbConnection conn, string column, object parms, CancellationToken ct)
    {
        // `column` is one of three compile-time constants below — never user input — so the
        // interpolation is not a SQL-injection vector; the tenant/since values stay parameterized.
        var sql = $"""
            SELECT
                "{column}"          AS Key,
                SUM("CostUsd")      AS CostUsd,
                SUM("TokensIn")::int  AS TokensIn,
                SUM("TokensOut")::int AS TokensOut,
                SUM("CacheReadTokens")::bigint  AS CacheReadTokens,
                SUM("CacheWriteTokens")::bigint AS CacheWriteTokens,
                COUNT(*)::int       AS Calls
            FROM pipeline.run_metrics
            {CostWhere}
            GROUP BY "{column}"
            ORDER BY SUM("CostUsd") DESC, "{column}"
            """;
        var rows = await conn.QueryAsync<BucketRow>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToBucket())];
    }

    private static async Task<List<CostBucket>> GroupByDayAsync(
        DbConnection conn, object parms, CancellationToken ct)
    {
        // Day bucket = UTC calendar day, matching the EF fold's TimestampUtc.UtcDateTime "yyyy-MM-dd".
        var sql = $"""
            SELECT
                to_char("TimestampUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD') AS Key,
                SUM("CostUsd")      AS CostUsd,
                SUM("TokensIn")::int  AS TokensIn,
                SUM("TokensOut")::int AS TokensOut,
                SUM("CacheReadTokens")::bigint  AS CacheReadTokens,
                SUM("CacheWriteTokens")::bigint AS CacheWriteTokens,
                COUNT(*)::int       AS Calls
            FROM pipeline.run_metrics
            {CostWhere}
            GROUP BY to_char("TimestampUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD')
            ORDER BY Key
            """;
        var rows = await conn.QueryAsync<BucketRow>(
            new CommandDefinition(sql, parms, cancellationToken: ct)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToBucket())];
    }

    private sealed class TotalsRow
    {
        public decimal CostUsd { get; init; }
        public int TokensIn { get; init; }
        public int TokensOut { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
        public int Calls { get; init; }
        public int RunCount { get; init; }
    }

    private sealed class BucketRow
    {
        public string Key { get; init; } = string.Empty;
        public decimal CostUsd { get; init; }
        public int TokensIn { get; init; }
        public int TokensOut { get; init; }
        public long CacheReadTokens { get; init; }
        public long CacheWriteTokens { get; init; }
        public int Calls { get; init; }

        public CostBucket ToBucket() => new(Key, CostUsd, TokensIn, TokensOut, Calls, CacheReadTokens, CacheWriteTokens);
    }

    // Dapper row for ListForTenantViaDapperAsync — column names match the SELECT aliases.
    private sealed class RunSummaryRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = string.Empty;
        public decimal TotalCostUsd { get; init; }
        public int IterationCount { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; init; }
        public string UserStoryPreview { get; init; } = string.Empty;
    }

    // ---- Insights extras (Dapper fast-path) ----

    private async Task<InsightsExtra> GetInsightsExtraViaDapperAsync(
        string tenantId, DateTimeOffset? since, CancellationToken ct)
    {
        var parms = new { tenantId, since };
        await using var conn = await connectionFactory!.OpenWithTenantAsync(ct).ConfigureAwait(false);

        var latencySql = $"""
            SELECT "AgentName" AS Agent, AVG("LatencyMs") AS AvgLatencyMs, COUNT(*)::int AS Calls
            FROM pipeline.run_metrics
            {CostWhere}
            GROUP BY "AgentName"
            ORDER BY COUNT(*) DESC, "AgentName"
            """;
        var latency = (await conn.QueryAsync<AgentLatencyRow>(
                new CommandDefinition(latencySql, parms, cancellationToken: ct)).ConfigureAwait(false))
            .Select(r => new AgentLatencyBucket(r.Agent, r.AvgLatencyMs, r.Calls)).ToList();

        var iterSql = $"""
            SELECT "Iteration" AS Iteration, COUNT(DISTINCT "RunId")::int AS Runs, COUNT(*)::int AS Calls
            FROM pipeline.run_metrics
            {CostWhere}
            GROUP BY "Iteration"
            ORDER BY "Iteration"
            """;
        var iterations = (await conn.QueryAsync<IterationRow>(
                new CommandDefinition(iterSql, parms, cancellationToken: ct)).ConfigureAwait(false))
            .Select(r => new IterationBucket(r.Iteration, r.Runs, r.Calls)).ToList();

        // Runs-per-day is over pipeline_runs (distinct runs, not metric rows), keyed on CreatedAtUtc.
        const string runsSql = """
            SELECT to_char("CreatedAtUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD') AS Day, COUNT(*)::int AS Runs
            FROM pipeline.pipeline_runs
            WHERE "TenantId" = @tenantId AND (@since::timestamptz IS NULL OR "CreatedAtUtc" >= @since::timestamptz)
            GROUP BY to_char("CreatedAtUtc" AT TIME ZONE 'UTC', 'YYYY-MM-DD')
            ORDER BY Day
            """;
        var runsByDay = (await conn.QueryAsync<DayRunsRow>(
                new CommandDefinition(runsSql, parms, cancellationToken: ct)).ConfigureAwait(false))
            .Select(r => new DayRunsBucket(r.Day, r.Runs)).ToList();

        return new InsightsExtra(latency, iterations, runsByDay);
    }

    private sealed class AgentLatencyRow
    {
        public string Agent { get; init; } = string.Empty;
        public double AvgLatencyMs { get; init; }
        public int Calls { get; init; }
    }

    private sealed class IterationRow
    {
        public int Iteration { get; init; }
        public int Runs { get; init; }
        public int Calls { get; init; }
    }

    private sealed class DayRunsRow
    {
        public string Day { get; init; } = string.Empty;
        public int Runs { get; init; }
    }
}
