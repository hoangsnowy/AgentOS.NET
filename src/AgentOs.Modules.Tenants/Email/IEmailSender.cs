// App-side email seam. Implemented by MailKitEmailSender (real SMTP) or NullEmailSender (no-op log).
// Used for AgentOS.NET-owned mail such as invitation links. NOTE: Keycloak still sends its OWN auth
// emails (verify-email, reset-password) via the realm smtpServer — this seam is for app-originated
// mail only, not those.

using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Modules.Tenants.Email;

/// <summary>Sends an email from the application.</summary>
public interface IEmailSender
{
    /// <summary>Send an HTML email (with an optional plain-text alternative).</summary>
    /// <param name="toEmail">Recipient address.</param>
    /// <param name="subject">Subject line.</param>
    /// <param name="htmlBody">HTML body.</param>
    /// <param name="textBody">Optional plain-text alternative for non-HTML clients.</param>
    Task SendAsync(string toEmail, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default);
}
