// Module entry: registers the tenant registry DbContext + repository + Keycloak admin client +
// /tenants endpoints. Active when a DB connection string is present; otherwise no-op repo so the
// host still boots.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.Modules.Tenants.Persistence.Entities;
using AgentOs.Modules.Tenants.Persistence.Repositories;
using AgentOs.SharedKernel.Modularity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentOs.Modules.Tenants;

public sealed class TenantsModule : IModule, IEndpointModule, IInitializableModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Fail-fast config: a malformed admin BaseUrl otherwise dies on the first member-management
        // call inside the Users app's circuit. Empty stays allowed (no-op client, standalone/CI).
        services.AddOptions<KeycloakAdminOptions>()
            .Bind(configuration.GetSection(KeycloakAdminOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.BaseUrl)
                    || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
                "Auth:Keycloak:Admin:BaseUrl must be an absolute URL when set.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Realm),
                "Auth:Keycloak:Admin:Realm must not be empty.")
            .ValidateOnStart();

        // Real admin client only when a Keycloak server is configured. Without a BaseUrl (standalone
        // Web / CI) the typed HttpClient has no BaseAddress and the real client throws on first call,
        // crashing the Users app's circuit — fall back to a no-op client that degrades gracefully.
        var keycloakBaseUrl = configuration.GetSection(KeycloakAdminOptions.SectionName)["BaseUrl"];
        if (!string.IsNullOrWhiteSpace(keycloakBaseUrl))
        {
            services.AddHttpClient<IKeycloakAdminClient, KeycloakAdminClient>();
        }
        else
        {
            services.AddSingleton<IKeycloakAdminClient, NullKeycloakAdminClient>();
        }

        services.AddScoped<ITenantSignupService, TenantSignupService>();

        // App-sent email (invitation links). Real MailKit sender when an SMTP host is configured
        // (full stack injects MailHog; prod injects a real provider via secrets); otherwise a no-op
        // logger so standalone dev / CI still boot. Keycloak's own auth emails are separate.
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .Validate(o => o.SmtpPort is > 0 and <= 65535,
                "Email:SmtpPort must be in [1, 65535].")
            .Validate(o => string.IsNullOrWhiteSpace(o.SmtpHost) || !string.IsNullOrWhiteSpace(o.From),
                "Email:From is required when Email:SmtpHost is set.")
            .ValidateOnStart();
        var smtpHost = configuration.GetSection(EmailOptions.SectionName)["SmtpHost"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddSingleton<IEmailSender, MailKitEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, NullEmailSender>();
        }

        // Plan C.3: settings writes are audited into this module's trail. Add (not TryAdd) so it replaces the
        // AppConfig module's no-op default whichever module loads first (last registration wins on resolve).
        services.AddSingleton<AgentOs.Domain.Configuration.ISettingsAuditSink, Settings.AuditLogSettingsSink>();

        // E1.4: tenant export + purge (catalog-driven; a no-DB host reports it unavailable).
        services.AddScoped<Data.ITenantDataService>(sp => new Data.TenantDataService(
            configuration.GetConnectionString("DefaultConnection"),
            sp.GetRequiredService<Keycloak.IKeycloakAdminClient>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Data.TenantDataService>>()));

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<TenantsDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "tenants")));
            services.AddNpgsqlConnectionFactory(connectionString);
            services.AddScoped<ITenantsRepository, TenantsRepository>();
            services.AddScoped<IAuditLog, EfAuditLog>();
            services.AddScoped<ApiTokens.IApiTokenService>(sp => new ApiTokens.ApiTokenService(
                sp.GetRequiredService<TenantsDbContext>(), sp.GetRequiredService<IAuditLog>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        }
        else
        {
            services.TryAddSingleton<ApiTokens.IApiTokenService, ApiTokens.NullApiTokenService>();
            services.TryAddSingleton<ITenantsRepository, NullTenantsRepository>();
            services.TryAddSingleton<IAuditLog, NullAuditLog>();
        }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapTenantEndpoints();
    }

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<TenantsDbContext>();
        if (db is not null)
        {
            // Advisory lock: serialise concurrent replicas racing the same migration at boot.
            // Held through the seed below so the existence-check + insert can't race either.
            await using var migrationLock = await PgAdvisoryLock
                .AcquireAsync(db.Database.GetConnectionString(), "agentos:migrate:tenants", ct).ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);

            // Seed the 'default' tenant so operator-mode invitations can always be accepted.
            // DefaultTenantContext uses tenant id "default" for single-tenant / dev operation;
            // invitations created in that mode embed "default" and would fail the existence
            // check in TenantSignupService without this row.
            if (!await db.Tenants.AnyAsync(t => t.Id == "default", ct).ConfigureAwait(false))
            {
                db.Tenants.Add(new TenantEntity
                {
                    Id = "default",
                    Name = "Default Workspace",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
