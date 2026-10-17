// EF Core DbContext for the Tenants module. Owns `tenants.tenants` (admin-scoped registry, no
// global query filter — every admin endpoint guards on the Admin policy).

using AgentOs.Modules.Tenants.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AgentOs.Modules.Tenants.Persistence;

internal sealed class TenantsDbContext : DbContext
{
    public TenantsDbContext(DbContextOptions<TenantsDbContext> options)
        : base(options)
    {
    }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<ApiTokens.ApiTokenEntity> ApiTokens => Set<ApiTokens.ApiTokenEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema("tenants");

        modelBuilder.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(128);
            e.Property(x => x.Target).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => new { x.TenantId, x.TimestampUtc });
        });

        // E1.3: API tokens — only the SHA-256 is stored; Prefix is the (unique) lookup key at authentication.
        modelBuilder.Entity<ApiTokens.ApiTokenEntity>(e =>
        {
            e.ToTable("api_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasIndex(x => x.TenantId);
        });
    }
}
