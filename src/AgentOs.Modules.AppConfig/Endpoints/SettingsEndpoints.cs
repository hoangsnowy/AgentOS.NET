// Operator-only runtime configuration endpoints. The Web Settings UI calls these to rotate LLM
// keys and GitHub settings (the only keys in SettingsKeyRegistry) without restarting the API.
// Secrets are WRITE-ONLY (GAP-08): GET masks every value not known to be non-secret (fail closed — see
// SettingsKeyRegistry.IsSecret) the same way the Settings hub does — an admin can see that a key is set and its last
// four characters, never the key. POST /settings/Github/test probes GitHub with the STORED token server-side, so the
// desktop can test a saved PAT it can no longer read.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Workspaces;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace AgentOs.Modules.AppConfig.Endpoints;

internal static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/settings/{prefix}", ReadPrefixAsync)
        .WithName("SettingsList")
        .WithSummary("Read every key beneath a prefix (Llm, Github); secret values are masked")
        .WithTags("Settings")
        .RequireAuthorization("Admin");

        app.MapPost("/settings/Github/test", TestGitHubAsync)
        .WithName("SettingsGithubTest")
        .WithSummary("Probe GitHub with the saved (or a just-typed) Pipeline auto-PR token; never returns the token")
        .WithTags("Settings")
        .RequireAuthorization("Admin");

        app.MapPost("/settings", async (SetEntryRequest? body, IAppConfigStore store, CancellationToken ct) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Key))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["key"] = ["A non-empty setting key is required."],
                });
            }
            if (!SettingsKeyRegistry.IsKnownKey(body.Key))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["key"] = [$"'{body.Key}' is not a recognised runtime setting."],
                });
            }
            if (SettingsKeyRegistry.ValidateValue(body.Key, body.Value) is { } error)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["value"] = [error],
                });
            }
            await store.SetAsync(body.Key, body.Value, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("SettingsSet")
        .WithSummary("Set a single key (overwrite if present)")
        .WithTags("Settings")
        .RequireAuthorization("Admin");

        app.MapDelete("/settings/{key}", async (string key, IAppConfigStore store, CancellationToken ct) =>
        {
            if (!SettingsKeyRegistry.IsKnownKey(key))
            {
                return Results.Problem(
                    detail: $"'{key}' is not a recognised runtime setting.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            await store.DeleteAsync(key, ct).ConfigureAwait(false);
            return Results.NoContent();
        })
        .WithName("SettingsDelete")
        .WithSummary("Delete a key")
        .WithTags("Settings")
        .RequireAuthorization("Admin");

        return app;
    }

    /// <summary><c>GET /settings/{prefix}</c>: every key beneath a readable prefix, secret values masked (GAP-08).</summary>
    internal static async Task<IResult> ReadPrefixAsync(
        string prefix, IAppConfigStore store, ISettingsCatalog? catalog, CancellationToken ct)
    {
        if (!SettingsKeyRegistry.IsReadablePrefix(prefix))
        {
            return Results.Problem(
                detail: $"'{prefix}' is not a readable settings prefix.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var keys = await store.ListAsync(prefix + ":", ct).ConfigureAwait(false);
        var dict = new Dictionary<string, string?>(keys.Count);
        foreach (var k in keys)
        {
            var value = await store.GetAsync(k, ct).ConfigureAwait(false);
            dict[k] = value is not null && SettingsKeyRegistry.IsSecret(k, catalog)
                ? AgentOs.Modules.AppConfig.Settings.SettingValidation.Mask(value)
                : value;
        }
        return Results.Ok(dict);
    }

    /// <summary><c>POST /settings/Github/test</c>: probe GitHub with the token typed in the form, else the SAVED token
    /// (read server-side — the browser never gets it). With a repo owner + name (typed, else saved) it checks that repo
    /// and resolves its default branch; without, it checks the token can list repositories. Always 200 with a
    /// plain-language <see cref="GitHubTestResult"/>; the token never appears in the response.</summary>
    internal static async Task<IResult> TestGitHubAsync(
        GitHubTestRequest? body,
        IAppConfigStore store,
        [FromServices] ISourceProviderResolver? sources,
        [FromServices] ITenantContext? tenant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var token = Typed(body?.Token) ?? await store.GetAsync(AppConfigKeys.GithubPat, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Ok(new GitHubTestResult(false, "No token saved yet. Paste a personal access token, then Save or Test."));
        }
        if (sources is null || !sources.TryResolve(SourceProviderKind.GitHub, out var provider) || provider is null)
        {
            return Results.Ok(new GitHubTestResult(false, "GitHub is not available on this server."));
        }

        var owner = Typed(body?.Owner) ?? await store.GetAsync(AppConfigKeys.GithubRepoOwner, ct).ConfigureAwait(false);
        var repo = Typed(body?.Repo) ?? await store.GetAsync(AppConfigKeys.GithubRepoName, ct).ConfigureAwait(false);
        var branch = Typed(body?.Branch) ?? await store.GetAsync(AppConfigKeys.GithubBaseBranch, ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
            {
                var descriptor = new WorkspaceDescriptor(
                    Guid.NewGuid(), tenant?.TenantId ?? ITenantContext.DefaultTenantId, SourceProviderKind.GitHub,
                    owner, repo, null, string.IsNullOrWhiteSpace(branch) ? "main" : branch, token);
                var result = await provider.ValidateAsync(descriptor, ct).ConfigureAwait(false);
                return Results.Ok(result.Ok
                    ? new GitHubTestResult(true, $"{owner}/{repo} reachable — default branch {result.DefaultBranch}.")
                    : new GitHubTestResult(false, Scrub(result.Error ?? "GitHub could not reach that repository.", token)));
            }
            var repos = await provider.ListRepositoriesAsync(new ConnectionCredentials(SourceProviderKind.GitHub, token, owner), ct)
                .ConfigureAwait(false);
            return Results.Ok(new GitHubTestResult(true,
                $"Token OK — it can see {repos.Count} repositories. Add the repo owner and name to check the target repo too."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Ok(new GitHubTestResult(false, Scrub($"GitHub rejected the token: {ex.Message}", token)));
        }
    }

    private static string? Typed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // A provider error must never echo the credential back to the browser.
    private static string Scrub(string message, string token) =>
        message.Replace(token, "••••", StringComparison.Ordinal);
}

/// <summary>Body for <c>POST /settings/Github/test</c>: every field optional — a blank one falls back to the saved value.</summary>
public sealed record GitHubTestRequest(string? Token = null, string? Owner = null, string? Repo = null, string? Branch = null);

/// <summary>Outcome of <c>POST /settings/Github/test</c>: a plain-language message, never the token.</summary>
public sealed record GitHubTestResult(bool Ok, string Message);

/// <summary>Body for <c>POST /settings</c>.</summary>
public sealed record SetEntryRequest(string Key, string Value);
