// Settings for the Admin REST client. Bound from the "Auth:Keycloak:Admin" config section. The
// Aspire AppHost forwards the dev master admin credentials; in production the operator points
// these at a service-account client with the realm-management role.

namespace AgentOs.Modules.Tenants.Keycloak;

/// <summary>Keycloak admin REST settings.</summary>
public sealed class KeycloakAdminOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth:Keycloak:Admin";

    /// <summary>Base URL of the Keycloak server, e.g. <c>http://localhost:8080</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Realm that holds the tenants, e.g. <c>agentic</c>.</summary>
    public string Realm { get; set; } = "agentic";

    /// <summary>Master-realm admin username (password grant; ignored when <see cref="ClientSecret"/> is set).</summary>
    public string Username { get; set; } = "admin";

    /// <summary>Master-realm admin password (password grant; ignored when <see cref="ClientSecret"/> is set).</summary>
    public string Password { get; set; } = "admin";

    /// <summary>Client id used to obtain the admin access token: <c>admin-cli</c> (password grant, dev) or a
    /// confidential service-account client in <see cref="Realm"/> (client-credentials, on-prem/prod).</summary>
    public string ClientId { get; set; } = "admin-cli";

    /// <summary>Secret of a confidential service-account client in <see cref="Realm"/>. When set, the admin
    /// token is obtained with the <c>client_credentials</c> grant from that realm (no master-realm user is
    /// involved — the client's service account holds only the realm-management roles the app needs:
    /// <c>manage-users</c>, <c>view-users</c>, <c>query-users</c>, <c>view-realm</c>). Empty = the master-realm
    /// password grant with <see cref="Username"/>/<see cref="Password"/> (the Aspire dev stack).</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>True when the client-credentials grant is configured.</summary>
    public bool UsesClientCredentials => !string.IsNullOrEmpty(ClientSecret);
}
