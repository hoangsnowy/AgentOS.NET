// Shared DataProtection wiring for the Api + Web hosts. Replaces a bare AddDataProtection() (which
// keeps the key ring in memory → tenant secrets + auth cookies break on every restart/scale, and the
// two hosts can't decrypt each other's data). When a Postgres connection string is configured the key
// ring is persisted to `config.data_protection_keys` and SHARED via a fixed application name; with no
// DB (CI / standalone dev) it degrades to an ephemeral in-memory ring so the host still boots.
//
// Enterprise review E0.2 — the ring encrypts every tenant secret (LLM keys, PATs) and sits in the SAME
// database. Wrap it with a key the database never holds (bootstrap config, set at deploy):
//   DataProtection:KeyVaultKeyId            https://<vault>.vault.azure.net/keys/<key>  (managed identity / az login)
//   DataProtection:CertificatePath (+ :CertificatePassword)   a PFX on disk (on-prem, k8s secret mount)
//   DataProtection:CertificateThumbprint    a certificate in CurrentUser/LocalMachine\My (Windows hosts)
//   DataProtection:RequireKeyWrapping       true → refuse to start with an unwrapped ring (outside Development)

using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using AgentOs.Modules.AppConfig.Persistence;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentOs.Modules.AppConfig;

/// <summary>How the DataProtection key ring is protected at rest (shown on System → About).</summary>
/// <param name="Mode">"Azure Key Vault", "Certificate" or "Unwrapped".</param>
/// <param name="Detail">Vault key / certificate subject — never secret material.</param>
public sealed record KeyRingProtection(string Mode, string? Detail)
{
    /// <summary>True when the ring is encrypted with a key outside the database.</summary>
    public bool IsWrapped => !string.Equals(Mode, Unwrapped, StringComparison.Ordinal);

    /// <summary>No wrapping configured.</summary>
    public const string Unwrapped = "Unwrapped";
}

/// <summary>Host extension: durable, cross-host DataProtection key ring.</summary>
public static class DataProtectionExtensions
{
    /// <summary>The shared application name both hosts protect/unprotect under — must match so a value
    /// encrypted by the Web (e.g. a tenant LLM key saved in Settings) is decryptable by the Api.</summary>
    // Deliberately the pre-rename spelling: the application name isolates the key ring, so renaming it to
    // "AgentOS.NET" would make every existing encrypted setting undecryptable.
    private const string SharedApplicationName = "AgentOS";

    /// <summary>Adds DataProtection, persisting the key ring to Postgres when configured and wrapping it with
    /// Key Vault or a certificate when configured.</summary>
    public static IHostApplicationBuilder AddAgentOsDataProtection(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName(SharedApplicationName);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddDbContext<DataProtectionDbContext>(opt =>
                opt.UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__DataProtectionMigrationsHistory", schema: "config")));

            dataProtection.PersistKeysToDbContext<DataProtectionDbContext>();
        }

        var protection = ProtectKeys(dataProtection, builder.Configuration);
        if (!protection.IsWrapped
            && !builder.Environment.IsDevelopment()
            && builder.Configuration.GetValue("DataProtection:RequireKeyWrapping", false))
        {
            throw new InvalidOperationException(
                "DataProtection:RequireKeyWrapping is on but the key ring is unwrapped — set DataProtection:KeyVaultKeyId " +
                "(Azure Key Vault) or DataProtection:CertificatePath / CertificateThumbprint.");
        }
        builder.Services.AddSingleton(protection);
        return builder;
    }

    /// <summary>Apply the configured key wrapping; returns what was applied.</summary>
    internal static KeyRingProtection ProtectKeys(IDataProtectionBuilder dataProtection, IConfiguration configuration)
    {
        if (Uri.TryCreate(configuration["DataProtection:KeyVaultKeyId"], UriKind.Absolute, out var keyId))
        {
            // DefaultAzureCredential: the Container App's managed identity in Azure, `az login` on a dev box.
            dataProtection.ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential());
            return new KeyRingProtection("Azure Key Vault", keyId.GetLeftPart(UriPartial.Path));
        }

        if (configuration["DataProtection:CertificatePath"] is { Length: > 0 } path)
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, configuration["DataProtection:CertificatePassword"]);
            dataProtection.ProtectKeysWithCertificate(certificate);
            return new KeyRingProtection("Certificate", certificate.Subject);
        }

        if (configuration["DataProtection:CertificateThumbprint"] is { Length: > 0 } thumbprint)
        {
            var certificate = FindByThumbprint(thumbprint)
                ?? throw new InvalidOperationException($"DataProtection certificate {thumbprint} was not found in CurrentUser or LocalMachine\\My.");
            dataProtection.ProtectKeysWithCertificate(certificate);
            return new KeyRingProtection("Certificate", certificate.Subject);
        }

        return new KeyRingProtection(KeyRingProtection.Unwrapped, null);
    }

    private static readonly StoreLocation[] StoreLocations = [StoreLocation.CurrentUser, StoreLocation.LocalMachine];

    private static X509Certificate2? FindByThumbprint(string thumbprint)
        => StoreLocations.Select(location => FindIn(location, thumbprint)).FirstOrDefault(c => c is not null);

    private static X509Certificate2? FindIn(StoreLocation location, string thumbprint)
    {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return found.Count > 0 ? found[0] : null;
    }
}
