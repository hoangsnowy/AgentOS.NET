// #2 — committed dev-default secrets (admin/admin, the web client secret) must never authenticate a real
// deployment. The startup guard fails fast in non-Development if any are still in effect; no-op in Dev.

using System;
using System.Collections.Generic;
using AgentOs.SharedKernel.Security;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Security;

public sealed class DevSecretGuardTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) { dict[k] = v; }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void EnsureNoDevDefaults_ProductionWithDevClientSecret_Throws()
    {
        var cfg = Config(("Auth:Keycloak:ClientSecret", "agentic-web-dev-secret"));
        var ex = Should.Throw<InvalidOperationException>(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Production"));
        ex.Message.ShouldContain("Auth:Keycloak:ClientSecret");
    }

    [Fact]
    public void EnsureNoDevDefaults_ProductionWithDevAdminPassword_Throws()
    {
        var cfg = Config(("Auth:Keycloak:Admin:Password", "admin"));
        Should.Throw<InvalidOperationException>(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Production"));
    }

    [Fact]
    public void EnsureNoDevDefaults_ProductionWithRealSecrets_DoesNotThrow()
    {
        var cfg = Config(
            ("Auth:Keycloak:ClientSecret", "a-real-rotated-secret"),
            ("Auth:Keycloak:Admin:Password", "S0me-Str0ng-Pw"));
        Should.NotThrow(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Production"));
    }

    [Fact]
    public void EnsureNoDevDefaults_Development_DoesNotThrow_EvenWithDevDefaults()
    {
        var cfg = Config(
            ("Auth:Keycloak:ClientSecret", "agentic-web-dev-secret"),
            ("Auth:Keycloak:Admin:Password", "admin"));
        Should.NotThrow(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Development"));
    }

    [Fact]
    public void EnsureNoDevDefaults_ProductionWithChangeMePlaceholder_Throws()
    {
        var cfg = Config(("Auth:Keycloak:Admin:ClientSecret", "CHANGE_ME"));
        var ex = Should.Throw<InvalidOperationException>(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Production"));
        ex.Message.ShouldContain("Auth:Keycloak:Admin:ClientSecret");
    }

    [Fact]
    public void EnsureNoDevDefaults_ProductionWithChangeMeInsideConnectionString_Throws()
    {
        var cfg = Config(("ConnectionStrings:DefaultConnection", "Host=postgres;Username=agentos;Password=CHANGE_ME"));
        var ex = Should.Throw<InvalidOperationException>(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Production"));
        ex.Message.ShouldContain("ConnectionStrings:DefaultConnection");
    }

    [Fact]
    public void EnsureNoDevDefaults_DevelopmentWithChangeMe_DoesNotThrow()
    {
        var cfg = Config(("Auth:Keycloak:ClientSecret", "CHANGE_ME"));
        Should.NotThrow(() => DevSecretGuard.EnsureNoDevDefaults(cfg, "Development"));
    }
}
