// Lab configuration: where the sandboxes are and the tokens to reach them. Read from a DEDICATED user-secrets
// store ("agentos-lab-tests") plus environment variables (Lab__GitHub__Token …) — never from the repo.
// Every lab test skips with a precise reason when its part of the lab isn't configured, so the suite is safe to
// run anywhere and never fails just because a secret is missing.

using System;
using Microsoft.Extensions.Configuration;

namespace AgentOs.Lab.Tests;

internal static class LabConfig
{
    public const string UserSecretsId = "agentos-lab-tests";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddUserSecrets(UserSecretsId)
        .AddEnvironmentVariables()
        .Build();

    /// <summary>Opt-in gate: real network calls only when explicitly requested.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_LAB"), "true", StringComparison.OrdinalIgnoreCase);

    public static string? GitHubToken => Get("Lab:GitHub:Token");
    public static string GitHubOwner => Get("Lab:GitHub:Owner") ?? "hoangsnowy";
    public static string GitHubRepo => Get("Lab:GitHub:Repo") ?? "agentos-lab";

    /// <summary>GitHub Projects (v2) board number, e.g. 1 for https://github.com/users/hoangsnowy/projects/1.</summary>
    public static int? GitHubProjectNumber => int.TryParse(Get("Lab:GitHub:ProjectNumber"), out var n) ? n : null;

    public static string? AzureDevOpsToken => Get("Lab:AzureDevOps:Token");
    public static string? AzureDevOpsOrg => Get("Lab:AzureDevOps:Org");
    public static string? AzureDevOpsProject => Get("Lab:AzureDevOps:Project");
    public static string? AzureDevOpsRepo => Get("Lab:AzureDevOps:Repo") ?? AzureDevOpsProject;

    // E1.8 — Jira Cloud (a free site is enough) as a ticket tracker.
    public static string? JiraSite => Get("Lab:Jira:Site");
    public static string? JiraProject => Get("Lab:Jira:Project");
    public static string? JiraEmail => Get("Lab:Jira:Email");
    public static string? JiraToken => Get("Lab:Jira:Token");

    /// <summary>The Azure DevOps project whose work items the Boards lab tests use (defaults to the repo project).</summary>
    public static string? AzureBoardsProject => Get("Lab:AzureDevOps:BoardsProject") ?? AzureDevOpsProject;

    /// <summary>Why a Jira lab test can't run, or null when it can.</summary>
    public static string? JiraSkipReason =>
        !Enabled ? "Lab tests are opt-in: set RUN_AGENTOS_LAB=true (see docs/LAB.md)."
        : string.IsNullOrWhiteSpace(JiraSite) || string.IsNullOrWhiteSpace(JiraProject) || string.IsNullOrWhiteSpace(JiraEmail) || string.IsNullOrWhiteSpace(JiraToken)
            ? $"Set Lab:Jira:Site, :Project, :Email and :Token in user-secrets '{UserSecretsId}' (see docs/LAB.md)."
            : null;

    /// <summary>Why a GitHub lab test can't run, or null when it can.</summary>
    public static string? GitHubSkipReason =>
        !Enabled ? "Lab tests are opt-in: set RUN_AGENTOS_LAB=true (see docs/LAB.md)."
        : string.IsNullOrWhiteSpace(GitHubToken) ? $"Set Lab:GitHub:Token in user-secrets '{UserSecretsId}' (see docs/LAB.md)."
        : null;

    /// <summary>Why an Azure DevOps lab test can't run, or null when it can.</summary>
    public static string? AzureDevOpsSkipReason =>
        !Enabled ? "Lab tests are opt-in: set RUN_AGENTOS_LAB=true (see docs/LAB.md)."
        : string.IsNullOrWhiteSpace(AzureDevOpsToken) || string.IsNullOrWhiteSpace(AzureDevOpsOrg) || string.IsNullOrWhiteSpace(AzureDevOpsProject)
            ? $"Set Lab:AzureDevOps:Org, :Project and :Token in user-secrets '{UserSecretsId}' (see docs/LAB.md)."
            : null;

    private static string? Get(string key) => Config[key] is { Length: > 0 } v ? v.Trim() : null;
}
