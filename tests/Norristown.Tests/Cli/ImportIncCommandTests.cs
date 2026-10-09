using Norristown.Cli;
using Norristown.Syntax;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests <c>nt65 import-inc</c>, the one-time conversion of a ca65 include file of constants
/// into an nt65 module. A person reads the module it writes, so nothing is dropped silently. A
/// line it cannot convert is kept in the module as a comment and is counted on standard error.
/// </summary>
public sealed class ImportIncCommandTests
{
    [Fact]
    public void ConstantsBecomeAModuleThatExportsThem()
    {
        var (text, refused) = ImportIncCommand.Convert(
            """
            ; Hardware, as a project keeps it.
            KBD      = $C000
            KBDSTRB := $C010        ; the strobe
            WNDLFT   = $20

            SCREEN   = $0400
            CHARS    = SCREEN + $400
            MASK     = %1010_0000
            """.ReplaceLineEndings("\n") + "\n",
            "hw",
            "asm/apple2.inc");

        Assert.Empty(refused);
        Assert.Contains(".module hw", text, StringComparison.Ordinal);
        Assert.Contains(".export KBD, KBDSTRB, WNDLFT, SCREEN, CHARS, MASK", text, StringComparison.Ordinal);
        Assert.Contains("; Hardware, as a project keeps it.", text, StringComparison.Ordinal);
        Assert.Contains(".const KBD     = $C000", text, StringComparison.Ordinal);
        Assert.Contains(".const KBDSTRB = $C010", text, StringComparison.Ordinal);
        Assert.Contains("; the strobe", text, StringComparison.Ordinal);
        Assert.Contains(".const CHARS  = SCREEN + $400", text, StringComparison.Ordinal);
        Assert.Contains("asm/apple2.inc", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line nt65 cannot read stays in the module as a comment and is counted. Such lines
    /// include a ca65 directive and an expression nt65 cannot parse.
    /// </summary>
    [Fact]
    public void WhatItCannotConvertIsLeftAsAComment()
    {
        var (text, refused) = ImportIncCommand.Convert(
            """
            .include "other.inc"
            BROKEN = 1 +
            .struct Point
                    x       .word
            .endstruct
            OK    = 7
            """.ReplaceLineEndings("\n") + "\n",
            "hw",
            "other.inc");

        Assert.Equal([1, 2, 3, 4, 5], refused.Select(line => line.Line));
        Assert.Contains("`.include` is a ca65 directive", refused[0].Why, StringComparison.Ordinal);
        Assert.Contains("cannot parse", refused[1].Why, StringComparison.Ordinal);
        Assert.Contains("; not converted: .include \"other.inc\"", text, StringComparison.Ordinal);
        Assert.Contains("; not converted: BROKEN = 1 +", text, StringComparison.Ordinal);
        Assert.Contains(".export OK", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// An expression is read with ca65's precedence and written with nt65's operators and the
    /// parentheses that keep its meaning. ca65 binds <c>.SHL</c> as tightly as <c>*</c> and
    /// <c>|</c> as tightly as <c>+</c>, both left to right, so <c>1 | 2 + 3</c> is
    /// <c>(1 | 2) + 3</c>. Its <c>!</c> binds loosest of all, so <c>!OK + 1</c> is
    /// <c>!(OK + 1)</c>, which nt65 would otherwise read as <c>(!OK) + 1</c>.
    /// </summary>
    [Fact]
    public void ExpressionsKeepCa65sPrecedence()
    {
        var (text, refused) = ImportIncCommand.Convert(
            """
            OK    = 7
            FLAGS = 1 .SHL 3
            MIXED = 1 | 2 + 3
            FLIP  = !OK + 1
            """.ReplaceLineEndings("\n") + "\n",
            "hw",
            "other.inc");

        Assert.Empty(refused);
        Assert.Contains("= 1 << 3", text, StringComparison.Ordinal);
        Assert.Contains("= (1 | 2) + 3", text, StringComparison.Ordinal);
        Assert.Contains("= !(OK + 1)", text, StringComparison.Ordinal);
    }

    /// <summary>The file it writes is an nt65 module that parses and is in the standard layout.</summary>
    [Fact]
    public void WhatItWritesReadsBackAsNt65()
    {
        var (text, _) = ImportIncCommand.Convert("A = 1\nB = A + 1   ; two\n", "hw", "hw.inc");
        var tree = SyntaxTree.Parse(new SourceFile("hw.nt65", text));

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(text, Formatter.Format(tree));
    }

    [Fact]
    public void ItWritesToStandardOutput()
    {
        var (code, output, problems) = Run("import-inc", Repo.Path("tests/corpus/interop/asm/apple2.inc"));

        Assert.Equal(ExitCode.Success, code);
        Assert.Empty(problems);
        Assert.Contains(".module apple2", output, StringComparison.Ordinal);
        Assert.Matches(@"\.const KBD += \$C000", output);
    }

    [Fact]
    public void ItNeedsAFile()
    {
        var (code, _, problems) = Run("import-inc");

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains("import-inc needs the `.inc` file to convert", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileItCannotReadIsReported()
    {
        var (code, _, problems) = Run("import-inc", Repo.Path("tests/corpus/interop/asm/no-such-file.inc"));

        Assert.Equal(ExitCode.InputError, code);
        Assert.Contains("cannot read", problems, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpIsTheUsageText()
    {
        var (code, output, _) = Run("import-inc", "--help");

        Assert.Equal(ExitCode.Success, code);
        Assert.Contains("nt65 import-inc <file.inc>", output, StringComparison.Ordinal);
    }

    private static (ExitCode Code, string Output, string Problems) Run(params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(
            arguments, Directory.GetCurrentDirectory(), output, error,
            cancellation: TestTimeout.Token());
        return (code, output.ToString(), error.ToString());
    }
}
