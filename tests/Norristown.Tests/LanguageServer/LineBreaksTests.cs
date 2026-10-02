using Norristown.LanguageServer;
using Norristown.LanguageServer.Protocol;
using Norristown.Semantics;
using Norristown.Syntax;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the refactorings that break a line, over every construct that may be written across
/// lines. Each construct is laid out and joined back, the layout is one the formatter keeps and
/// the parser reads without an error, and a long line is suggested over the construct.
/// </summary>
public sealed class LineBreaksTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    private const string Lay = "Lay out the expression across lines";

    private const string Join = "Join the expression onto one line";

    private const string AsBlock = "Write the items as a block";

    private const string OnOneLine = "Write the items on one line";

    private const string Prefix = ".module main\n"
        + ".macro fill(dest: operand, value, count: const = 1, body: block = {}) {\n    body\n}\n"
        + ".macro when(c, d, then: block, else: block = {}) {\n    then\n    else\n}\n"
        + ".func f(value, factor = 1) = value * factor\n";

    /// <summary>
    /// Gets each construct as it is written on one line and as the refactoring lays it out, with
    /// the text the caret is put at and the text the long-line suggestion starts at.
    /// </summary>
    public static TheoryData<string, string, string, string, string> Constructs { get; } = new()
    {
        {
            ".const X = .select(1, 2, 3) + 1\n",
            ".const X = .select(\n    1,\n    2,\n    3) + 1\n",
            "2,", ".select", Lay
        },
        {
            ".const Y = 2 .in [10, 20, 30]\n",
            ".const Y = 2 .in [\n    10,\n    20,\n    30]\n",
            "20", "2 .in", Lay
        },
        {
            ".const Z = f(1000, factor = 2)\n",
            ".const Z = f(\n    1000,\n    factor = 2)\n",
            "factor = 2", "f(1000", Lay
        },
        {
            ".segment CODE\n.proc main {\n    lda #f(1000, factor = 2)\n    rts\n}\n",
            ".segment CODE\n.proc main {\n    lda #f(\n        1000,\n        factor = 2)\n    rts\n}\n",
            "factor = 2", "f(1000", Lay
        },
        {
            ".segment CODE\n.proc main {\n    fill!({buf,x}, 1, count = 2)\n    rts\n}\n",
            ".segment CODE\n.proc main {\n    fill!(\n        {buf,x},\n        1,\n        count = 2)\n    rts\n}\n",
            "count", "({buf", Lay
        },
        {
            ".segment CODE\n.proc main {\n    when!(eq, {buf}) {\n        nop\n    } else {\n        inx\n    }\n    rts\n}\n",
            ".segment CODE\n.proc main {\n    when!(\n        eq,\n        {buf}) {\n        nop\n    } else {\n        inx\n    }\n    rts\n}\n",
            "eq,", "(eq", Lay
        },
        {
            ".macro put(address: operand(abs, zp), value: const(0..255) = 0, times: one(once, twice)) {\n    nop\n}\n",
            ".macro put(\n    address: operand(abs, zp),\n    value: const(0..255) = 0,\n    times: one(once, twice)) {\n    nop\n}\n",
            "value:", "(address", Lay
        },
        {
            ".func g(value, factor = 2, bias = 0) = value * factor + bias\n",
            ".func g(\n    value,\n    factor = 2,\n    bias = 0) = value * factor + bias\n",
            "factor =", "(value", Lay
        },
        {
            ".export first, second, third\n",
            ".export {\n    first\n    second\n    third\n}\n",
            "second", ".export", AsBlock
        },
        {
            ".import vsync: proc(), frame: zp, scratch: zp\n",
            ".import {\n    vsync: proc()\n    frame: zp\n    scratch: zp\n}\n",
            "frame", ".import", AsBlock
        },
        {
            ".export .import frame: zp, scratch: zp\n",
            ".export .import {\n    frame: zp\n    scratch: zp\n}\n",
            "frame", ".import", AsBlock
        },
        {
            ".use hw::{BORDER, set_border as border}\n",
            ".use hw::{\n    BORDER\n    set_border as border\n}\n",
            "BORDER", ".use", AsBlock
        },
        {
            ".export .use hw::{BORDER, BACKGROUND}\n",
            ".export .use hw::{\n    BORDER\n    BACKGROUND\n}\n",
            "BORDER", ".use", AsBlock
        },
        {
            ".segment CODE\n.proc main {\n    jmp (buf)\n    .next @moving, @firing\n@moving:\n    rts\n@firing:\n    rts\n}\n",
            ".segment CODE\n.proc main {\n    jmp (buf)\n    .next {\n        @moving\n        @firing\n    }\n@moving:\n    rts\n@firing:\n    rts\n}\n",
            "@moving", ".next", AsBlock
        },
    };

    /// <summary>
    /// A construct written on one line is laid out from the caret, the layout parses without an
    /// error and is one the formatter leaves as it is, and joining it gives back what was written.
    /// A long line is suggested over the construct, starting where the refactoring is offered.
    /// </summary>
    [Theory]
    [MemberData(nameof(Constructs))]
    public void EachConstructIsLaidOutAndJoinedBack(string written, string laid, string at, string hintAt, string title)
    {
        var source = Prefix + written;
        var join = title == Lay ? Join : OnOneLine;

        var result = Editing.Apply(source, Single(source, at, title).Edit!.Changes[Uri]);

        Assert.Equal(Prefix + laid, result);
        Assert.DoesNotContain(SyntaxTree.Parse("main.nt65", result).Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.Equal(result, Formatter.Format(SyntaxTree.Parse("main.nt65", result)));
        Assert.DoesNotContain(Actions(result, At(result, at)), action => action.Title == title);
        Assert.Equal(source, Editing.Apply(result, Single(result, at, join).Edit!.Changes[Uri]));
        Assert.DoesNotContain(Actions(source, At(source, at)), action => action.Title == join);

        var start = source.IndexOf(at, Prefix.Length, StringComparison.Ordinal);
        var line = source[..start].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', start) + 1;
        var hint = Assert.Single(
            Lsp.ToDiagnostics([], SyntaxTree.Parse("main.nt65", source), Configuration.Everything, 20),
            hint => hint.Range.Start.Line == line);
        Assert.Equal(source.IndexOf(hintAt, lineStart, StringComparison.Ordinal) - lineStart, hint.Range.Start.Character);
    }

    /// <summary>
    /// Whatever an argument starts with, the layout reads back as the one statement it was and
    /// joins back to what was written. A line that starts with an instruction's name would start
    /// a statement of its own, so an argument that starts with one follows the argument before it.
    /// </summary>
    [Theory]
    [InlineData("    when!(@moving, .select(1, 2))\n", "    when!(\n        @moving,\n        .select(1, 2))\n")]
    [InlineData("    when!(<buf, -1)\n", "    when!(\n        <buf,\n        -1)\n")]
    [InlineData("    when!(*, \"text\")\n", "    when!(\n        *,\n        \"text\")\n")]
    [InlineData("    when!('c', {(buf),y})\n", "    when!(\n        'c',\n        {(buf),y})\n")]
    [InlineData("    when!(::f(1), x)\n", "    when!(\n        ::f(1),\n        x)\n")]
    [InlineData("    when!(eq, d = inc)\n", "    when!(\n        eq,\n        d = inc)\n")]
    [InlineData("    when!(eq, inc)\n", "    when!(\n        eq, inc)\n")]
    [InlineData("    lda #f(inc, 3)\n", "    lda #f(inc,\n        3)\n")]
    public void EveryArgumentReadsBack(string call, string laid)
    {
        const string Before = Prefix + ".segment CODE\n.proc main {\n";
        const string After = "@moving:\n    rts\n}\n";
        var source = Before + call + After;

        var result = Editing.Apply(source, Single(source, "(", Lay).Edit!.Changes[Uri]);

        Assert.Equal(Before + laid + After, result);
        Assert.DoesNotContain(SyntaxTree.Parse("main.nt65", result).Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        Assert.Equal(result, Formatter.Format(SyntaxTree.Parse("main.nt65", result)));
        Assert.Equal(source, Editing.Apply(result, Single(result, "(", Join).Edit!.Changes[Uri]));
    }

    /// <summary>
    /// Arguments that each start with an instruction's name stay on one line, so there is nothing
    /// to lay out and no long line is suggested over them.
    /// </summary>
    [Fact]
    public void ArgumentsThatAllStartWithAnInstructionStayOnTheirLine()
    {
        const string Source = Prefix + ".segment CODE\n.proc main {\n    when!(inc, dec)\n    rts\n}\n";

        Assert.DoesNotContain(Actions(Source, At(Source, "(")), action => action.Title == Lay);
        Assert.DoesNotContain(Lsp.ToDiagnostics([], SyntaxTree.Parse("main.nt65", Source), Configuration.Everything, 10),
            hint => hint.Range.Start.Line == Source.Count(c => c == '\n') - 3);
    }

    /// <summary>
    /// The items of a list directive are written as a block only where there are two or more of
    /// them to write, and a <c>.use</c> of everything or a <c>.next ?</c> has none.
    /// </summary>
    [Theory]
    [InlineData(".export first\n", "first")]
    [InlineData(".use hw::*\n", "hw")]
    [InlineData(".use hw::BORDER\n", "BORDER")]
    [InlineData(".segment CODE\n.proc main {\n    jmp (buf)\n    .next ?\n}\n", ".next")]
    public void NothingIsWrittenAsABlockWhereThereIsOneItemOrNone(string written, string at)
    {
        var source = ".module main\n" + written;

        Assert.DoesNotContain(Actions(source, At(source, at)), action => action.Title is AsBlock or OnOneLine);
    }

    /// <summary>
    /// A list directive is suggested only where its items run past the line length, not where a
    /// comment after them makes the line long.
    /// </summary>
    [Fact]
    public void AListIsSuggestedOnlyWhereItsItemsMakeTheLineLong()
    {
        var tree = SyntaxTree.Parse("main.nt65", ".module main\n.export first, second                ; a comment that runs on\n");

        Assert.DoesNotContain(Lsp.ToDiagnostics([], tree, Configuration.Everything, 30), hint => hint.Code == "long-line");
    }

    /// <summary>
    /// A comment after the directive's line moves to the line that opens the block, and back
    /// again. A block whose other lines hold a comment is not written on one line, from any of
    /// its lines.
    /// </summary>
    [Fact]
    public void AnItemBlockKeepsTheLinesCommentAndNoOtherIsLost()
    {
        const string Written = ".module main\n.export first, second  ; why\n";
        const string Laid = ".module main\n.export {  ; why\n    first\n    second\n}\n";

        Assert.Equal(Laid, Editing.Apply(Written, Single(Written, "first", AsBlock).Edit!.Changes[Uri]));
        Assert.Equal(Laid, Formatter.Format(SyntaxTree.Parse("main.nt65", Laid)));
        Assert.Equal(Written, Editing.Apply(Laid, Single(Laid, "}", OnOneLine).Edit!.Changes[Uri]));

        foreach (var commented in new[]
        {
            ".module main\n.export {\n    first  ; the first\n    second\n}\n",
            ".module main\n.export {\n    ; the first\n    first, second\n}\n",
            ".module main\n.export {\n    first, second\n}  ; done\n",
        })
        {
            foreach (var at in new[] { ".export", "first", "}" })
                Assert.DoesNotContain(Actions(commented, At(commented, at)), action => action.Title == OnOneLine);
        }
    }

    /// <summary>
    /// Returns the one action titled <paramref name="title"/> that is offered at the caret where
    /// <paramref name="at"/> first appears in <paramref name="text"/>.
    /// </summary>
    private static CodeAction Single(string text, string at, string title) =>
        Assert.Single(Actions(text, At(text, at)), action => action.Title == title);

    /// <summary>Returns an empty range where <paramref name="at"/> first appears in the text after its prefix.</summary>
    private static Range At(string text, string at)
    {
        var start = text.IndexOf(at, text.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix.Length : 0, StringComparison.Ordinal);
        var line = text[..start].Count(c => c == '\n');
        var caret = new Position(line, start - (text[..start].LastIndexOf('\n') + 1));
        return new Range(caret, caret);
    }

    /// <summary>Returns the refactorings offered over <paramref name="range"/> of the text.</summary>
    private static IReadOnlyList<CodeAction> Actions(string text, Range range)
    {
        var workspace = new Workspace();
        var document = workspace.Open(new TextDocumentItem(Uri, "nt65", 1, text));
        var analysis = workspace.AnalysisForAsync(document.Tree.Path, TestTimeout.Token()).GetAwaiter().GetResult();
        return CodeActions.In(analysis, analysis.ModelFor(document.Tree.Path)!, range, ["refactor"]);
    }
}
