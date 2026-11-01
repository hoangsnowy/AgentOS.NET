// Batch 3 — credential masking: secret-shaped substrings must not survive into log sinks.
using AgentOs.SharedKernel.Logging;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Logging;

public class LogSafeMaskingTests
{
    [Fact]
    public void MaskSecrets_AnthropicKey_MasksPayload()
    {
        var masked = LogSafe.MaskSecrets("calling with key sk-ant-api03-AbCdEfGh123456 done");
        masked.ShouldNotContain("AbCdEfGh123456");
        masked.ShouldContain("sk-ant-");
        masked.ShouldContain("***");
    }

    [Theory]
    [InlineData("ghp_1234567890abcdef")]
    [InlineData("gho_1234567890abcdef")]
    [InlineData("ghs_1234567890abcdef")]
    [InlineData("github_pat_1234567890abcdef")]
    [InlineData("GHP_1234567890ABCDEF")] // uppercased copies must not bypass the mask
    [InlineData("SK-ANT-API03-ABCDEF12345")]
    public void MaskSecrets_TokenPrefixes_AnyCase_MasksPayload(string token)
    {
        var masked = LogSafe.MaskSecrets($"token={token}");
        masked.ShouldNotContain(token);
        masked.ShouldContain("***");
    }

    [Fact]
    public void MaskSecrets_BearerHeader_MasksToken()
    {
        var masked = LogSafe.MaskSecrets("Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig");
        masked.ShouldNotContain("eyJhbGciOiJSUzI1NiJ9.payload.sig");
        masked.ShouldContain("Bearer ");
    }

    [Fact]
    public void MaskSecrets_ConnectionStringPassword_Masked()
    {
        var masked = LogSafe.MaskSecrets("Host=db;Username=app;Password=Hunter2!;Database=agentos");
        masked.ShouldNotContain("Hunter2!");
        masked.ShouldContain("Password=***");
        masked.ShouldContain("Host=db");
    }

    [Theory]
    [InlineData("ghu_1234567890abcdef", "567890abcdef")]
    [InlineData("ghr_1234567890abcdef", "567890abcdef")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "dozjgNryP4J3jVmNHl0w5N")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("ado pat abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrst end", "efghijklmnopqrstuvwxyz234567")]
    [InlineData("password: hunter2hunter2", "hunter2hunter2")]
    [InlineData("TOKEN: abcd1234efgh5678", "abcd1234efgh5678")]
    [InlineData("{\"client_secret\": \"zyxw9876vuts\"}", "zyxw9876vuts")]
    [InlineData("api-key: 'q1w2e3r4t5'", "q1w2e3r4t5")]
    public void MaskSecrets_MoreCredentialShapes_Masked(string text, string secret)
    {
        var masked = LogSafe.MaskSecrets(text);
        masked.ShouldNotContain(secret);
        masked.ShouldContain("***");
    }

    [Fact]
    public void MaskSecrets_AzureDevOpsPat84_Masked()
    {
        var pat = new string('A', 40) + new string('7', 36) + "AZDO" + "x1Y2";
        LogSafe.MaskSecrets($"pat {pat} end").ShouldNotContain(pat[8..]);
    }

    [Theory]
    [InlineData("used tokens: 5000 in 3 turns")]                 // "tokens" is not "token"
    [InlineData("Basic authentication is disabled")]            // the word, not the header
    [InlineData("commit 3f2a9c4e1b7d8a6f5e4c3b2a1908f7e6d5c4b3a2")]   // a git SHA is not a token
    public void MaskSecrets_LookAlikes_Unchanged(string text) => LogSafe.MaskSecrets(text).ShouldBe(text);

    [Fact]
    public void MaskSecrets_PlainText_Unchanged() =>
        LogSafe.MaskSecrets("pipeline run 42 finished in 3.2s").ShouldBe("pipeline run 42 finished in 3.2s");

    [Fact]
    public void Scrub_NewlinesAndSecrets_BothNeutralised()
    {
        var scrubbed = LogSafe.Scrub("line1\r\ninjected ghp_abcdef123456789");
        scrubbed.ShouldNotContain("\n");
        scrubbed.ShouldNotContain("ghp_abcdef123456789");
    }

    [Fact]
    public void Scrub_Null_ReturnsPlaceholder() => LogSafe.Scrub(null).ShouldBe("(null)");
}
