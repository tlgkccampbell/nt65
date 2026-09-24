using System.Text.RegularExpressions;
using Norristown.Processor;

namespace Norristown.Tests.Layout;

/// <summary>
/// Checks the words ca65 reads as an instruction against the instruction tables in the pinned
/// ca65 source. Alias spellings were missed while nt65's list described the processor
/// rather than what the assembler accepts as input, so this test reads the assembler's own
/// tables.
/// <para>
/// It runs no assembler, but it needs the cc65 source checkout that
/// <c>scripts/build-cc65.ps1</c> makes, which is why it is an oracle test: the gate builds
/// cc65 before it runs the oracle tests.
/// </para>
/// </summary>
[Trait("Category", "Oracle")]
public sealed partial class Ca65InstructionTests
{
    /// <summary>
    /// For every CPU, the words the emitter writes with a module prefix when they are used as
    /// names are exactly ca65's table for the <c>.setcpu</c> nt65 writes. If a newer ca65 gains
    /// an instruction, this fails when the pinned version is moved, which is when there is
    /// something to decide.
    /// </summary>
    [Fact]
    public void TheWordsNt65PrefixesAreCa65sOwnTable()
    {
        var tables = Tables();
        var problems = new List<string>();
        foreach (var cpu in CpuNames.All)
        {
            var setcpu = CpuNames.FormatForCa65(cpu);
            Assert.True(tables.TryGetValue("InsTab" + setcpu, out var theirs),
                $"ca65's instr.c has no table InsTab{setcpu}");
            var ours = Ca65Instructions.Of(cpu);
            problems.AddRange(theirs!.Except(ours, StringComparer.Ordinal)
                .Select(name => $"`.setcpu \"{setcpu}\"`: ca65 has `{name}` and nt65 does not"));
            problems.AddRange(ours.Except(theirs!, StringComparer.Ordinal)
                .Select(name => $"`.setcpu \"{setcpu}\"`: nt65 has `{name}` and ca65 does not"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Matches a row of one of ca65's instruction tables, which begins with the mnemonic in
    /// quotes.
    /// </summary>
    [GeneratedRegex(@"^\s*\{\s*""(?<name>[A-Za-z0-9]+)""")]
    private static partial Regex Row();

    /// <summary>
    /// Matches the line where one of the tables begins. The table's name follows the closing
    /// brace of its struct type.
    /// </summary>
    [GeneratedRegex(@"^\}\s*(?<table>InsTab\w+)\s*=\s*\{")]
    private static partial Regex Table();

    /// <summary>
    /// Returns each table of ca65's <c>instr.c</c>, keyed by its name, holding its mnemonics in
    /// lower case.
    /// </summary>
    private static Dictionary<string, List<string>> Tables()
    {
        var path = Repo.Path(".cache", "cc65-src", "src", "ca65", "instr.c");
        Assert.True(File.Exists(path), $"the pinned cc65 source is not at {path}; run scripts/build-cc65.ps1");

        var tables = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in Repo.ReadText(path).ReplaceLineEndings("\n").Split('\n'))
        {
            if (Table().Match(line) is { Success: true } opened)
                tables[opened.Groups["table"].Value] = current = [];
            else if (line.StartsWith("};", StringComparison.Ordinal))
                current = null;
            else if (current is not null && Row().Match(line) is { Success: true } row)
                current.Add(row.Groups["name"].Value.ToLowerInvariant());
        }
        return tables;
    }
}
