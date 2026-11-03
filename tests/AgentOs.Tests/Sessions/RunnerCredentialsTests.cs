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

    // GAP-05: the runner's token never travels over plain http:// — loopback in Development is the only exception.
    [Theory]
    [InlineData("https://agentos.example.com/hubs/remote-agent", false, true)]
    [InlineData("https://localhost:5180/hubs/remote-agent", false, true)]
    [InlineData("http://agentos.example.com/hubs/remote-agent", false, false)]
    [InlineData("http://agentos.example.com/hubs/remote-agent", true, false)]
    [InlineData("http://localhost:5199/hubs/remote-agent", false, false)]
    [InlineData("http://localhost:5199/hubs/remote-agent", true, true)]
    [InlineData("http://127.0.0.1:5199/hubs/remote-agent", true, true)]
    [InlineData("http://[::1]:5199/hubs/remote-agent", true, true)]
    [InlineData("ws://agentos.example.com/hubs/remote-agent", true, false)]
    [InlineData("not a url", true, false)]
    public void InsecureReason_AllowsHttpsAndDevelopmentLoopbackOnly(string url, bool development, bool allowed) =>
        (RunnerCredentials.InsecureReason(url, development) is null).ShouldBe(allowed);

    [Fact]
    public async Task ExchangeAsync_PlainHttpServer_IsRefusedBeforeSendingTheCode()
    {
        var stub = new Stub(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(stub);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => RunnerCredentials.ExchangeAsync(http, "http://agentos.example.com", "K7F2-9QXM"));

        ex.Message.ShouldContain("http://");
        stub.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task ExchangeAsync_ServerNamesAPlainHttpHub_IsRefused()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK,
            $$"""{"runnerId":"{{Guid.NewGuid()}}","token":"tok-1","hubUrl":"http://evil.example/hubs/remote-agent"}"""));

        await Should.ThrowAsync<InvalidOperationException>(() => RunnerCredentials.ExchangeAsync(http, "https://h", "K7F2-9QXM"));
    }

    [Fact]
    public async Task ExchangeAsync_LoopbackHttp_InDevelopment_IsAllowed()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK,
            $$"""{"runnerId":"{{Guid.NewGuid()}}","token":"tok-1","hubUrl":"http://localhost:5199/hubs/remote-agent"}"""));

        (await RunnerCredentials.ExchangeAsync(http, "http://localhost:5199", "K7F2-9QXM", development: true)).ShouldNotBeNull();
    }

    // Owns the responses it hands out and disposes them with itself (the HttpClient disposes the handler).
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        public int Requests => _responses.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/runner/pair/exchange");
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _responses.ForEach(r => r.Dispose()); }
            base.Dispose(disposing);
        }
    }
}
