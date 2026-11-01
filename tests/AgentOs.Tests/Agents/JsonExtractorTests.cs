// AgentOs.Tests/Agents/JsonExtractorTests.cs
// Phase 4 — JsonExtractor handles 3 forms: direct, fenced, prose-wrapped.

using AgentOs.Domain.Llm;
using AgentOs.Modules.Pipeline.Agents;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Agents;

public class JsonExtractorTests
{
    private sealed class Probe
    {
        public string? Name { get; set; }
        public int Value { get; set; }
    }

    [Fact]
    public void Deserialize_DirectJson_Works()
    {
        var raw = """{"name":"foo","value":42}""";
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("foo");
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Deserialize_MarkdownFence_StrippedAndParsed()
    {
        var raw = """
            Here is the JSON:
            ```json
            {"name":"bar","value":99}
            ```
            """;
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("bar");
        result.Value.ShouldBe(99);
    }

    [Fact]
    public void Deserialize_PlainFenceNoLang_StrippedAndParsed()
    {
        var raw = """
            ```
            {"name":"baz","value":7}
            ```
            """;
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("baz");
    }

    [Fact]
    public void Deserialize_ProseWrapped_ExtractsBraced()
    {
        var raw = "Sure! Here's the result: {\"name\":\"qux\",\"value\":1} — let me know if you need more.";
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("qux");
    }

    [Fact]
    public void Deserialize_CamelCaseInsensitive_Works()
    {
        var raw = """{"Name":"camel","Value":3}""";
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("camel");
    }

    [Fact]
    public void Deserialize_TrailingComma_AllowedByOptions()
    {
        var raw = """{"name":"trailing","value":5,}""";
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("trailing");
    }

    [Fact]
    public void Deserialize_LeadingWhitespaceAndComment_ParsedDirectly()
    {
        var raw = "\n  // the agent's note\n{\"name\":\"commented\",\"value\":7}";
        var result = JsonExtractor.Deserialize<Probe>(raw, "Test");
        result.Name.ShouldBe("commented");
    }

    [Fact]
    public void ExtractJson_ByteOrderMark_ReturnsTheObjectWithoutIt()
    {
        var raw = "\uFEFF{\"name\":\"bom\",\"value\":1}";
        JsonExtractor.ExtractJson(raw, "Test").ShouldBe("{\"name\":\"bom\",\"value\":1}");
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("  {\"a\":1}  ")]
    public void ExtractJson_DirectJsonValue_ReturnsRawText(string raw) =>
        JsonExtractor.ExtractJson(raw, "Test").ShouldBe(raw);

    [Fact]
    public void ExtractJson_FencedReply_ReturnsTheFencedObject()
    {
        var raw = "Here you go:\n```json\n{\"name\":\"fenced\"}\n```\nDone.";
        JsonExtractor.ExtractJson(raw, "Test").ShouldBe("{\"name\":\"fenced\"}");
    }

    [Fact]
    public void ExtractJson_FenceAroundProse_FallsBackToBraces()
    {
        var raw = "```\nnot json here\n```\nresult: {\"name\":\"braced\"}";
        JsonExtractor.ExtractJson(raw, "Test").ShouldBe("{\"name\":\"braced\"}");
    }

    [Fact]
    public void Deserialize_NotJson_ThrowsLlmException()
    {
        var raw = "I don't know how to do that.";
        var ex = Should.Throw<LlmException>(() => JsonExtractor.Deserialize<Probe>(raw, "Test"));
        ex.Provider.ShouldBe("Test");
        ex.Message.ShouldContain("Test:");
    }
}
