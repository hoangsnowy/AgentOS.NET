// E1.3 — the published OpenAPI "v1" document: title, version, how to authenticate (Keycloak JWT or an AgentOS.NET API
// token, both as a Bearer token) and the deprecation policy. The servers list is left out so the committed copy
// (docs/api/openapi-v1.json) is the same on every machine; clients pass their own base URL.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.SharedKernel.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgentOs.Api;

/// <summary>Describes the <c>v1</c> OpenAPI document.</summary>
public sealed class ApiDocumentV1 : IOpenApiDocumentTransformer
{
    /// <summary>The bearer security scheme name.</summary>
    public const string BearerScheme = "Bearer";

    /// <inheritdoc />
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        System.ArgumentNullException.ThrowIfNull(document);
        document.Info = new OpenApiInfo
        {
            Title = "AgentOS.NET API",
            Version = ApiVersions.V1,
            Description =
                "Versioned REST API of AgentOS.NET. Authenticate with `Authorization: Bearer <token>` — a Keycloak access token "
                + "(people) or an AgentOS.NET API token `aos_…` (personal tokens and service accounts, created in Settings › API tokens; "
                + "scopes: read = GET, run = also POST/PUT/PATCH/DELETE, admin = also tenant-admin routes). "
                + "The unversioned routes (the same paths without `/v1`) are deprecated and answer with `Deprecation`, `Sunset` and "
                + "`Link: rel=\"successor-version\"` headers; see " + ApiVersions.PolicyUrl + ".",
            License = new OpenApiLicense { Name = "MIT" },
        };
        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "A Keycloak access token (JWT) or an AgentOS.NET API token (aos_…).",
        };
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, document)] = [] }];
        return Task.CompletedTask;
    }
}
