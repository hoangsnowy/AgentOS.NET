// Email/SMTP options. Bound from the "Email" configuration section. When SmtpHost is empty the
// module registers a NullEmailSender (logs only) so the host boots without an SMTP server — the
// full Aspire stack injects the MailHog endpoint; production injects a real provider via secrets.

namespace AgentOs.Modules.Tenants.Email;

/// <summary>SMTP configuration for app-sent mail (invitations, notifications).</summary>
public sealed class EmailOptions
{
    /// <summary>Section name for <c>Configuration.GetSection</c>.</summary>
    public const string SectionName = "Email";

    /// <summary>SMTP host. Empty = no real sender (NullEmailSender logs the message instead).
    /// Dev: <c>localhost</c> (MailHog). Prod: e.g. <c>smtp.sendgrid.net</c>.</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>SMTP port. MailHog = 1025; STARTTLS providers = 587; implicit-TLS = 465.</summary>
    public int SmtpPort { get; set; } = 1025;

    /// <summary>Envelope-from address.</summary>
    public string From { get; set; } = "noreply@agentic.local";

    /// <summary>Display name shown to recipients.</summary>
    public string FromName { get; set; } = "AgentOS.NET";

    /// <summary>Use STARTTLS (port 587 providers). Port 465 implies implicit TLS regardless.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>SMTP auth username. Empty = no authentication (MailHog / open relay).</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>SMTP auth password / API key. Supply via secrets in production — never commit.</summary>
    public string Password { get; set; } = string.Empty;
}
