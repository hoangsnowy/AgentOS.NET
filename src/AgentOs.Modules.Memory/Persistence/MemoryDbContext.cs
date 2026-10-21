// Knowledge graph persistence (schema memory, ADR-0008 slice 1): nodes and typed edges, both bi-temporal, every row
// per tenant (EF filter + RLS). Nodes carry a generated tsvector over title + body with a GIN index, so lessons are
// recalled by full-text relevance in the database (embeddings are slice 2).
// The tenant filter references the instance member (_tenant), never a captured local — EF caches the model per type.

using System;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace AgentOs.Modules.Memory.Persistence;

/// <summary>A knowledge-graph node.</summary>
public sealed class MemoryNodeEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    /// <summary>"owner/repo" of the project it belongs to; null for tenant-wide knowledge.</summary>
    public string? RepoKey { get; set; }
    public string Kind { get; set; } = string.Empty;
    /// <summary>Natural key per tenant (e.g. <c>ticket:owner/repo#7</c>); recording it again refreshes the node.</summary>
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public bool Pinned { get; set; }
    public string? SourceRef { get; set; }
    /// <summary>World time: when it became true.</summary>
    public DateTimeOffset ValidFrom { get; set; }
    /// <summary>System time: when it was recorded / last refreshed.</summary>
    public DateTimeOffset RecordedAt { get; set; }
    /// <summary>Forgotten (kept for history, never recalled or shown).</summary>
    public DateTimeOffset? RetractedAt { get; set; }
    public DateTimeOffset? LastUsedUtc { get; set; }
    public int UseCount { get; set; }
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}

/// <summary>A typed edge between two nodes.</summary>
public sealed class MemoryEdgeEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string Rel { get; set; } = string.Empty;
    public DateTimeOffset ValidFrom { get; set; }
    /// <summary>When the relation stopped holding (null = live).</summary>
    public DateTimeOffset? ValidTo { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>EF context for the knowledge graph (schema <c>memory</c>).</summary>
public sealed class MemoryDbContext : DbContext
{
    private readonly ITenantContext? _tenant;

    public MemoryDbContext(DbContextOptions<MemoryDbContext> options, ITenantContext? tenant = null) : base(options) => _tenant = tenant;

    public DbSet<MemoryNodeEntity> Nodes => Set<MemoryNodeEntity>();

    public DbSet<MemoryEdgeEntity> Edges => Set<MemoryEdgeEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("memory");

        modelBuilder.Entity<MemoryNodeEntity>(e =>
        {
            e.ToTable("nodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.RepoKey).HasMaxLength(300);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.Key).IsRequired().HasMaxLength(400);
            e.Property(x => x.Title).IsRequired().HasMaxLength(400);
            e.Property(x => x.Body).HasMaxLength(16_000);
            e.Property(x => x.SourceRef).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.RepoKey, x.Kind });
            e.HasGeneratedTsVectorColumn(x => x.SearchVector, "simple", x => new { x.Title, x.Body })
             .HasIndex(x => x.SearchVector).HasMethod("GIN");
            e.HasQueryFilter(x => _tenant == null || x.TenantId == _tenant.TenantId);
        });

        modelBuilder.Entity<MemoryEdgeEntity>(e =>
        {
            e.ToTable("edges");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Rel).IsRequired().HasMaxLength(32);
            e.HasIndex(x => new { x.TenantId, x.FromId, x.Rel });
            e.HasIndex(x => new { x.TenantId, x.ToId, x.Rel });
            e.HasQueryFilter(x => _tenant == null || x.TenantId == _tenant.TenantId);
        });
    }
}
