// `AgentOs.RemoteAgent pair <server> <code>`: the runner trades a typed code for its credentials and remembers them.

using System.Net;
using System.Text;
using AgentOs.RemoteAgent;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class RunnerCredentialsTests
{
    [Theory]
    [InlineData("https://lab.dev.localhost:6180", "https://lab.dev.localhost:6180/runner/pair/exchange")]
    [InlineData("https://lab.dev.localhost:6180/", "https://lab.dev.localhost:6180/runner/pair/exchange")]
    [InlineData("lab.dev.localhost:6180", "https://lab.dev.localhost:6180/runner/pair/exchange")]
    [InlineData(" http://localhost:5199 ", "http://localhost:5199/runner/pair/exchange")]
    public void ExchangeUri_AsAPersonTypesIt_PointsAtTheExchange(string server, string expected) =>
        RunnerCredentials.ExchangeUri(server).ToString().ShouldBe(expected);

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Join(Path.GetTempPath(), "agentos-creds-" + Guid.NewGuid().ToString("N"), "runner.json");
        try
        {
            new RunnerCredentials("https://h/hubs/remote-agent", Guid.NewGuid().ToString(), "tok").Save(path);
            var loaded = RunnerCredentials.Load(path);
            loaded.ShouldNotBeNull();
            loaded!.Token.ShouldBe("tok");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingOrBroken_IsNull()
    {
        var path = Path.Join(Path.GetTempPath(), "agentos-creds-" + Guid.NewGuid().ToString("N") + ".json");
        RunnerCredentials.Load(path).ShouldBeNull();
        File.WriteAllText(path, "{ not json");
        try { RunnerCredentials.Load(path).ShouldBeNull(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ExchangeAsync_ValidCode_ReturnsCredentials()
    {
        var runner = Guid.NewGuid();
        using var http = new HttpClient(new Stub(HttpStatusCode.OK,
            $$"""{"runnerId":"{{runner}}","token":"tok-1","hubUrl":"https://h/hubs/remote-agent"}"""));

        var creds = await RunnerCredentials.ExchangeAsync(http, "https://h", "K7F2-9QXM");

        creds.ShouldNotBeNull();
        creds!.RunnerId.ShouldBe(runner.ToString());
        creds.HubUrl.ShouldBe("https://h/hubs/remote-agent");
    }

    [Fact]
    public async Task ExchangeAsync_UsedOrExpiredCode_IsNull()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.NotFound, "\"Pairing code is invalid, already used, or expired.\""));
        (await RunnerCredentials.ExchangeAsync(http, "https://h", "K7F2-9QXM")).ShouldBeNull();
    }

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/runner/pair/exchange");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
