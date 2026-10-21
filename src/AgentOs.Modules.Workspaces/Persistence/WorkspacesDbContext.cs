// M2 / board reshape — Workspaces persistence (schema workspaces). Two aggregates now: the board
// (WorkspaceEntity) and the repos under it (WorkspaceRepoEntity). Both tenant-scoped via a global
// query filter on TenantId, mirrored from the request's ITenantContext.
// IMPORTANT: the filter MUST reference the context instance member (_tenant) — NOT a local captured in
// OnModelCreating. EF caches the compiled model once per context type; a closure local is funcletized to
// a constant and freezes to whichever tenant first builds the model, whereas an instance-member reference
// is re-parameterized per executing context instance. (PipelineDbContext does the same.)

using System;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Workspaces.Persistence;

/// <summary>EF Core context for workspace persistence (schema <c>workspaces</c>).</summary>
public sealed class WorkspacesDbContext : DbContext
{
    private readonly ITenantContext? _tenant;

    public WorkspacesDbContext(DbContextOptions<WorkspacesDbContext> options, ITenantContext? tenant = null)
        : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();

    public DbSet<WorkspaceRepoEntity> WorkspaceRepos => Set<WorkspaceRepoEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("workspaces");

        modelBuilder.Entity<WorkspaceEntity>(e =>
        {
            e.ToTable("workspaces");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Kind).IsRequired();

            // Board binding.
            e.Property(x => x.ProjectOwner).IsRequired().HasMaxLength(256);
            e.Property(x => x.ProjectScope).IsRequired().HasMaxLength(16);
            e.Property(x => x.ProjectNumber);
            e.Property(x => x.ProjectNodeId).HasMaxLength(256);
            e.Property(x => x.Project).HasMaxLength(256);

            // Legacy single-repo coordinates — nullable since the board carries no single repo.
            e.Property(x => x.Owner).HasMaxLength(256);
            e.Property(x => x.Repo).HasMaxLength(256);
            e.Property(x => x.DefaultBranch).HasMaxLength(256);
            e.Property(x => x.RemoteUrl).HasMaxLength(2048);

            e.Property(x => x.CredentialRef).IsRequired().HasMaxLength(256);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32);
            e.Property(x => x.CreatedAtUtc).IsRequired();
            e.Property(x => x.Autopilot).IsRequired().HasMaxLength(16).HasDefaultValue("Off");
            e.Property(x => x.AutopilotUserId).HasMaxLength(64);
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            e.HasIndex(x => x.Autopilot);
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));
        });

        modelBuilder.Entity<WorkspaceRepoEntity>(e =>
        {
            e.ToTable("workspace_repos");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.WorkspaceId).IsRequired();
            e.Property(x => x.Owner).IsRequired().HasMaxLength(256);
            e.Property(x => x.Repo).IsRequired().HasMaxLength(256);
            e.Property(x => x.DefaultBranch).IsRequired().HasMaxLength(256);
            e.Property(x => x.RemoteUrl).IsRequired().HasMaxLength(2048);
            e.Property(x => x.Private).IsRequired();
            e.Property(x => x.AddedAtUtc).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.WorkspaceId });
            e.HasQueryFilter(x => x.TenantId == (_tenant != null ? _tenant.TenantId : null));

            // Cascade-delete repos when their board is removed.
            e.HasOne<WorkspaceEntity>()
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
