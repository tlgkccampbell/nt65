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
        Assert.Contains("KBD = $C000", text, StringComparison.Ordinal);
        Assert.Contains("KBDSTRB = $C010", text, StringComparison.Ordinal);
        Assert.Contains("; the strobe", text, StringComparison.Ordinal);
        Assert.Contains("CHARS = SCREEN + $400", text, StringComparison.Ordinal);
        Assert.Contains("asm/apple2.inc", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A line nt65 cannot read stays in the module as a comment and is counted. Such lines
    /// include a ca65 directive, a ca65 operator nt65 does not have, and an expression in which
    /// nt65 requires parentheses to make its order explicit.
    /// </summary>
    [Fact]
    public void WhatItCannotConvertIsLeftAsAComment()
    {
        var (text, refused) = ImportIncCommand.Convert(
            """
            .include "other.inc"
            FLAGS = 1 .SHL 3
            MIXED = 1 | 2 + 3
            .struct Point
                    x       .word
            .endstruct
            OK    = 7
            """.ReplaceLineEndings("\n") + "\n",
            "hw",
            "other.inc");

        Assert.Equal([1, 2, 3, 4, 5, 6], refused.Select(line => line.Line));
        Assert.Contains("`.include` is a ca65 directive", refused[0].Why, StringComparison.Ordinal);
        Assert.Contains("parentheses", refused[2].Why, StringComparison.Ordinal);
        Assert.Contains("; not converted: .include \"other.inc\"", text, StringComparison.Ordinal);
        Assert.Contains("; not converted: MIXED = 1 | 2 + 3", text, StringComparison.Ordinal);
        Assert.Contains(".export OK", text, StringComparison.Ordinal);
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
        Assert.Contains("KBD = $C000", output, StringComparison.Ordinal);
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
