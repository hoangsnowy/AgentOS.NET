// UX phase 1 (docs/plans/ux-simplification.md): Ctrl/⌘+K opens the Activities search, which finds apps, settings
// AND actions — one search box for everything (Linear / Devin style), not a second palette. Actions are the
// verbs people come for ("run the pipeline", "add a model key", "invite a member"); each is executed by the
// shell (AppShellLayout.RunAction) so it needs no app to be open first.

using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentOs.Web.Services;

/// <summary>A shell action listed in Activities search.</summary>
/// <param name="Id">Stable id the shell switches on.</param>
/// <param name="Title">Verb phrase shown to the user.</param>
/// <param name="Caption">What it does / where it goes.</param>
/// <param name="Icon">Icon name.</param>
public sealed record ShellAction(string Id, string Title, string Caption, string Icon)
{
    internal string Keywords { get; init; } = string.Empty;
    internal bool AdminOnly { get; init; }
}

/// <summary>The action catalogue + search.</summary>
public static class ShellActions
{
    /// <summary>Open the Workflow app (run the pipeline).</summary>
    public const string Run = "run";
    /// <summary>Open the Board (start from an idea / ticket).</summary>
    public const string Idea = "idea";
    /// <summary>Settings → Model keys.</summary>
    public const string AddKey = "add-key";
    /// <summary>Settings → Users.</summary>
    public const string Invite = "invite";
    /// <summary>Settings → API tokens.</summary>
    public const string Token = "token";
    /// <summary>Settings → Data.</summary>
    public const string Export = "export";
    /// <summary>Toggle light / dark.</summary>
    public const string Theme = "theme";
    /// <summary>Sign out.</summary>
    public const string SignOut = "sign-out";

    private static readonly ShellAction[] All =
    [
        new(Run, "Run the agent pipeline", "Workflow · run the 5 agents on a story", "play") { Keywords = "run start execute pipeline workflow agents build story" },
        new(Idea, "Start from an idea", "Board · turn an idea into tickets and sessions", "git-pull-request") { Keywords = "new idea ticket story issue plan board decompose" },
        new(AddKey, "Add a model key", "Settings › LLM & providers › Model keys", "key") { Keywords = "add api key connect model llm provider anthropic openai gemini xai azure", AdminOnly = true },
        new(Invite, "Invite a member", "Settings › Users", "user") { Keywords = "invite add user member people team", AdminOnly = true },
        new(Token, "Create an API token", "Settings › API tokens", "tag") { Keywords = "create new api token service account ci machine", AdminOnly = true },
        new(Export, "Export workspace data", "Settings › Data", "floppy-disk") { Keywords = "export download backup data workspace", AdminOnly = true },
        new(Theme, "Switch light / dark theme", "Appearance", "moon") { Keywords = "theme dark light mode toggle switch appearance night" },
        new(SignOut, "Sign out", "End this session", "power") { Keywords = "sign out log out logout exit leave" },
    ];

    /// <summary>Actions whose title or keywords contain every query term; title-prefix matches first.</summary>
    public static IReadOnlyList<ShellAction> Find(string query, bool isAdmin, int max = 4)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) { return []; }
        return All
            .Where(a => (isAdmin || !a.AdminOnly) && terms.All(t => (a.Title + " " + a.Keywords).Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(a => a.Title.StartsWith(terms[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Take(max)
            .ToList();
    }

    /// <summary>The Settings deep-link target an action opens, or null when it isn't a Settings action.</summary>
    public static string? SettingsTarget(string id) => id switch
    {
        AddKey => SettingsLinks.ModelKeys,
        Invite => "users",
        Token => "tokens",
        Export => "data",
        _ => null,
    };
}
