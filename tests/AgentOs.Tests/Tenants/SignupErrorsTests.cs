// Public sign-up (POST /tenants/register, the /signup page) talks to an ANONYMOUS caller. Keycloak's raw error body
// (realm internals, password-policy detail, other accounts) used to be echoed straight back as `error`; it now stays in
// the server log and the caller gets a plain product message — while our own validation messages still pass through.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Email;
using AgentOs.Modules.Tenants.Endpoints;
using AgentOs.Modules.Tenants.Keycloak;
using AgentOs.SharedKernel.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class SignupErrorsTests
{
    private const string RawBody = "{\"errorMessage\":\"User exists with same email\",\"field\":\"email\",\"realm\":\"agentic-internal\"}";

    public static TheoryData<string, int, string> IdentityFailures => new()
    {
        { "conflict", StatusCodes.Status409Conflict, SignupErrors.Taken },
        { "refused", StatusCodes.Status503ServiceUnavailable, SignupErrors.Unavailable },
        { "left-behind", StatusCodes.Status503ServiceUnavailable, SignupErrors.Unavailable },
        { "unreachable", StatusCodes.Status503ServiceUnavailable, SignupErrors.Unreachable },
    };

    [Theory]
    [MemberData(nameof(IdentityFailures))]
    public async Task Register_IdentityServiceFails_ReturnsAPlainMessage_KeepsKeycloaksBodyInTheLog(string failure, int status, string message)
    {
        var (code, body, logs) = await RegisterAsync(Failure(failure));

        code.ShouldBe(status);
        body.ShouldNotContain("agentic-internal");
        body.ShouldNotContain("errorMessage");
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe(message);
        var logged = logs.ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogLevel.Error);
        logged.Exception.ShouldNotBeNull();
    }

    // Our own validation messages are written for the visitor: they still come back as they are, with a 400.
    [Fact]
    public async Task Register_ValidationMessage_StillComesBack()
    {
        var (code, body, logs) = await RegisterAsync(new InvalidOperationException("Username is required."));

        code.ShouldBe(StatusCodes.Status400BadRequest);
        JsonDocument.Parse(body).RootElement.GetProperty("error").GetString().ShouldBe("Username is required.");
        logs.ShouldBeEmpty();
    }

    [Fact]
    public void ForPublic_MapsIdentityFailures_PassesValidationThrough()
    {
        SignupErrors.ForPublic(Failure("conflict")).ShouldBe(SignupErrors.Taken);
        SignupErrors.ForPublic(Failure("refused")).ShouldBe(SignupErrors.Unavailable);
        SignupErrors.ForPublic(new KeycloakAdminException("User management requires Keycloak, which isn't configured")).ShouldBe(SignupErrors.Unavailable);
        SignupErrors.ForPublic(new TaskCanceledException()).ShouldBe(SignupErrors.Unreachable);
        SignupErrors.ForPublic(new InvalidOperationException("Workspace 'acme' already exists.")).ShouldBe("Workspace 'acme' already exists.");
        SignupErrors.IsIdentityFailure(new InvalidOperationException("x")).ShouldBeFalse();
    }

    // The client raises the typed exception (status kept for the mapping; the raw body for the log and admins only).
    [Fact]
    public void Refused_KeepsTheStatus_AndTheDetailForTheLog()
    {
        var ex = KeycloakAdminException.Refused("create-user", HttpStatusCode.Conflict, RawBody);

        ex.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ex.Message.ShouldContain("create-user failed (409)");
        ex.Message.ShouldContain("agentic-internal");
        ex.ShouldBeAssignableTo<InvalidOperationException>();   // administrator-side handlers keep catching it
    }

    private static Exception Failure(string kind) => kind switch
    {
        "conflict" => KeycloakAdminException.Refused("create-user", HttpStatusCode.Conflict, RawBody),
        "refused" => KeycloakAdminException.Refused("create-user", HttpStatusCode.BadRequest, RawBody),
        "left-behind" => KeycloakUserLeftBehindException.For("kc-user-9", KeycloakAdminException.Refused("grant-roles", HttpStatusCode.InternalServerError, RawBody)),
        _ => new HttpRequestException("Connection refused (kc.internal:8443) " + RawBody),
    };

    // The real endpoint delegate (as OpenSignupAndAzureEndpointTests drives it), with a sign-up service that fails.
    private static async Task<(int Status, string Body, IReadOnlyList<FakeLogRecord> Logs)> RegisterAsync(Exception failure)
    {
        var signup = Substitute.For<ITenantSignupService>();
        signup.SignupAsync(Arg.Any<TenantSignupRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var logs = new FakeLogCollector();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FakeLoggerProvider(logs));
        builder.Services.AddSingleton(Substitute.For<ITenantsRepository>());
        builder.Services.AddSingleton(Substitute.For<IKeycloakAdminClient>());
        builder.Services.AddSingleton(Substitute.For<IAuditLog>());
        builder.Services.AddSingleton(Substitute.For<IEmailSender>());
        builder.Services.AddSingleton(Substitute.For<ITenantContext>());
        builder.Services.AddSingleton(signup);
        builder.Services.AddSingleton(new AgentOs.SharedKernel.Hosting.PublicOrigin("https://agentos.example.com", allowRequestFallback: false));
        await using var app = builder.Build();
        app.MapTenantEndpoints();
        var register = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "TenantsRegister");

        var request = JsonSerializer.SerializeToUtf8Bytes(new RegisterTenantRequest("acme", "Acme", "alice", "alice@acme.test", "Correct-Horse-9-Battery!", null));
        using var requestBody = new MemoryStream(request);
        using var responseBody = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = request.Length;
        http.Request.Body = requestBody;
        http.Response.Body = responseBody;
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new HasBody());

        await register.RequestDelegate!(http);

        return (http.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()),
            [.. logs.GetSnapshot().Where(r => r.Category == "AgentOs.Modules.Tenants.Signup")]);
    }

    private sealed class HasBody : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
