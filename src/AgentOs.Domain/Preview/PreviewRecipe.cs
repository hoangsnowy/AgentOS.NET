// "Try the app" — a project says how to run itself locally in one file at its root, `.agentos/preview.json`:
//
//   { "command": "dotnet run --project infra/TeacherHub.AppHost", "url": "http://localhost:5173", "readyTimeoutSeconds": 600 }
//
// The paired runner gets the latest default branch, runs `command` in it and opens `url` once something listens there.
// The server reads the recipe to SHOW the command before anything runs; the runner re-reads its own checkout and
// refuses when it differs, so what the person approved is exactly what runs. Shared verbatim by the runner
// (AgentOs.RemoteAgent links this file), so both sides validate with the same rules. BCL only.

using System;
using System.Text.Json;

namespace AgentOs.Domain.Preview;

/// <summary>How a project runs itself for "Try the app" (<see cref="FilePath"/> in the repo).</summary>
/// <param name="Command">One shell line, run in the checkout's root.</param>
/// <param name="Url">Where the app answers once it is up — on this computer only (localhost).</param>
/// <param name="ReadyTimeoutSeconds">How long the first start may take (restores, image pulls) before it counts as failed.</param>
public sealed record PreviewRecipe(string Command, Uri Url, int ReadyTimeoutSeconds)
{
    /// <summary>The recipe's path, relative to the repository root.</summary>
    public const string FilePath = ".agentos/preview.json";

    /// <summary>Default wait for the app to come up.</summary>
    public const int DefaultReadyTimeoutSeconds = 600;

    private const int MaxCommandLength = 400;

    /// <summary>Parse and validate a recipe; null with a plain <paramref name="error"/> when it can't be used.</summary>
    public static PreviewRecipe? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "The recipe file is empty."; return null; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "The recipe must be a JSON object."; return null; }

            var command = root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()!.Trim() : "";
            if (command.Length == 0) { error = "The recipe has no \"command\"."; return null; }
            if (command.Length > MaxCommandLength) { error = $"The command is longer than {MaxCommandLength} characters."; return null; }
            if (command.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0) { error = "The command must be a single line."; return null; }

            var rawUrl = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()!.Trim() : "";
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                error = "The recipe needs a \"url\" like http://localhost:5173.";
                return null;
            }
            if (!IsLocal(url)) { error = "The url must be on this computer (localhost)."; return null; }

            var timeout = DefaultReadyTimeoutSeconds;
            if (root.TryGetProperty("readyTimeoutSeconds", out var t)
                && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out timeout) || timeout is < 10 or > 3600))
            {
                error = "\"readyTimeoutSeconds\" must be a whole number from 10 to 3600.";
                return null;
            }
            return new PreviewRecipe(command, url, timeout);
        }
        catch (JsonException)
        {
            error = "The recipe is not valid JSON.";
            return null;
        }
    }

    /// <summary>True for loopback hosts: localhost, *.localhost, 127.0.0.1, [::1].</summary>
    public static bool IsLocal(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var host = url.IdnHost;
        return url.IsLoopback
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A starter recipe for a repo that has none (shown to the person and put in the set-up ticket).</summary>
    public const string Example = """
        {
          "command": "dotnet run --project src/MyApp",
          "url": "http://localhost:5000",
          "readyTimeoutSeconds": 600
        }
        """;
}
