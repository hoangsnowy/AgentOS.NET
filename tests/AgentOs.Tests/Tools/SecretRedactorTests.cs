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
    // ── review of PR #299: shapes that used to pass through ──
    [InlineData("SECRET_KEY=abc python app.py", "abc python")]
    [InlineData("export OPENAI_KEY=abc123def456", "abc123def456")]
    [InlineData("STRIPE_KEY=rk_live_short npm start", "rk_live_short")]
    [InlineData("export ENCRYPTION_KEY=myencryptionvalue", "myencryptionvalue")]
    [InlineData("AZURE_STORAGE_KEY=storagevalue az storage blob list", "storagevalue")]
    [InlineData("KEY=plainvalue ./run.sh", "plainvalue")]
    [InlineData("az storage account keys list --account-key accountvalue", "accountvalue")]
    [InlineData("tool --github-token tokvalue", "tokvalue")]
    [InlineData("tool --client-secret clientvalue", "clientvalue")]
    [InlineData("dotnet user-secrets set Llm:AzureOpenAi:ApiKey 0123456789abcdef0123456789abcdef", "0123456789abcdef")]
    [InlineData("dotnet user-secrets set Llm:Claude:ApiKey shortvalue", "shortvalue")]
    [InlineData("dotnet user-secrets set --project src/AgentOs.Api \"Llm:Claude:ApiKey\" \"quoted value\"", "quoted value")]
    [InlineData("aws configure set aws_secret_access_key wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI")]
    [InlineData("aws configure set aws_session_token sessionvalue", "sessionvalue")]
    [InlineData("gh secret set FOO --body s3cret", "s3cret")]
    [InlineData("gh secret set FOO -b s3cret --repo o/r", "s3cret")]
    [InlineData("gh secret set FOO --body=\"s3cret value\"", "s3cret value")]
    [InlineData("az keyvault secret set --vault-name v --name n --value kvsecret", "kvsecret")]
    [InlineData("az login -u x -p Hunter2", "Hunter2")]
    [InlineData("az acr login -n reg -u me -p acrpass", "acrpass")]
    [InlineData("curl \"https://acct.blob.core.windows.net/c/b?sv=2022-11-02&se=2026-12-01&sig=AbCdEf%2Bsignature%3D\"", "AbCdEf")]
    [InlineData("curl https://x/b?sig=sigvalue", "sigvalue")]
    [InlineData("curl \"https://login.example.com/callback?code=authcodevalue&state=xyz\"", "authcodevalue")]
    [InlineData("curl \"https://api.example.com/v1?token=querytoken\"", "querytoken")]
    [InlineData("curl \"https://s3.amazonaws.com/b/k?X-Amz-Signature=amzsigvalue&X-Amz-Expires=60\"", "amzsigvalue")]
    [InlineData("git clone https://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst@dev.azure.com/org/p/_git/r", "abcdefghijklmnop")]
    [InlineData("openssl pkcs12 -in cert.pfx -passin pass:pfxpassword -nodes", "pfxpassword")]
    [InlineData("openssl enc -aes-256-cbc -pass pass:\"enc pass\" -in a", "enc pass")]
    [InlineData("htpasswd -b .htpasswd admin htsecret", "htsecret")]
    [InlineData("htpasswd -cb /etc/nginx/.htpasswd admin htsecret", "htsecret")]
    [InlineData("htpasswd -nb admin htsecret", "htsecret")]
    [InlineData("echo eyJhbGciOiJub25lIn0.eyJzdWIiOiIxMjM0NTY3ODkwIn0.", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    [InlineData("echo ghu_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "ghu_aaaa")]
    [InlineData("echo ghr_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "ghr_bbbb")]
    [InlineData("git remote set-url origin https://github_pat_11ABCDEFG0123456789_abcdefghij@github.com/o/r", "github_pat_11")]
    [InlineData("curl -H \"Authorization: Basic dXNlcjpwYXNzd29yZA==\" https://x", "dXNlcjpwYXNzd29yZA")]
    [InlineData("curl -H 'Proxy-Authorization: Basic dXNlcjpwYXNz' https://x", "dXNlcjpwYXNz")]
    [InlineData("password: yamlpass", "yamlpass")]
    [InlineData("  token: yamltoken", "yamltoken")]
    [InlineData("AZURE_DEVOPS_EXT_PAT=patvalue az devops login", "patvalue")]
    [InlineData("echo 0123456789abcdef0123456789abcdef01234567", "0123456789abcdef")]
    [InlineData("echo Zm9vYmFyQmF6UXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE1OT1BRUlNU", "Zm9vYmFyQmF6")]
    [InlineData("tool --webhook 7d9f3b2a1c8e4f6a5b0d9c8e7f6a5b4c", "7d9f3b2a1c8e")]
    [InlineData("echo k7q2m9x4p3w8z5r6t1y0u2i3o4p5a6s7d8f9g0h1", "k7q2m9x4p3w8")]
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
    [InlineData("git checkout 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("git show --stat 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("git log -1 --format=%H 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("git revert --no-edit 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("git cherry-pick 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("git reset --hard 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21")]
    [InlineData("docker pull mcr.microsoft.com/dotnet/sdk@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("az account set --subscription 3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("src/AgentOs.Modules.Tools/Persistence/Migrations/20261101090221_MemberComputerEvidence.cs")]
    [InlineData("dotnet test --filter-class \"*MemberComputerEvidenceRealPostgresTests\"")]
    [InlineData("git clone ssh://git@github.com/o/r.git")]
    [InlineData("git clone https://myorg@dev.azure.com/myorg/project/_git/repo")]
    [InlineData("gh issue create --title \"Fix login\" --body \"The login page breaks\"")]
    [InlineData("psql -h localhost -p 5432 -U postgres")]
    [InlineData("dotnet user-secrets list")]
    [InlineData("ls src/Components/VeryLongComponentNameForTheBoardWindow")]
    [InlineData("git switch -c feature/issue-7-fix-the-login-page-for-members")]
    [InlineData("curl \"https://api.example.com/items?page=2&sort=name\"")]
    [InlineData("openssl pkcs12 -in cert.pfx -passin env:PFX_PASSWORD")]
    public void Redact_OrdinaryCommandOrPath_IsUnchanged(string text) =>
        SecretRedactor.Redact(text).ShouldBe(text);

    [Fact]
    public void Redact_HexOutsideAGitCommand_IsMasked()
    {
        // Decision: a 40-hex SHA is kept only right after a git command that names a commit — anywhere else a long hex
        // run is treated as a secret (an API key, a webhook secret), because the two look the same.
        SecretRedactor.Redact("echo 3f2a9c1d7e8b4a6f0c5d2e1b9a8f7c6d5e4b3a21").ShouldBe("echo [redacted]");
    }

    [Fact]
    public void Redact_LongInput_IsCutBeforeScanningAndTheRestMasked()
    {
        var text = new string('a', 300) + " " + string.Concat(Enumerable.Repeat("word ", 1000)) + "--password tailsecret";

        var redacted = SecretRedactor.Redact(text);

        redacted.Length.ShouldBeLessThan(SecretRedactor.MaxScanLength + 64);
        redacted.ShouldNotContain("tailsecret");
        redacted.ShouldContain("[redacted:");
        redacted.ShouldContain("more characters]");
    }

    [Fact]
    public void Redact_SecretStraddlingTheScanCut_LeavesNoReadablePrefix()
    {
        // The cut goes back to a word boundary: the half of a token the cut would split is dropped, not left readable.
        var prefix = string.Concat(Enumerable.Repeat("x ", (SecretRedactor.MaxScanLength - 10) / 2));
        var redacted = SecretRedactor.Redact(prefix + "ghp_" + new string('z', 36));

        redacted.ShouldNotContain("ghp_");
        redacted.ShouldNotContain("zzzz");
    }

    [Fact]
    public void Summary_HugeSingleLine_StaysBoundedAndFast()
    {
        var huge = "echo " + string.Concat(Enumerable.Repeat("Zm9vYmFy ", 200_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var summary = SecretRedactor.Summary(huge);

        watch.Stop();
        summary.Length.ShouldBeLessThanOrEqualTo(SecretRedactor.DefaultMaxLength);
        watch.Elapsed.ShouldBeLessThan(System.TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Summary_CountsTheLinesThatFollow()
    {
        SecretRedactor.Summary("first\nsecond\r\nthird\n").ShouldBe("first (+2 more lines)");
        SecretRedactor.Summary("only\nsecond").ShouldBe("only (+1 more line)");
    }

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
