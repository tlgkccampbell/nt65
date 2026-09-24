using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>Constant evaluation and the classification of a name as a constant or an address.</summary>
public sealed class ConstantTests
{
    [Theory]
    [InlineData("$1f", 31)]
    [InlineData("%1010", 10)]
    [InlineData("255", 255)]
    [InlineData("'A'", 65)]
    [InlineData("'\\n'", 10)]
    [InlineData("'\\x7f'", 127)]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("7 / 2", 3)]
    [InlineData("7 .mod 2", 1)]
    [InlineData("1 << (2 + 1)", 8)]
    [InlineData("-1 >> 1", -1)]
    [InlineData("~0", -1)]
    [InlineData("!0", 1)]
    [InlineData("<$1234", 0x34)]
    [InlineData(">$1234", 0x12)]
    [InlineData("^$123456", 0x12)]
    [InlineData("($0f & 3) == 3", 1)]
    [InlineData("1 < 2", 1)]
    [InlineData("(1 == 1) && (2 == 3)", 0)]
    [InlineData("(1 == 1) ^^ (2 == 3)", 1)]
    [InlineData("(0 != 0) || (1 != 0)", 1)]
    [InlineData(".lobyte($1234)", 0x34)]
    [InlineData(".hibyte($1234)", 0x12)]
    [InlineData(".bankbyte($123456)", 0x12)]
    [InlineData(".loword($12345678)", 0x5678)]
    [InlineData(".hiword($12345678)", 0x1234)]
    [InlineData(".min(3, 4)", 3)]
    [InlineData(".max(3, 4)", 4)]
    [InlineData(".strlen(\"hello\")", 5)]
    [InlineData(".strat(\"hello\", 1)", 'e')]
    public void ExpressionsEvaluate(string expression, long expected)
    {
        var model = Analysis.Model($".module main\nVALUE = {expression}\n");

        Assert.Empty(model.Problems());
        Assert.Equal(expected, model.Symbol("VALUE").Value.Number);
    }

    [Fact]
    public void AStringIsAValue()
    {
        var model = Analysis.Model(".module main\nGREETING = \"hi\\n\"\n");

        Assert.Equal("hi\n", model.Symbol("GREETING").Value.Text);
    }

    /// <summary>A constant may be declared before the names it uses.</summary>
    [Fact]
    public void ForwardReferencesResolve()
    {
        var model = Analysis.Model(".module main\nFIRST = SECOND + 1\nSECOND = THIRD * 2\nTHIRD = 3\n");

        Assert.Empty(model.Problems());
        Assert.Equal([7, 6, 3], model.Symbols.Select(symbol => symbol.Value.Number));
    }

    [Fact]
    public void ANameThatNeedsItselfIsReportedOnce()
    {
        var model = Analysis.Model(".module main\nSELF = SELF + 1\n");

        Assert.Equal(["2: `SELF` is defined in terms of itself"], model.Problems());
        Assert.False(model.Symbol("SELF").Value.IsKnown);
    }

    /// <summary>
    /// A cycle of names is one error, at the declaration that closes it, naming the others it runs
    /// through.
    /// </summary>
    [Fact]
    public void ACycleIsReportedOnceAndNamesTheRest()
    {
        var model = Analysis.Model(".module main\nONE = TWO\nTWO = THREE\nTHREE = ONE\n");

        var diagnostic = Assert.Single(model.Diagnostics);
        Assert.Equal("`ONE` is defined in terms of itself", diagnostic.Message);
        Assert.Equal(["through `TWO`", "through `THREE`"], diagnostic.Related.Select(r => r.Message));
        Assert.All(model.Symbols, symbol => Assert.False(symbol.Value.IsKnown));
    }

    [Fact]
    public void DivisionByZeroIsReported()
    {
        var model = Analysis.Model(".module main\nQUOTIENT = 1 / 0\nREMAINDER = 1 .mod 0\n");

        Assert.Equal(["2: division by zero", "3: division by zero"], model.Problems());
    }

    /// <summary>
    /// The host processor traps on one division, the most negative 64-bit number divided by −1.
    /// Its quotient is one more than the largest 64-bit number, which is an overflow like any
    /// other and has no value. Its remainder is zero, as a remainder by −1 always is. Neither
    /// crashes the assembler.
    /// </summary>
    [Fact]
    public void TheDivisionWithNoAnswerDoesNotEndTheProcess()
    {
        var model = Analysis.Model(
            ".module main\nQUOTIENT = (0 - $7fffffffffffffff - 1) / (0 - 1)\n"
            + "REMAINDER = (0 - $7fffffffffffffff - 1) .mod (0 - 1)\n");

        Assert.False(model.Symbol("QUOTIENT").Value.IsKnown);
        Assert.Equal(0, model.Symbol("REMAINDER").Value.AsNumber());
    }

    [Fact]
    public void ArithmeticOnAStringIsReported()
    {
        var model = Analysis.Model(".module main\nTEXT = \"hi\"\nJOIN = TEXT + 1\n");

        Assert.Equal(["3: `+` cannot be used on a string"], model.Problems());
    }

    /// <summary>
    /// A <c>NAME = expr</c> is a constant when the expression names no address, and an
    /// address alias when it does.
    /// </summary>
    [Fact]
    public void AnExpressionNamingAnAddressIsAnAlias()
    {
        var model = Analysis.Model("""
            .module main
            .segment ZEROPAGE
            .data ptr:    .byte[2]
            SCREEN  = $0400
            NEXT    = ptr + 1
            HERE    = *
            TWICE   = SCREEN * 2
            ALIAS   = NEXT
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(SymbolKind.Constant, model.Symbol("SCREEN").Kind);
        Assert.Equal(SymbolKind.Constant, model.Symbol("TWICE").Kind);
        Assert.Equal(SymbolKind.AddressAlias, model.Symbol("NEXT").Kind);
        Assert.Equal(SymbolKind.AddressAlias, model.Symbol("HERE").Kind);
        Assert.Equal(SymbolKind.AddressAlias, model.Symbol("ALIAS").Kind);
    }

    [Fact]
    public void ALabelHasNoValue()
    {
        var model = Analysis.Model(".module main\n.proc main {\nrts\n}\n");

        Assert.False(model.Symbol("main").Value.IsKnown);
        Assert.Equal(SymbolKind.Proc, model.Symbol("main").Kind);
    }

    /// <summary>An extern proc is a routine at a constant address, which nt65 knows.</summary>
    [Fact]
    public void AnExternProcKeepsItsAddress()
    {
        var model = Analysis.Model(".module main\n.proc CHROUT = $ffd2: a8, i8\n");

        Assert.Equal(SymbolKind.ExternProc, model.Symbol("CHROUT").Kind);
        Assert.Equal(0xffd2, model.Symbol("CHROUT").Value.Number);
        Assert.Equal(AddressSize.Absolute, model.Symbol("CHROUT").AddressSize);
    }

    /// <summary>
    /// An extern proc that names another routine must declare that routine's signature. The
    /// check compares what each signature declares, not the syntax it uses, so a
    /// matching <c>inline</c> item is accepted and a differing <c>keeps</c> is reported.
    /// </summary>
    [Theory]
    [InlineData("inline .strz", "inline .strz", false)]
    [InlineData("keeps x", "keeps x", false)]
    [InlineData("keeps x", "keeps a", true)]
    public void AnotherNameForARoutineDeclaresTheSameSignature(string routine, string alias, bool reported)
    {
        var problems = Analysis.Program(
            ("main.nt65", $".module main\n.export .proc print = $ffd2: {routine}\n.export .proc say = print: {alias}\n")).Problems();

        Assert.Equal(reported, problems.Any(problem => problem.Contains("must declare the same signature", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An import may give its value, which nt65 then uses everywhere, or its address size. An
    /// import that gives neither is absolute.
    /// </summary>
    [Fact]
    public void ImportsCarryTheirValueOrTheirSize()
    {
        var model = Analysis.Model(".module main\n.import VIC_BORDER = $d020\n.import scratch: zp\n.import table: far\n.import raw\n");

        Assert.Empty(model.Problems());
        Assert.Equal(0xd020, model.Symbol("VIC_BORDER").Value.Number);
        Assert.Equal(SymbolKind.ImportedConstant, model.Symbol("VIC_BORDER").Kind);
        Assert.Equal(AddressSize.ZeroPage, model.Symbol("scratch").AddressSize);
        Assert.Equal(AddressSize.Far, model.Symbol("table").AddressSize);
        Assert.Equal(AddressSize.Absolute, model.Symbol("raw").AddressSize);
    }

    /// <summary>A file may export only what it declares.</summary>
    [Fact]
    public void AnExportNamesADeclaration()
    {
        var model = Analysis.Model(".module main\n.export main, missing\n.proc main {\nrts\n}\n");

        Assert.Equal(["2: `missing` is not declared"], model.Problems());
        Assert.Equal(2, model.ReferencesTo(model.Symbol("main")).Count);
    }
}
