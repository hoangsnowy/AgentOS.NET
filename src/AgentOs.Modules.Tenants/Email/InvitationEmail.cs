// Builds the invitation email body shared by both invite call sites (the /tenants/{id}/invitations
// HTTP endpoint and the Users desktop app), so the wording + styling stay in one place. Mirrors the
// AgentOS.NET login theme (Inter, Breeze accent #3daee9) so the email feels like part of the product.

using System.Globalization;

namespace AgentOs.Modules.Tenants.Email;

public static class InvitationEmail
{
    /// <summary>Build the subject + HTML + plain-text bodies for an invitation to join a workspace.</summary>
    public static (string Subject, string HtmlBody, string TextBody) Build(string tenantId, string role, string acceptUrl)
    {
        const string subject = "You're invited to AgentOS.NET";

        var html = string.Format(
            CultureInfo.InvariantCulture,
            """
            <div style="font-family:Inter,'Segoe UI',system-ui,sans-serif;max-width:480px;margin:0 auto;color:#0f172a">
              <h2 style="font-size:18px;margin:0 0 12px">You're invited to AgentOS.NET</h2>
              <p style="font-size:14px;line-height:1.5;color:#334155">
                You've been invited to join the <strong>{0}</strong> workspace as <strong>{1}</strong>.
              </p>
              <p style="margin:20px 0">
                <a href="{2}" style="display:inline-block;background:#3daee9;color:#fff;text-decoration:none;
                   padding:10px 18px;border-radius:6px;font-weight:600;font-size:14px">Accept invitation</a>
              </p>
              <p style="font-size:12px;color:#64748b">Or paste this link into your browser:<br>
                <code style="word-break:break-all">{2}</code></p>
            </div>
            """,
            tenantId, role, acceptUrl);

        var text = string.Format(
            CultureInfo.InvariantCulture,
            "You're invited to join the {0} workspace on AgentOS.NET as {1}.\nAccept the invitation: {2}",
            tenantId, role, acceptUrl);

        return (subject, html, text);
    }
}
