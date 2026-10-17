// M5-adjacent — tests for the app-sent email layer (MailKit sender selection + invitation body).
// The real MailKit SMTP path needs a server, so we test the parts that are pure: the invitation
// body builder and the DI switch (NullEmailSender when no SMTP host, MailKitEmailSender when set).

using System.Collections.Generic;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public class EmailSenderTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildModule(IConfiguration config)
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        new TenantsModule().AddServices(sc, config);
        return sc.BuildServiceProvider();
    }

    [Fact]
    public void Build_Invitation_IncludesUrlRoleAndTenant()
    {
        var (subject, html, text) = InvitationEmail.Build(
            "acme", "admin", "https://localhost:5180/signup?invite=abc123");

        subject.ShouldContain("AgentOS.NET");
        html.ShouldContain("https://localhost:5180/signup?invite=abc123");
        html.ShouldContain("acme");
        html.ShouldContain("admin");
        text.ShouldContain("https://localhost:5180/signup?invite=abc123");
        text.ShouldContain("acme");
    }

    [Fact]
    public async Task NullEmailSender_DoesNotThrow()
    {
        var sender = new NullEmailSender(NullLogger<NullEmailSender>.Instance);
        await Should.NotThrowAsync(() => sender.SendAsync("a@b.com", "subj", "<p>hi</p>", "hi"));
    }

    [Fact]
    public void AddServices_NoSmtpHost_RegistersNullEmailSender()
    {
        using var sp = BuildModule(Config(new Dictionary<string, string?>()));
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<NullEmailSender>();
    }

    [Fact]
    public void AddServices_WithSmtpHost_RegistersMailKitEmailSender()
    {
        using var sp = BuildModule(Config(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = "localhost",
            ["Email:SmtpPort"] = "1025",
        }));
        sp.GetRequiredService<IEmailSender>().ShouldBeOfType<MailKitEmailSender>();
    }

    [Fact]
    public void AddServices_BindsEmailOptions()
    {
        using var sp = BuildModule(Config(new Dictionary<string, string?>
        {
            ["Email:SmtpHost"] = "smtp.example.com",
            ["Email:SmtpPort"] = "587",
            ["Email:From"] = "no-reply@example.com",
            ["Email:UseStartTls"] = "true",
        }));
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value;
        opts.SmtpHost.ShouldBe("smtp.example.com");
        opts.SmtpPort.ShouldBe(587);
        opts.From.ShouldBe("no-reply@example.com");
        opts.UseStartTls.ShouldBeTrue();
    }
}
