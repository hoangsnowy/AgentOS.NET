// "Connect this computer" without environment variables: `AgentOs.RemoteAgent pair <server> <code>` exchanges the
// one-time code shown on Board › Computers for this runner's id + token + hub URL, saves them for the signed-in OS
// user, and every later start just reads them. REMOTE_AGENT_HUB / _ID / _TOKEN still win when set (scripts, CI).
//
// Where: %APPDATA%\AgentOS.NET\runner.json on Windows (inside the user's own profile), ~/.config/agentos-net/runner.json
// elsewhere with mode 0600 — the same trust model as the gh CLI's hosts file. The token is the runner's pairing
// secret for ONE runner; Disconnect on Board › Computers revokes it.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.RemoteAgent;

/// <summary>What a paired runner needs to connect.</summary>
internal sealed record RunnerCredentials(string HubUrl, string RunnerId, string Token)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The saved-credentials file for this OS user.</summary>
    internal static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentOS.NET", "runner.json");
        }
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Join(string.IsNullOrWhiteSpace(config)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : config, "agentos-net", "runner.json");
    }

    /// <summary>The saved credentials, or null when there are none (or the file is unreadable).</summary>
    internal static RunnerCredentials? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) { return null; }
            var creds = JsonSerializer.Deserialize<RunnerCredentials>(File.ReadAllText(path), Json);
            return creds is { HubUrl.Length: > 0, RunnerId.Length: > 0, Token.Length: > 0 } ? creds : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Forget the saved credentials (<c>AgentOs.RemoteAgent unpair</c>). True when there were any.</summary>
    internal static bool Delete(string path)
    {
        if (!File.Exists(path)) { return false; }
        File.Delete(path);
        return true;
    }

    /// <summary>Save for this OS user only (0600 off Windows).</summary>
    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>The exchange URL for a server address as a member types it ("https://host", "host/", "host:6180").</summary>
    internal static Uri ExchangeUri(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        var s = server.Trim().TrimEnd('/');
        if (!s.Contains("://", StringComparison.Ordinal)) { s = "https://" + s; }
        return new Uri(s + "/runner/pair/exchange", UriKind.Absolute);
    }

    /// <summary>Redeem a one-time code. Null when the server says it is invalid, used or expired.</summary>
    internal static async Task<RunnerCredentials?> ExchangeAsync(HttpClient http, string server, string code, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(ExchangeUri(server), new { code = code.Trim() }, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { return null; }
        var dto = await response.Content.ReadFromJsonAsync<ExchangeDto>(Json, ct).ConfigureAwait(false);
        return dto is null || dto.RunnerId == Guid.Empty || string.IsNullOrEmpty(dto.Token) || string.IsNullOrEmpty(dto.HubUrl)
            ? null
            : new RunnerCredentials(dto.HubUrl, dto.RunnerId.ToString(), dto.Token);
    }

    private sealed record ExchangeDto(Guid RunnerId, string Token, string HubUrl);
}
