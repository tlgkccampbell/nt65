using Norristown.LanguageServer;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What a macro call is shown as: the body with the arguments in place, written as nt65 rather
/// than as the ca65 the emitter writes, one level at a time. The fixture's macros are the
/// language's own set of them, so they are what this is asked about.
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

    /// <summary>An expansion is what the programmer would have written: the operand goes in whole.</summary>
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

    /// <summary>A `.byteof` is the byte it asks for: of the value for an immediate, of the address for a mode that has one.</summary>
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
    /// A `.each` over a `list` argument is written out as its turns, because nt65 has no way to
    /// write a call's arguments as a list; the `.if` inside each turn is the branch it takes.
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

    /// <summary>A call inside a body is left as a call, with a way to ask for it one level down.</summary>
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

        // Asked for, that one call is written out in place and the rest stands as it was.
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

    /// <summary>The summary is the answer: what it becomes, in words before numbers.</summary>
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
            // A call written inside a body is expanded by whatever expands that body: on its
            // own its arguments are names and not values, and there is nothing to write out.
            if (InAMacroBody(node))
                continue;
            calls++;

            // What is shown has to be nt65 a person could have written, so it parses on its
            // own — one level at a time, and written out all the way down.
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

    /// <summary>
    /// Inlining a call changes what a file says and not what it assembles to: every call the
    /// change is offered on, in every fixture that writes one, builds to the same bytes with the
    /// call written out as it did with the call.
    /// </summary>
    [Fact]
    public void InliningACallLeavesTheProgramTheSame()
    {
        var inlined = 0;
        foreach (var fixture in Fixtures.FixtureCase.All()
            .DistinctBy(fixture => fixture.Directory, StringComparer.Ordinal))
        {
            if (!fixture.Sources.Any(source => source.Text.Contains("!(", StringComparison.Ordinal)))
                continue;
            var before = Compiler.Compile(fixture.Sources, fixture.Project, fixture.BinaryLength);
            if (before.Outputs.Count == 0)
                continue;
            var analysis = Compiler.Analyze(
                [.. fixture.Sources.Select(Norristown.Syntax.SyntaxTree.Parse)], fixture.Project, fixture.BinaryLength);

            foreach (var source in fixture.Sources)
            {
                if (analysis.ModelFor(source.Path) is not { } model)
                    continue;
                foreach (var call in model.Tree.Root.DescendantNodes().OfType<Norristown.Syntax.MacroCallSyntax>())
                {
                    if (InAMacroBody(call)
                        || InlineMacro.In(analysis, model, call.Position).ToList() is not
                            [{ Refused: null, Edits: [var edit] }])
                    {
                        continue;
                    }
                    var written = source.Text[..edit.Span.Start] + edit.Text + source.Text[edit.Span.End..];
                    var after = Compiler.Compile(
                        [.. fixture.Sources.Select(file =>
                            file.Path == source.Path ? new SourceFile(file.Path, written) : file)],
                        fixture.Project, fixture.BinaryLength);
                    // The same bytes, in the same order: what the ca65 calls the labels of an
                    // expansion is the one thing that moves, because they are the caller's own
                    // once the call is gone, and the comment naming the call goes with it.
                    Assert.True(
                        Assembled(before).SequenceEqual(Assembled(after)),
                        $"[{fixture.Name}] {source.Path}:{model.Tree.GetLineIndex(call.Position) + 1}\n{edit.Text}\n"
                        + string.Join("\n", after.Diagnostics.Where(Wrong).Select(Fixtures.FixtureCase.Format)));

                    // Nothing new is wrong with it. A macro whose only call was written out is
                    // one nothing names any more, which is the warning it should be.
                    Assert.Equal(
                        before.Diagnostics.Where(Wrong).Select(Fixtures.FixtureCase.Format),
                        after.Diagnostics.Where(Wrong).Select(Fixtures.FixtureCase.Format));
                    inlined++;
                }
            }
        }
        Assert.True(inlined > 5, $"the fixtures offered the change on {inlined} calls");
    }

    /// <summary>Whether a diagnostic says the program is wrong, rather than only remarking on it.</summary>
    private static bool Wrong(Diagnostic diagnostic) => diagnostic.Id != "unused-symbol";

    /// <summary>
    /// What a compilation assembles to: each file, and the length of every line of it that
    /// generates bytes, in order. A comment and a label generate none, so this is the program
    /// and not the ca65 that spells it.
    /// </summary>
    private static IEnumerable<string> Assembled(Compilation compilation) =>
        compilation.Outputs
            .Where(output => output.Kind == OutputKind.Ca65)
            .Select(output => $"{output.Path}: {string.Join(",", output.LineBytes.Where(bytes => bytes != 0))}");

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
