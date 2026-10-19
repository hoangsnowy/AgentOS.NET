// EF Core DbContext for the Pipeline module. Owns 4 tables under schema `pipeline`:
// pipeline_runs (run + artifact jsonb), run_metrics (1 LLM call), orchestrations (AgentOS.NET
// orchestration state), scheduled_runs (cron / one-off pipeline runs). Every entity carries a TenantId column + a global query filter (reads ITenantContext)
// so each request only ever sees rows owned by its own tenant.

using AgentOs.Modules.Pipeline.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Pipeline.Persistence;

internal sealed class PipelineDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public PipelineDbContext(DbContextOptions<PipelineDbContext> options, ITenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<PipelineRunEntity> PipelineRuns => Set<PipelineRunEntity>();

    public DbSet<RunMetricEntity> RunMetrics => Set<RunMetricEntity>();

    public DbSet<OrchestrationEntity> Orchestrations => Set<OrchestrationEntity>();

    public DbSet<ScheduledRunEntity> ScheduledRuns => Set<ScheduledRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("pipeline");

        modelBuilder.Entity<PipelineRunEntity>(e =>
        {
            e.ToTable("pipeline_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.UserStoryText).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.Property(x => x.TotalCostUsd).HasPrecision(18, 6);
            e.Property(x => x.ResultJson).HasColumnType("jsonb").IsRequired();
            // The run list is always (TenantId =, ORDER BY CreatedAtUtc DESC, Id DESC) — both the global
            // query filter path (ListAsync) and the tenant-explicit Dapper/EF path (ListForTenantAsync).
            // One composite makes that a sort-free backward index walk (LIMIT short-circuits), and its
            // leftmost prefix covers plain TenantId lookups — so no separate TenantId/CreatedAtUtc index.
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc, x.Id });
            e.HasMany(x => x.Metrics)
             .WithOne()
             .HasForeignKey(m => m.RunId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<RunMetricEntity>(e =>
        {
            e.ToTable("run_metrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.KcId).HasMaxLength(32).IsRequired();
            e.Property(x => x.AgentName).HasMaxLength(64).IsRequired();
            e.Property(x => x.Model).HasMaxLength(128).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasIndex(x => x.RunId);
            // Composite supports the cost summary's TenantId filter + TimestampUtc range/group (and the
            // leftmost-prefix covers plain TenantId lookups, so no separate TenantId index is needed).
            e.HasIndex(x => new { x.TenantId, x.TimestampUtc });
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<OrchestrationEntity>(e =>
        {
            e.ToTable("orchestrations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.DefinitionJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.TenantId);
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<ScheduledRunEntity>(e =>
        {
            e.ToTable("scheduled_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Story).HasMaxLength(4000).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            e.Property(x => x.Cron).HasMaxLength(128);
            e.Property(x => x.LastStatus).HasMaxLength(256);
            e.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.TenantId);
            // The scheduler's due-scan: enabled rows ordered by the next fire time.
            e.HasIndex(x => new { x.Enabled, x.NextRunAtUtc });
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });
    }
}
