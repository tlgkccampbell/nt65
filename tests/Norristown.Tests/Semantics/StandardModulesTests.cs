using Norristown.Project;
using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks the modules that come with nt65. The tests check the bytes each charmap gives, and
/// check that the modules join only a program that could name them, that they write nothing, and
/// that their root is reserved.
/// </summary>
public sealed class StandardModulesTests
{
    /// <summary>
    /// Each charmap gives its machine's bytes, at the edges of each of its ranges and for each
    /// character it names on its own.
    /// </summary>
    [Theory]
    [InlineData("cbm::petscii", "AZ@ 09?[£]↑←", "$41, $5a, $40, $20, $30, $39, $3f, $5b, $5c, $5d, $5e, $5f")]
    [InlineData("cbm::petscii_lower", "azAZ@", "$41, $5a, $c1, $da, $40")]
    [InlineData("cbm::screen", "@AZ[£]↑← ?", "$00, $01, $1a, $1b, $1c, $1d, $1e, $1f, $20, $3f")]
    [InlineData("cbm::screen_lower", "@azAZ", "$00, $01, $1a, $41, $5a")]
    [InlineData("atari::atascii", "♥♣ _♦az♠|", "$00, $10, $20, $5f, $60, $61, $7a, $7b, $7c")]
    [InlineData("atari::screen", " _♥♣♦az♠|", "$00, $3f, $40, $50, $60, $61, $7a, $7b, $7c")]
    [InlineData("apple2::normal", " @_", "$a0, $c0, $df")]
    [InlineData("apple2::normal_lower", "a~", "$e1, $fe")]
    [InlineData("apple2::inverse", "@_ ?", "$00, $1f, $20, $3f")]
    [InlineData("apple2::flash", "@_ ?", "$40, $5f, $60, $7f")]
    public void EachCharmapGivesItsMachinesBytes(string charmap, string text, string bytes)
    {
        var main = Analysis.Outputs(
            ("main.nt65", $".module main\n.segment RODATA\n.data text: .byte nt65::{charmap}(\"{text}\")\n"))["main.s"];
        Assert.Contains($".byte {bytes}", main);
    }

    /// <summary>
    /// A character a machine's set lacks is an error, rather than whatever shares its code: the
    /// uppercase set has no lower case, and neither has an Apple II before the IIe.
    /// </summary>
    [Theory]
    [InlineData("cbm::petscii", "a")]
    [InlineData("cbm::screen", "a")]
    [InlineData("apple2::normal", "a")]
    [InlineData("atari::atascii", "~")]
    public void ACharacterTheSetLacksIsAnError(string charmap, string text)
    {
        var problems = Analysis.Program(
            ("main.nt65", $".module main\n.segment RODATA\n.data text: .byte nt65::{charmap}(\"{text}\")\n")).Problems();
        Assert.Contains(problems, problem => problem.Contains($"has no entry for `{text}`", StringComparison.Ordinal));
    }

    /// <summary>
    /// The modules join a program whose text mentions <c>nt65</c>, report nothing of their own,
    /// and write no output. A program that never mentions them is analyzed without them.
    /// </summary>
    [Fact]
    public void TheModulesJoinOnlyAProgramThatCouldNameThem()
    {
        var without = Analysis.Program(("main.nt65", ".module main\n"));
        Assert.DoesNotContain(without.Program.Files, file => StandardModules.IsStandard(file.Tree.Path));

        var files = ("main.nt65", ".module main\n.use nt65::cbm::screen\n.segment RODATA\n.data text: .byte screen(\"HI\")\n");
        var with = Analysis.Program(files);
        Assert.Equal(3, with.Program.Files.Count(file => StandardModules.IsStandard(file.Tree.Path)));
        Assert.Empty(with.Problems());
        Assert.Equal(["main.s", "main.s.lines"], Analysis.Outputs(files).Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An edit that first mentions the modules brings them in, which is a change to the files of
    /// the program, and an edit that keeps mentioning them keeps them.
    /// </summary>
    [Fact]
    public void AnEditThatFirstNamesThemAnalyzesTheWholeProgram()
    {
        var project = ProjectSettings.None;
        var plain = SyntaxTree.Parse("main.nt65", ".module main\nA_ = 1\n");
        var first = Compiler.Analyze([plain], project, Nothing);

        var naming = SyntaxTree.Parse("main.nt65", ".module main\n.use nt65::cbm::screen\nA_ = 1\n");
        var second = Compiler.Analyze([naming], project, Nothing, first, TestContext.Current.CancellationToken);
        Assert.Equal(WholeProgramReason.FilesAddedOrRemoved, second.WholeProgram);
        Assert.Contains(second.Program.Files, file => StandardModules.IsStandard(file.Tree.Path));

        var still = SyntaxTree.Parse("main.nt65", ".module main\n.use nt65::cbm::screen\nA_ = 2\n");
        var third = Compiler.Analyze([still], project, Nothing, second, TestContext.Current.CancellationToken);
        Assert.Contains(third.Program.Files, file => StandardModules.IsStandard(file.Tree.Path));
        Assert.DoesNotContain(third.Diagnostics, d => d.Severity == Severity.Error);
    }

    /// <summary>
    /// No module of a program's own may be under <c>nt65</c>, whether or not the modules there are
    /// in use.
    /// </summary>
    [Theory]
    [InlineData("nt65")]
    [InlineData("nt65::mine")]
    [InlineData("nt65::cbm")]
    public void TheRootIsReserved(string name)
    {
        var problems = Analysis.Program(("mine.nt65", $".module {name}\n")).Problems();
        Assert.Contains(problems, problem => problem.Contains("reserved for the modules that come with nt65", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("already declared", StringComparison.Ordinal));
    }

    /// <summary>Returns no length for any path, because no test here reads a binary file.</summary>
    private static long? Nothing(string path) => null;
}
