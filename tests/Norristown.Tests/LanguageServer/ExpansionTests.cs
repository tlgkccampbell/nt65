using Norristown.LanguageServer;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// How a macro call's expansion is shown: the body with the arguments substituted, written as
/// nt65 rather than as the ca65 the emitter produces, one level of calls at a time. The tests
/// use the macros fixture, which holds one of every kind of macro the language has.
/// </summary>
public sealed class ExpansionTests
{
    /// <summary>The fixture that holds one of every kind of macro, read once for every test here.</summary>
    private static readonly Lazy<(ProgramAnalysis Analysis, Norristown.Semantics.SemanticModel Model)> Macros =
        new(() =>
        {
            var text = Repo.ReadText(Repo.Path("tests", "fixtures", "macros", "main.nt65"));
            var analysis = Analysis.Program(("main.nt65", text));
            return (analysis, analysis.File("main.nt65"));
        });

    /// <summary>An expansion reads as the programmer would have written it: an operand argument is substituted whole, index included.</summary>
    [Fact]
    public void AnOperandArgumentGoesInWholeAndCarriesItsIndex()
    {
        Assert.Equal(
            """
            lda #<SCREEN
            sta ptr
            lda #>SCREEN
            sta ptr+1
            """,
            Written("set16!(ptr, SCREEN)"));

        // `dest+1` given `{buf,x}` is `buf+1,x`: the offset goes on the address and the index
        // goes back after it, which is the line a person would have written.
        Assert.Equal(
            """
            lda #<$1234
            sta buf,x
            lda #>$1234
            sta buf+1,x
            """,
            Written("set16!({buf,x}, $1234)"));
    }

    /// <summary>A `.byteof` is written as the byte it selects: a byte of the value for an immediate, and of the address for a mode that has one.</summary>
    [Fact]
    public void ByteofIsWrittenAsTheByteItAsksFor()
    {
        Assert.Equal(
            """
            lda #<SCREEN
            sta ptr
            lda #>SCREEN
            sta ptr+1
            """,
            Written("mov16!(ptr, {#SCREEN})"));
        Assert.Equal(
            """
            lda other
            sta ptr
            lda other+1
            sta ptr+1
            """,
            Written("mov16!(ptr, other)"));
    }

    /// <summary>
    /// A `.each` over a `list` argument is written out one iteration at a time, because nt65 has
    /// no syntax for a call's arguments as a list; each `.if` inside an iteration is replaced by
    /// the branch it takes.
    /// </summary>
    [Fact]
    public void ARepetitionOverTheArgumentsIsWrittenOut()
    {
        Assert.Equal(
            """
            pha
            phx
            phy
            """,
            Written("push!(a, x, y)"));
    }

    /// <summary>A block argument is the caller's own code, spliced where the body names it.</summary>
    [Fact]
    public void ABlockArgumentIsSplicedWhereTheBodyNamesIt()
    {
        Assert.Equal(
            """
            ldx #8
            @loop:
            sta (ptr),y
            iny
            dex
            bne @loop
            """,
            Written("times_x!(8)"));
    }

    /// <summary>A macro call inside the body is left as a call, with a link that expands it one level further.</summary>
    [Fact]
    public void ACallInsideABodyIsLeftAsACall()
    {
        var expansion = At("if!(cs)");
        Assert.Equal(
            """
            branch_unless!(cs, @skip)
            lda #0
            jmp @done
            @skip:
            inx
            @done:
            """,
            string.Join("\n", expansion.Lines));
        var link = Assert.Single(expansion.Links);
        Assert.Equal(0, link.Line);
        Assert.Equal("branch_unless!(cs, @skip)", link.Text);

        // Following the link expands that one call in place and leaves the rest as it was.
        Assert.Equal(
            """
            bcc @skip
            lda #0
            jmp @done
            @skip:
            inx
            @done:
            """,
            Written("if!(cs)", link.Into));
    }

    /// <summary>A default fills in what a call leaves out, and a `const` argument goes in as written.</summary>
    [Fact]
    public void ADefaultFillsInWhatACallLeavesOut()
    {
        Assert.Equal(".byte C4, 8", Written("note!(C4, frames = 8)"));
        Assert.Equal(".byte E4, 1", Written("note!(E4)"));
    }

    /// <summary>The summary says what the call becomes: how many lines, bytes and cycles it expands to.</summary>
    [Fact]
    public void TheSummarySaysWhatTheCallBecomes()
    {
        Assert.Equal("expands to 4 lines · 8 bytes · 10 cycles", At("set16!(ptr, SCREEN)").Summary());

        // A macro that writes data has no cycles to name, and says nothing about them.
        Assert.Equal("expands to 1 line · 2 bytes", At("note!(E4)").Summary());
    }

    /// <summary>Every call in the fixture is written out, and what comes out re-parses as nt65.</summary>
    [Fact]
    public void EveryCallInTheFixtureIsWrittenOutAsNt65()
    {
        var (analysis, model) = Macros.Value;
        var calls = 0;
        foreach (var node in model.Tree.Root.DescendantNodes().OfType<Norristown.Syntax.MacroCallSyntax>())
        {
            // A call written inside a macro body is expanded as part of that body: on its own,
            // its arguments are parameter names rather than values, so there is nothing to write out.
            if (InAMacroBody(node))
                continue;
            calls++;

            // What is shown must be nt65 a person could have written, so it must parse on its
            // own, both expanded one level and expanded all the way down.
            foreach (var all in (bool[])[false, true])
            {
                var expansion = MacroExpansion.Of(analysis, model, node, null, all);
                Assert.NotNull(expansion);
                Assert.Null(expansion.Refusal);
                var written = string.Join("\n", expansion.Lines);
                var parsed = Norristown.Syntax.SyntaxTree.Parse(
                    "expansion.nt65", $".module m\n.segment CODE\n.proc p {{\n{written}\n}}\n");
                Assert.Empty(parsed.Diagnostics.Select(d => $"{d.Span.Line}: {d.Message}"));
            }
        }
        Assert.True(calls > 10, $"the fixture holds {calls} calls");
    }

    /// <summary>Whether a call is written inside a macro body, where its arguments are not known.</summary>
    private static bool InAMacroBody(Norristown.Syntax.SyntaxNode call)
    {
        for (var at = call.Parent; at is not null; at = at.Parent)
        {
            if (at is Norristown.Syntax.BlockSyntax { BlockKind: Norristown.Syntax.BlockKind.Macro })
                return true;
        }
        return false;
    }

    /// <summary>The expansion of the call written at <paramref name="find"/>, as one string.</summary>
    private static string Written(string find, IReadOnlyList<int>? into = null) =>
        string.Join("\n", At(find, into).Lines);

    /// <summary>The expansion of the call written at <paramref name="find"/>.</summary>
    private static MacroExpansion At(string find, IReadOnlyList<int>? into = null)
    {
        var (analysis, model) = Macros.Value;
        var at = model.Tree.Text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the fixture writes no {find}");
        var expansion = MacroExpansion.At(analysis, model, at, into);
        Assert.NotNull(expansion);
        return expansion;
    }
}
