// The one rule for what may leave a member's computer as tool-call evidence: credential-shaped text is redacted, a
// summary is one bounded line. The runner and the server both apply it (the runner links SecretRedactor.cs).

using System.Linq;
using AgentOs.Domain.Tools;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tools;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("curl -H \"Authorization: Bearer abcdefghijklmnop\" https://api.example.com", "abcdefghijklmnop")]
    [InlineData("curl -H 'authorization: token ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' https://api.github.com", "ghp_aaaa")]
    [InlineData("curl -H \"X-Api-Key: s3cr3t-value\" https://x", "s3cr3t-value")]
    [InlineData("curl -H \"Cookie: session=abc123\" https://x", "abc123")]
    [InlineData("export ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnop")]
    [InlineData("echo sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-ant-api03")]
    [InlineData("OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuv npm test", "sk-proj-abcdef")]
    [InlineData("git clone https://x-access-token:ghs_bbbbbbbbbbbbbbbbbbbbbbbbbbbb@github.com/o/r.git", "ghs_bbbb")]
    [InlineData("git clone https://octocat:hunter2@github.com/o/r.git", "hunter2")]
    [InlineData("echo github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz", "github_pat_11")]
    [InlineData("GITHUB_TOKEN=gho_cccccccccccccccccccccccccccc gh pr create", "gho_cccc")]
    [InlineData("aws configure set aws_access_key_id AKIAABCDEFGHIJKLMNOP", "AKIAABCDEFGHIJKLMNOP")]
    [InlineData("psql \"Host=db;Username=app;Password=Pa55w0rd!;Database=x\"", "Pa55w0rd!")]
    [InlineData("dotnet user-secrets set ConnectionStrings:Db \"Server=.;User Id=sa;Pwd=topsecret;\"", "topsecret")]
    [InlineData("mysql -u root -psupersecret mydb", "supersecret")]
    [InlineData("mysql -u root -p supersecret mydb", "supersecret")]
    [InlineData("sshpass -p 'hunter2' ssh user@host", "hunter2")]
    [InlineData("sqlcmd -S . -U sa -P Str0ng!Pass -Q \"select 1\"", "Str0ng!Pass")]
    [InlineData("redis-cli -a myredispass ping", "myredispass")]
    [InlineData("docker login -u me -p dockerpass registry.example.com", "dockerpass")]
    [InlineData("tool --password hunter2 --verbose", "hunter2")]
    [InlineData("tool --token=abc.def.ghi", "abc.def.ghi")]
    [InlineData("tool --api-key \"quoted secret\"", "quoted secret")]
    [InlineData("curl -u admin:letmein https://x", "letmein")]
    [InlineData("echo eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("{\"client_secret\": \"abcd1234\"}", "abcd1234")]
    [InlineData("echo xoxb-123456789012-abcdefghijkl", "xoxb-1234")]
    public void Redact_CredentialShapedText_IsReplaced(string text, string secret)
    {
        var redacted = SecretRedactor.Redact(text);

        redacted.ShouldNotContain(secret);
        redacted.ShouldContain(SecretRedactor.Mask);
    }

    [Theory]
    [InlineData("dotnet build TeacherHub.slnx -c Release")]
    [InlineData("mkdir -p src/Components/Pages")]
    [InlineData("dotnet run -p src/App")]
    [InlineData("git push -u origin issue-7-ai-fix")]
    [InlineData("npm test -- --reporter=dot")]
    [InlineData("src/Services/TokenService.cs")]
    [InlineData("grep -rn \"tokens:\" src")]
    public void Redact_OrdinaryCommandOrPath_IsUnchanged(string text) =>
        SecretRedactor.Redact(text).ShouldBe(text);

    [Fact]
    public void Redact_KeepsTheUserOfAUrlCredential()
    {
        SecretRedactor.Redact("git clone https://octocat:hunter2@github.com/o/r.git")
            .ShouldBe("git clone https://octocat:[redacted]@github.com/o/r.git");
    }

    [Fact]
    public void Redact_TokenAsTheUserOfAUrl_KeepsTheRepository()
    {
        // Found on a live run: the header rule used to read "x-access-token:" as a header and swallow the repo URL.
        SecretRedactor.Redact("git clone https://x-access-token:ghs_bbbbbbbbbbbbbbbbbbbbbbbbbbbb@github.com/acme/shop.git .")
            .ShouldBe("git clone https://x-access-token:[redacted]@github.com/acme/shop.git .");
    }

    [Fact]
    public void Redact_PrivateKeyBlock_IsReplacedWhole()
    {
        var redacted = SecretRedactor.Redact("key=-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk\n-----END OPENSSH PRIVATE KEY-----");

        redacted.ShouldNotContain("b3BlbnNzaC1rZXk");
        redacted.ShouldContain("[redacted private key]");
    }

    [Fact]
    public void Summary_MultiLineCommand_KeepsOnlyTheFirstLine()
    {
        // A heredoc's body is file content — it never leaves the computer.
        var summary = SecretRedactor.Summary("cat > appsettings.json <<'EOF'\n{ \"Secret\": \"x\" }\nmore\nEOF");

        summary.ShouldBe("cat > appsettings.json <<'EOF' (+3 more lines)");
    }

    [Fact]
    public void Summary_LongText_IsBoundedTo500Characters()
    {
        var summary = SecretRedactor.Summary("echo " + new string('a', 2000));

        summary.Length.ShouldBe(SecretRedactor.DefaultMaxLength);
        summary.ShouldEndWith("…");
    }

    [Fact]
    public void Summary_RedactsBeforeCutting()
    {
        // A secret straddling the cut must not leave a readable prefix behind.
        var summary = SecretRedactor.Summary(new string('x', 480) + " ghp_" + new string('d', 36), 500);

        summary.ShouldNotContain("ghp_dddd");
    }

    [Fact]
    public void Bound_NeverSplitsASurrogatePair()
    {
        var text = new string('a', 18) + string.Concat(Enumerable.Repeat("😀", 10));

        var bounded = SecretRedactor.Bound(text, 20);

        bounded.Length.ShouldBeLessThanOrEqualTo(20);
        char.IsHighSurrogate(bounded[^2]).ShouldBeFalse();
        bounded.ShouldEndWith("…");
    }

    [Fact]
    public void Bound_ControlCharacters_BecomeSpaces() =>
        SecretRedactor.Bound("a\tb\u0007c").ShouldBe("a b c");

    [Fact]
    public void Summary_EmptyOrWhitespace_IsEmpty()
    {
        SecretRedactor.Summary(null).ShouldBe(string.Empty);
        SecretRedactor.Summary("   \n  ").ShouldBe(string.Empty);
    }
}
