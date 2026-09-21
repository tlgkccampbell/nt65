namespace Norristown.Tests.Oracle;

/// <summary>
/// §13's promise that the output does not depend on ca65's command line, run rather than
/// asserted: every corpus program is assembled under a set of option sets that would change
/// what a hand-written file means, and the linked image has to come out byte for byte the
/// same as the baseline's, with ca65 saying nothing at <c>-W2</c>.
/// <para>
/// The options go only to what nt65 wrote. A hand-written module in the corpus is somebody
/// else's file and nt65 promises nothing about it, so it is assembled as its own build script
/// assembles it. The sets run beside each other, and the whole thing is gate work: it spawns
/// an assembler per file per set.
/// </para>
/// </summary>
[Trait("Category", "Oracle")]
public sealed class HostileOptionTests
{
    /// <summary>
    /// The option sets. Each is a ca65 command line that changes what a source file means —
    /// a target that translates characters, a memory model that changes default address
    /// sizes, smart mode, automatic imports, a define of a name a program's own configuration
    /// uses, and every emulation feature at once — plus <c>-W2</c> throughout.
    /// </summary>
    private static readonly (string Name, string[] Options, bool MaySayTheSegmentsDisagree)[] Sets =
    [
        ("plain", [], false),
        ("-t c64", ["-t", "c64"], false),
        ("-t none", ["-t", "none"], false),
        ("-mm near", ["-mm", "near"], true),
        ("-mm far", ["-mm", "far"], true),
        ("-U", ["-U"], false),
        ("--smart", ["--smart"], false),
        ("-D DEBUG=1", ["-D", "DEBUG=1"], false),
        ("--feature", [.. Features.SelectMany(name => new[] { "--feature", name })], false),
    ];

    /// <summary>
    /// Every emulation feature ca65 has that changes what a line means. The output's header
    /// switches each of them off, so switching them all on from the command line must change
    /// nothing at all.
    /// </summary>
    private static string[] Features =>
    [
        "at_in_identifiers", "bracket_as_indirect", "c_comments", "dollar_in_identifiers",
        "dollar_is_pc", "force_range", "labels_without_colons", "leading_dot_in_identifiers",
        "line_continuations", "long_jsr_jmp_rts", "loose_char_term", "loose_string_term",
        "missing_char_term", "org_per_seg", "pc_assignment", "string_escapes",
        "ubiquitous_idents", "underline_in_numbers",
    ];

    [Fact]
    public void EveryProgramLinksToTheSameBytesUnderEveryOptionSet()
    {
        var programs = CorpusProgram.All();
        if (Repo.Selection is null)
            Assert.NotEmpty(programs);

        // The baseline is the same link the corpus test makes, with nothing added but the
        // warning level every set carries.
        var baselines = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var program in programs)
        {
            var result = Linked(program, Sets[0].Options);
            Assert.True(result.Succeeded, $"[{program.Name}] baseline:\n{result.Messages}");
            Assert.NotEmpty(result.Binary);
            baselines[program.Name] = result.Binary;
        }

        var failures = Repo.CollectFailures(Sets[1..], set => Check(set, programs, baselines));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static IEnumerable<string> Check(
        (string Name, string[] Options, bool MaySayTheSegmentsDisagree) set, IReadOnlyList<CorpusProgram> programs,
        IReadOnlyDictionary<string, byte[]> baselines)
    {
        foreach (var program in programs)
        {
            var result = Linked(program, set.Options);
            if (result.Succeeded)
            {
                if (!result.Binary.SequenceEqual(baselines[program.Name]))
                    yield return $"[{program.Name}] under `ca65 {set.Name}`: the linked image is not the baseline's";
                continue;
            }

            // Every segment carries its address size, so a memory model that disagrees with
            // the segment table is an error naming the segment rather than a quiet change of
            // addressing modes. That is the promise; being refused is keeping it.
            if (set.MaySayTheSegmentsDisagree
                && result.Messages.Contains("Segment attribute mismatch", StringComparison.Ordinal))
            {
                continue;
            }
            yield return $"[{program.Name}] under `ca65 {set.Name}`:\n{result.Messages}";
        }
    }

    private static LinkResult Linked(CorpusProgram program, string[] options)
    {
        var compilation = program.Compile();
        var generated = compilation.Ca65.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
        return Ca65Oracle.Pinned.Link(
            program.LinkerConfig,
            [.. program.HandWritten, .. compilation.Ca65.Select(o => (o.Path, o.Text))],
            program.Other,
            options: name => generated.Contains(name) ? ["-W2", .. options] : []);
    }
}
