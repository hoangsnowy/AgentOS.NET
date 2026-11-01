// M3 — Sessions persistence (schema sessions). Two aggregates: Runner (a paired machine) and
// RemoteSession (a member × workspace unit of work). Both tenant-scoped via a global query filter on
// TenantId mirrored from ITenantContext.
// IMPORTANT: the filter MUST reference the context instance member (_tenant) — NOT a local captured in
// OnModelCreating. EF caches the compiled model once per context type; a closure local is funcletized to
// a constant and freezes to whichever tenant first builds the model, whereas an instance-member reference
// is re-parameterized per executing context instance. (PipelineDbContext does the same.)
// NOTE: the pairing handshake reads a runner with the filter bypassed (see EfRunnerDirectory) because
// the connecting runner has no tenant context yet; the token hash is the credential.

using System;
using AgentOs.Modules.Sessions.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Sessions.Persistence;

/// <summary>EF Core context for session + runner persistence (schema <c>sessions</c>).</summary>
public sealed class SessionsDbContext : DbContext
{
    private readonly ITenantContext? _tenant;

    public SessionsDbContext(DbContextOptions<SessionsDbContext> options, ITenantContext? tenant = null)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<RunnerEntity> Runners => Set<RunnerEntity>();
    public DbSet<RemoteSessionEntity> Sessions => Set<RemoteSessionEntity>();
    public DbSet<SessionRepoEntity> SessionRepos => Set<SessionRepoEntity>();
    public DbSet<TicketFileEntity> TicketFiles => Set<TicketFileEntity>();
    public DbSet<DesignReviewEntity> DesignReviews => Set<DesignReviewEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("sessions");

        modelBuilder.Entity<RunnerEntity>(e =>
        {
            e.ToTable("runners");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.OwnerUserId).IsRequired().HasMaxLength(256);
            e.Property(x => x.Label).IsRequired().HasMaxLength(200);
            e.Property(x => x.TokenHash).IsRequired().HasMaxLength(256);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));
        });

        modelBuilder.Entity<RemoteSessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.MemberUserId).IsRequired().HasMaxLength(256);
            e.Property(x => x.Title).IsRequired().HasMaxLength(200);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.RepoOwner).HasMaxLength(256);
            e.Property(x => x.RepoName).HasMaxLength(256);
            e.Property(x => x.RepoDefaultBranch).HasMaxLength(256);
            e.Property(x => x.BoardItemNodeId).HasMaxLength(256);
            e.Property(x => x.TicketKind).HasMaxLength(32);
            e.Property(x => x.RunOnMachine).HasDefaultValue(false);
            e.Property(x => x.Brain).IsRequired().HasMaxLength(16).HasDefaultValue("Quick");
            e.Property(x => x.TicketType).HasMaxLength(32);
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));
        });

        modelBuilder.Entity<SessionRepoEntity>(e =>
        {
            e.ToTable("session_repos");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.SessionId).IsRequired();
            e.Property(x => x.WorkspaceRepoId);
            e.Property(x => x.Owner).IsRequired().HasMaxLength(256);
            e.Property(x => x.Repo).IsRequired().HasMaxLength(256);
            e.Property(x => x.DefaultBranch).IsRequired().HasMaxLength(256);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.Property(x => x.BranchName).HasMaxLength(256);
            e.Property(x => x.PrUrl).HasMaxLength(2048);
            e.HasIndex(x => new { x.TenantId, x.SessionId });
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));

            // Cascade-delete a session's repo rows when the session is removed.
            e.HasOne<RemoteSessionEntity>()
                .WithMany()
                .HasForeignKey(x => x.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Ticket files (ADR-0015): one row per (tenant, scope, path); the path is unique within a scope.
        modelBuilder.Entity<TicketFileEntity>(e =>
        {
            e.ToTable("ticket_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.ScopeKey).IsRequired().HasMaxLength(AgentOs.Domain.Files.TicketFileScope.MaxLength);
            e.Property(x => x.Path).IsRequired().HasMaxLength(AgentOs.Domain.Files.TicketFilePath.MaxLength);
            e.Property(x => x.PathKey).IsRequired().HasMaxLength(AgentOs.Domain.Files.TicketFilePath.MaxLength);
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Size).IsRequired();
            e.Property(x => x.Sha256).IsRequired().HasMaxLength(64);
            e.Property(x => x.ContentType).IsRequired().HasMaxLength(128);
            e.Property(x => x.UploadedBy).IsRequired().HasMaxLength(256);
            e.Property(x => x.UploadedAtUtc).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ScopeKey, x.PathKey }).IsUnique();
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));
        });

        // Design gate (ADR-0016): one review per (tenant, task, test run).
        modelBuilder.Entity<DesignReviewEntity>(e =>
        {
            e.ToTable("design_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.Property(x => x.Summary).IsRequired().HasMaxLength(4000);
            e.Property(x => x.FindingsJson).IsRequired();
            e.Property(x => x.ShotsJson).IsRequired();
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.Decision).HasMaxLength(32);
            e.Property(x => x.DecisionNote).HasMaxLength(4000);
            e.Property(x => x.DecidedBy).HasMaxLength(256);
            e.Property(x => x.UpdatedAtUtc).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.SessionId, x.Run }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.UpdatedAtUtc });
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));
        });
    }
}
