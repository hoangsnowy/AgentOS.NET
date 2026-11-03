// E1.3 — what an API token may do, and whom it acts as.
//   Scopes  read  — safe HTTP methods (GET/HEAD/OPTIONS): runs, results, settings views
//           run   — also unsafe methods (POST/PUT/PATCH/DELETE): start runs, write data; MCP (JSON-RPC over POST)
//           admin — also the tenant-admin policy (the token's principal gets the admin role)
//   Scopes are closed upwards (admin ⇒ run ⇒ read), so a token holds one of three levels. The level is enforced by
//   ApiTokenScopeMiddleware (method → scope) and by the role claim (admin endpoints need role admin).
//   Kinds   personal — acts as the member who created it (sub = their user id); revoked when they are removed
//           service  — a tenant service account (sub = apitoken:<id>); admins only

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Modules.Tenants.ApiTokens;

/// <summary>API token scopes (E1.3).</summary>
public static class ApiTokenScopes
{
    /// <summary>Read-only: safe HTTP methods.</summary>
    public const string Read = "read";

    /// <summary>Read + start runs / write (unsafe HTTP methods).</summary>
    public const string Run = "run";

    /// <summary>Run + the tenant-admin role.</summary>
    public const string Admin = "admin";

    /// <summary>The claim carrying the space-separated scopes (OAuth 2.0 convention).</summary>
    public const string ClaimType = "scope";

    /// <summary>Every scope, lowest first.</summary>
    public static IReadOnlyList<string> All { get; } = [Read, Run, Admin];

    /// <summary>The closed set for a requested level or list: admin ⇒ run ⇒ read. Throws on an unknown scope or none.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? requested)
    {
        var highest = -1;
        var scopes = (requested ?? []).Select(raw => raw?.Trim().ToLowerInvariant() ?? string.Empty).Where(s => s.Length > 0);
        foreach (var scope in scopes)
        {
            var rank = Rank(scope);
            if (rank < 0)
            {
                throw new InvalidOperationException($"Unknown scope '{scope}'. Scopes: {string.Join(", ", All)}.");
            }
            highest = Math.Max(highest, rank);
        }
        if (highest < 0)
        {
            throw new InvalidOperationException($"Choose at least one scope: {string.Join(", ", All)}.");
        }
        return All.Take(highest + 1).ToArray();
    }

    /// <summary>Space-separated storage / claim form.</summary>
    public static string Format(IEnumerable<string> scopes) => string.Join(' ', scopes);

    /// <summary>Parse the storage / claim form (unknown entries dropped).</summary>
    public static IReadOnlyList<string> Parse(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => Rank(s) >= 0).ToArray();

    /// <summary>True when <paramref name="granted"/> (space-separated) includes <paramref name="scope"/>.</summary>
    public static bool Allows(string? granted, string scope) => Parse(granted).Contains(scope, StringComparer.Ordinal);

    /// <summary>The role a token with <paramref name="scopes"/> carries.</summary>
    public static string RoleFor(IEnumerable<string> scopes) => scopes.Contains(Admin, StringComparer.Ordinal) ? "admin" : "member";

    /// <summary>Scopes for a legacy row that only had a role.</summary>
    public static string FromLegacyRole(string? role) => Format(string.Equals(role, "admin", StringComparison.Ordinal) ? All : [Read, Run]);

    /// <summary>The highest scope of the set (for display).</summary>
    public static string Highest(IEnumerable<string> scopes) => scopes.OrderBy(Rank).LastOrDefault() ?? Read;

    private static int Rank(string scope) => scope switch
    {
        Read => 0,
        Run => 1,
        Admin => 2,
        _ => -1,
    };
}

/// <summary>API token kinds (E1.3).</summary>
public static class ApiTokenKind
{
    /// <summary>Acts as the member who created it.</summary>
    public const string Personal = "personal";

    /// <summary>A tenant service account (admins only).</summary>
    public const string Service = "service";

    /// <summary>True for a known kind.</summary>
    public static bool IsKnown(string? kind) => kind is Personal or Service;
}
