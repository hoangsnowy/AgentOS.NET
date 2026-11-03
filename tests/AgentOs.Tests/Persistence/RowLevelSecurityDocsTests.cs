// ADR-0012 promises "RowLevelSecurity.Tables is the single list" — and then its own copy drifted (sessions.approvals was
// added in code, the ADR still said 19). The ADR's table list, and every table count the security docs quote, are read
// back here and compared with the code, so the next table added to RowLevelSecurity.Tables fails the build until the
// docs say so too.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AgentOs.SharedKernel.Persistence;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Persistence;

public sealed partial class RowLevelSecurityDocsTests
{
    private static string Code(string schema, string table) => $"{schema}.{table}";

    [Fact]
    public void Adr0012_TableList_IsExactlyRowLevelSecurityTables()
    {
        var adr = File.ReadAllText(Path.Join(RepoRoot(), "docs", "adr", "0012-postgres-row-level-security.md"));
        var section = TablesSection().Match(adr);
        section.Success.ShouldBeTrue("ADR-0012 must keep its '**Tables** — `RowLevelSecurity.Tables` is the single list (N):' section");

        int.Parse(section.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(RowLevelSecurity.Tables.Count, "ADR-0012's table count");
        ListedTables(section.Groups["body"].Value)
            .ShouldBe(RowLevelSecurity.Tables.Select(t => Code(t.Schema, t.Table)), ignoreOrder: true,
                customMessage: "ADR-0012's table list must match RowLevelSecurity.Tables");
    }

    public static TheoryData<string> SecurityDocs => ["data-flow.md", "hardening.md", "threat-model.md"];

    // "RLS on N tables", "the N tables in/of `RowLevelSecurity.Tables`", "(N tables, the exemption …)".
    [Theory]
    [MemberData(nameof(SecurityDocs))]
    public void SecurityDocs_QuoteTheCurrentTableCount(string doc)
    {
        var text = File.ReadAllText(Path.Join(RepoRoot(), "docs", "security", doc));

        var counts = TableCount().Matches(text).Select(m => int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();

        counts.ShouldNotBeEmpty($"{doc} no longer quotes the RLS table count — drop this case if that is on purpose");
        counts.ShouldAllBe(n => n == RowLevelSecurity.Tables.Count, $"{doc} quotes a stale RLS table count");
    }

    // Each bullet names tables as `schema.table` or, after the first one on its line, a bare `table` of that schema.
    private static List<string> ListedTables(string body)
    {
        var tables = new List<string>();
        foreach (var line in body.Split('\n').Where(l => l.TrimStart().StartsWith('-')))
        {
            string? schema = null;
            foreach (Match name in Quoted().Matches(line))
            {
                var value = name.Groups[1].Value;
                var dot = value.IndexOf('.', StringComparison.Ordinal);
                if (dot > 0)
                {
                    schema = value[..dot];
                    tables.Add(value);
                }
                else if (schema is not null)
                {
                    tables.Add(Code(schema, value));
                }
            }
        }
        return tables;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Join(dir.FullName, "AgentOs.slnx"))) { return dir.FullName; }
        }
        throw new DirectoryNotFoundException("AgentOs.slnx not found above the test output directory.");
    }

    [GeneratedRegex(@"\*\*Tables\*\* — `RowLevelSecurity\.Tables` is the single list \((?<count>\d+)\):\s*\n(?<body>(?:[ \t]+-[^\n]*\n)+)")]
    private static partial Regex TablesSection();

    [GeneratedRegex(@"`([a-z_]+(?:\.[a-z_]+)?)`")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?:RLS on (?<n>\d+) tables|the (?<n>\d+) tables (?:in|of) `RowLevelSecurity\.Tables`|\((?<n>\d+) tables, the exemption)")]
    private static partial Regex TableCount();
}
