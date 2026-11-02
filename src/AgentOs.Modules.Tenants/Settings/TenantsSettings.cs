// GAP-01 (docs/security/threat-model.md §15) — open self-signup is a platform decision (ADR-0011), not a hard-coded
// behaviour. Anyone who can reach /signup could create a tenant and become its admin; outside Development that is now
// OFF by default, and a platform admin turns it on in Settings › Platform (or the operator pins it with
// Settings:Locks:Tenants:OpenSignup). Joining an existing workspace with an invitation is never affected.

using System.Collections.Generic;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.Tenants.Settings;

/// <summary>Declares <c>Tenants:*</c> settings.</summary>
public sealed class TenantsSettings(bool openSignupDefault) : ISettingsContributor
{
    /// <summary>Platform switch: may anyone create a new workspace from the sign-up page?</summary>
    public const string OpenSignupKey = "Tenants:OpenSignup";

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return new(OpenSignupKey, SettingKind.Boolean, "Safety", "Open self-signup")
        {
            DefaultValue = openSignupDefault ? "true" : "false",
            Scope = SettingScope.Platform,
            Help = "Let anyone create a new workspace (and become its admin) from the sign-up page. Off outside Development; invitations always work.",
        };
    }
}
