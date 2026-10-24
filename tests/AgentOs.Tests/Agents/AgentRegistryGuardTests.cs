// One agent registry — the guard. Four Board agents once called factory.Create("RemoteAgent") with Model:
// "claude-sonnet-4-6" while Settings → Agent profiles showed rows that did nothing. Every agent under
// Modules.Pipeline/Agents and /Decomposition must take its provider and model from its profile, so this test parses
// those sources with Roslyn (syntax only — robust to formatting, comments and named vs positional arguments) and fails
// on a string literal, an interpolated string or a same-file string constant passed as the provider to
// ILlmClientFactory.Create or as the Model of an LlmRequest.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Agents;

public sealed class AgentRegistryGuardTests
{
    private static readonly string[] AgentFolders =
    [
        Path.Join("src", "AgentOs.Modules.Pipeline", "Agents"),
        Path.Join("src", "AgentOs.Modules.Pipeline", "Decomposition"),
    ];

    [Fact]
    public void Agents_NeverHardCodeTheirProviderOrModel()
    {
        var files = AgentFolders.SelectMany(f => Directory.EnumerateFiles(Path.Join(RepoRoot(), f), "*.cs", SearchOption.AllDirectories)).ToList();
        files.Count.ShouldBeGreaterThan(5);   // the scan really looked at the agents

        var violations = files.SelectMany(f => Violations(File.ReadAllText(f)).Select(v => $"{Path.GetFileName(f)}: {v}")).ToList();

        violations.ShouldBeEmpty(
            "Resolve the provider and model from the agent's profile (BoardAgent.ProfileAsync / IAgentProfiles) instead:\n"
            + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("""class A { void M(ILlmClientFactory f) { f.Create("RemoteAgent"); } }""")]
    [InlineData("""class A { void M(ILlmClientFactory f) { var c = f.Create($"Remote{1}"); } }""")]
    [InlineData("""class A { const string P = "Claude"; void M(ILlmClientFactory f) { _ = f.Create(P); } }""")]
    [InlineData("""class A { void M() { var r = new LlmRequest("s", "u", "claude-sonnet-4-6"); } }""")]
    [InlineData("""class A { void M() { var r = new LlmRequest(SystemPrompt: "s", UserPrompt: "u", Model: "gpt-5"); } }""")]
    [InlineData("""class A { void M() { var r = new LlmRequest(UserPrompt: "u", Model: "gpt-5", SystemPrompt: "s"); } }""")]
    [InlineData("""class A { const string M2 = "x"; void M() { LlmRequest r = new("s", "u", M2); } }""")]
    public void Scanner_FlagsEveryHardCodedForm(string source) => Violations(source).ShouldNotBeEmpty();

    [Theory]
    [InlineData("""class A { void M(ILlmClientFactory f, AgentOptions p) { f.Create(p.Provider); } }""")]
    [InlineData("""class A { void M(AgentOptions p) { var r = new LlmRequest("s", "u", p.Model, p.Temperature, p.MaxTokens); } }""")]
    [InlineData("""class A { void M(AgentOptions p) { var r = new LlmRequest(SystemPrompt: "s", UserPrompt: "u", Model: p.Model); } }""")]
    public void Scanner_AcceptsProfileValues(string source) => Violations(source).ShouldBeEmpty();

    // Syntax-only scan of one file.
    internal static IEnumerable<string> Violations(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var constants = root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword))
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.ValueText))
            .Concat(root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
                .Where(l => l.IsConst).SelectMany(l => l.Declaration.Variables.Select(v => v.Identifier.ValueText)))
            .ToHashSet(StringComparer.Ordinal);

        bool HardCoded(ExpressionSyntax e) => e switch
        {
            LiteralExpressionSyntax lit => lit.IsKind(SyntaxKind.StringLiteralExpression),
            InterpolatedStringExpressionSyntax => true,
            IdentifierNameSyntax id => constants.Contains(id.Identifier.ValueText),
            ParenthesizedExpressionSyntax p => HardCoded(p.Expression),
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) => HardCoded(b.Left) && HardCoded(b.Right),
            _ => false,
        };

        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Create" }
                && call.ArgumentList.Arguments is [var provider, ..]
                && provider.NameColon is null or { Name.Identifier.ValueText: "providerName" }
                && HardCoded(provider.Expression))
            {
                yield return $"line {Line(call)}: ILlmClientFactory.Create({provider.Expression})";
            }
        }

        foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var isRequest = creation switch
            {
                ObjectCreationExpressionSyntax o => o.Type.ToString() is "LlmRequest" or "AgentOs.Domain.Llm.LlmRequest",
                ImplicitObjectCreationExpressionSyntax i => i.Parent?.Parent?.Parent is VariableDeclarationSyntax { Type: var t }
                    && t.ToString() is "LlmRequest" or "AgentOs.Domain.Llm.LlmRequest",
                _ => false,
            };
            if (!isRequest || creation.ArgumentList is not { } args) { continue; }
            var model = args.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "Model")
                ?? (args.Arguments.Count > 2 && args.Arguments.Take(3).All(a => a.NameColon is null) ? args.Arguments[2] : null);
            if (model is not null && HardCoded(model.Expression))
            {
                yield return $"line {Line(creation)}: LlmRequest Model = {model.Expression}";
            }
        }
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }
}
