// E0.2 — the DataProtection key ring (it encrypts every tenant secret) can be wrapped with a key the database
// never holds. Proven with a real certificate: the key file on disk carries an encrypted secret, not the raw
// master key, and protect/unprotect still round-trips. Outside Development, RequireKeyWrapping refuses an
// unwrapped ring at startup.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AgentOs.Modules.AppConfig;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.AppConfig;

public sealed class KeyRingWrappingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("agentos-keyring-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HostApplicationBuilder Builder(string environment, Dictionary<string, string?> config)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(config);
        return builder;
    }

    private string WriteCertificate(string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AgentOS.NET key ring (test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Join(_dir, "keyring.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    [Fact]
    public void Certificate_WrapsTheRingOnDisk_AndStillRoundTrips()
    {
        var pfx = WriteCertificate("pw");
        var builder = Builder(Environments.Production, new()
        {
            ["DataProtection:CertificatePath"] = pfx,
            ["DataProtection:CertificatePassword"] = "pw",
            ["DataProtection:RequireKeyWrapping"] = "true",
        });
        builder.AddAgentOsDataProtection();
        var keys = Directory.CreateDirectory(Path.Join(_dir, "keys"));
        builder.Services.AddDataProtection().PersistKeysToFileSystem(keys);
        using var host = builder.Build();

        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
        protector.Unprotect(protector.Protect("tenant-api-key")).ShouldBe("tenant-api-key");

        var xml = File.ReadAllText(keys.GetFiles("key-*.xml").ShouldHaveSingleItem().FullName);
        xml.ShouldContain("encryptedSecret");          // wrapped by the certificate
        xml.ShouldNotContain("<masterKey");            // the raw master key is not on disk
        var protection = host.Services.GetRequiredService<KeyRingProtection>();
        protection.IsWrapped.ShouldBeTrue();
        protection.Mode.ShouldBe("Certificate");
        protection.Detail.ShouldBe("CN=AgentOS.NET key ring (test)");
    }

    [Fact]
    public void RequireKeyWrapping_RefusesAnUnwrappedRing_OutsideDevelopment()
    {
        var builder = Builder(Environments.Production, new() { ["DataProtection:RequireKeyWrapping"] = "true" });
        Should.Throw<InvalidOperationException>(() => builder.AddAgentOsDataProtection()).Message.ShouldContain("unwrapped");
    }

    [Fact]
    public void Default_IsUnwrapped_AndDevelopmentIsNeverBlocked()
    {
        var builder = Builder(Environments.Development, new() { ["DataProtection:RequireKeyWrapping"] = "true" });
        builder.AddAgentOsDataProtection();
        using var host = builder.Build();
        host.Services.GetRequiredService<KeyRingProtection>().IsWrapped.ShouldBeFalse();
    }
}
