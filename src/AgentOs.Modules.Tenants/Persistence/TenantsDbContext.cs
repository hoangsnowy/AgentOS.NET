// EF Core DbContext for the Tenants module. Owns `tenants.tenants` (admin-scoped registry, no
// global query filter — every admin endpoint guards on the Admin policy), the audit trail (append-only, hash-chained)
// and invitations; audit_events + invitations are under RLS (ADR-0012), api_tokens is exempt (looked up by prefix
// before any tenant is known).

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
    public DbSet<InvitationEntity> Invitations => Set<InvitationEntity>();

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
            // GAP-11b hash chain: one row per (tenant, position) — two writers racing for the same position cannot both
            // land (the loser retries on the new head), so the chain never forks. Legacy rows (Sequence NULL) are exempt:
            // NULLs are distinct in a Postgres unique index.
            e.Property(x => x.PrevHash).HasMaxLength(64);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
        });

        // GAP-11: single-use, email-bound, revocable invitations (the token carries only the id + tenant).
        modelBuilder.Entity<InvitationEntity>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.EmailNormalized).HasMaxLength(320).IsRequired();
            e.Property(x => x.Role).HasMaxLength(16).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(128);
            e.Property(x => x.AcceptedUserId).HasMaxLength(128);
            e.Property(x => x.RevokedBy).HasMaxLength(128);
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
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
