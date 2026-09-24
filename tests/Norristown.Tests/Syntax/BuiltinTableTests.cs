using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Tests the table of built-in functions, which the parser, the evaluator and completion all
/// read.
/// </summary>
public sealed class BuiltinTableTests
{
    /// <summary>Every built-in kind has exactly one row, and the rows are in the order of the kinds.</summary>
    [Fact]
    public void EveryKindHasOneRowInOrder() =>
        Assert.Equal(
            Enum.GetValues<BuiltinKind>().Where(kind => kind != BuiltinKind.None),
            SyntaxFacts.Builtins.Select(builtin => builtin.Kind));

    /// <summary>Each name reads back as its kind in any letter case, and other names are no built-in.</summary>
    [Fact]
    public void NamesReadBackAsTheirKinds()
    {
        Assert.All(SyntaxFacts.Builtins, builtin =>
        {
            Assert.Equal(builtin, SyntaxFacts.Builtin(builtin.Kind));
            Assert.Equal(builtin.Kind, SyntaxFacts.BuiltinKindOf(builtin.Name));
            Assert.Equal(builtin.Kind, SyntaxFacts.BuiltinKindOf(builtin.Name.ToUpperInvariant()));
        });
        Assert.Equal(BuiltinKind.None, SyntaxFacts.BuiltinKindOf(".byte"));
        Assert.Equal(BuiltinKind.None, SyntaxFacts.BuiltinKindOf("sizeof"));
        Assert.False(SyntaxFacts.IsBuiltinFunction(".lobytes"));
    }

    /// <summary>
    /// The table holds the built-ins the language defines, those a build's condition may call
    /// among them, and those only a macro body may call.
    /// </summary>
    [Fact]
    public void TheTableHoldsTheLanguagesBuiltins()
    {
        Assert.Equal(
            [
                ".lobyte", ".hibyte", ".bankbyte", ".loword", ".hiword", ".sizeof", ".countof", ".endof", ".spanof",
                ".loadof", ".runof", ".strlen", ".strat", ".strsub", ".strcat", ".min", ".max", ".addrsize", ".target",
                ".defined", ".has", ".select", ".sqrt", ".muldiv", ".sin", ".cos", ".mincycles", ".maxcycles",
                ".mode", ".byteof", ".exprof", ".empty",
            ],
            SyntaxFacts.Builtins.Select(builtin => builtin.Name));
        Assert.Equal(
            [
                ".lobyte", ".hibyte", ".bankbyte", ".loword", ".hiword", ".strlen", ".strat", ".strsub", ".strcat",
                ".min", ".max", ".sqrt", ".muldiv", ".sin", ".cos",
            ],
            SyntaxFacts.Builtins.Where(builtin => builtin.Arithmetic).Select(builtin => builtin.Name));
        Assert.Equal(
            [".mode", ".byteof", ".exprof", ".empty"],
            SyntaxFacts.Builtins.Where(builtin => builtin.MacroOnly).Select(builtin => builtin.Name));
    }
}
