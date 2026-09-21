using Norristown.LanguageServer;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The server's lists of which directives may begin a line where, held to the binder that
/// decides it. Nothing in the language ties the two together: a directive offered where the
/// binder refuses it is a completion that writes a broken line, and one the binder accepts and
/// the list leaves out is a directive nobody is offered.
/// <para>
/// Each directive is written as the smallest whole thing it can be, at the start of a line in
/// each kind of place, and the program is built. Allowed here means nothing is reported about
/// those lines; offered here is what <see cref="Directives"/> answers for the same caret.
/// </para>
/// </summary>
public sealed class DirectivePlacesTests
{
    /// <summary>
    /// What each directive is as a line of its own, with <c>#</c> standing for a number that
    /// keeps each one's names to itself. A block opener closes its own block, so that a whole
    /// place's worth of them can be written into one program.
    /// </summary>
    private static readonly Dictionary<string, string> Written = new(StringComparer.Ordinal)
    {
        [".addr"] = ".addr 0",
        [".align"] = ".align 2",
        [".assert"] = ".assert 1 == 1",
        [".bankbytes"] = ".bankbytes 0",
        [".bedword"] = ".bedword 0",
        [".belong"] = ".belong 0",
        [".beword"] = ".beword 0",
        [".byte"] = ".byte 0",
        [".charmap"] = ".charmap chars# {\n'a' = 1\n}",
        [".config"] = ".config SETTING# = 1",
        [".cpu"] = ".cpu 65816",
        [".data"] = ".data room#: .byte 0",
        [".dword"] = ".dword 0",
        [".each"] = ".each Kind, kind# {\n}",
        [".ensure"] = ".ensure a8",
        [".enum"] = ".enum Sort# {\nfirst#\n}",
        [".export"] = ".export SHARED# = 1",
        [".faraddr"] = ".faraddr 0",
        [".func"] = ".func twice#(v) = v * 2",
        [".hibytes"] = ".hibytes 0",
        [".if"] = ".if 1 {\n}",
        [".import"] = ".import outside#",
        [".list"] = ".list numbers# {\n1\n}",
        [".lobytes"] = ".lobytes 0",
        [".long"] = ".long 0",
        [".macro"] = ".macro written#() {\n}",
        [".multiproc"] = ".multiproc Kind, sort# {\nrts\n}",
        [".proc"] = ".proc routine# {\nrts\n}",
        [".repeat"] = ".repeat 2 {\n}",
        [".res"] = ".res 1",
        [".scope"] = ".scope area# {\n}",
        [".segment"] = ".segment RODATA {\n}",
        [".signature"] = ".signature usual# = a8",
        [".state"] = ".state a8",
        [".strz"] = ".strz \"x\"",
        [".struct"] = ".struct Shape# {\nwide#: .byte\n}",
        [".type"] = ".type Thing",
        [".union"] = ".union Either# {\nwide#: .byte\n}",
        [".use"] = ".use other as brought#",
        [".warning"] = ".warning \"x\"",
        [".word"] = ".word 0",
    };

    /// <summary>
    /// The ones that are not a line of their own, and so are held to nothing here: two
    /// continue a block that has to be open above them, two are about the statement above
    /// them, one is about where the stack has been left, one names the module and may only be
    /// a file's first line, one names a file on disk, and one fails the build on purpose.
    /// </summary>
    private static readonly string[] Partial =
        [".else", ".elseif", ".error", ".frame", ".incbin", ".module", ".next", ".patch"];

    /// <summary>
    /// What the server offers at a place and the binder refuses there, which is a list that
    /// wants shortening. The one left is a <c>.config</c> under an open <c>.segment</c> region:
    /// the binder wants a setting above every block, and the lists do not know a region from
    /// a file with none.
    /// </summary>
    private static readonly Dictionary<string, string[]> Refused = new(StringComparer.Ordinal)
    {
        ["file level"] = [".config"],
        ["a routine"] = [],
        ["a macro body"] = [],
        ["a repetition"] = [],
    };

    /// <summary>
    /// What the binder takes at a place and the server does not offer there, which is the
    /// server being stricter on purpose. A nested <c>.proc</c> is code the outer routine's
    /// flow analysis cannot follow; a <c>.cpu</c> inside a routine states the program's
    /// processor from somewhere nobody reads for one; and a <c>.segment</c> block inside a
    /// macro body or a repetition would be written out once per expansion.
    /// </summary>
    private static readonly Dictionary<string, string[]> Stricter = new(StringComparer.Ordinal)
    {
        ["file level"] = [],
        ["a routine"] = [".cpu", ".proc"],
        ["a macro body"] = [".segment"],
        ["a repetition"] = [".segment"],
    };

    /// <summary>Nothing may be offered that has no minimal form written for it here.</summary>
    [Fact]
    public void EveryDirectiveTheListsKnowIsWrittenHere()
    {
        Assert.Equal(
            Directives.All.Order(StringComparer.Ordinal),
            Written.Keys.Concat(Partial).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("file level")]
    [InlineData("a routine")]
    [InlineData("a macro body")]
    [InlineData("a repetition")]
    public void WhatIsOfferedIsWhatTheBinderTakes(string place)
    {
        var offered = Offered(place);
        var allowed = new List<string>();
        foreach (var directive in Written.Keys.Order(StringComparer.Ordinal))
        {
            if (Errors(place, directive).Count == 0)
                allowed.Add(directive);
        }

        // Nothing is offered twice, and everything offered is a line the binder takes.
        Assert.Equal(offered.Distinct(StringComparer.Ordinal), offered);
        Assert.Equal(
            Refused[place],
            offered.Where(name => !allowed.Contains(name)));

        // And the converse, but for the few the server is stricter about than the binder.
        Assert.Equal(Stricter[place], allowed.Where(name => !offered.Contains(name)));
    }

    /// <summary>What the server would offer at the start of a line in <paramref name="place"/>.</summary>
    private static IReadOnlyList<string> Offered(string place)
    {
        var (text, line) = Host(place, "");
        var tree = SyntaxTree.Parse(Analysis.Path, text);
        var caret = LineContext.At(tree, tree.LineStarts[line]);
        return [.. Directives.At(caret).Select(item => item.Name).Where(name => !Partial.Contains(name))];
    }

    /// <summary>What is reported about the lines <paramref name="directive"/> is written on.</summary>
    private static IReadOnlyList<string> Errors(string place, string directive)
    {
        var snippet = Written[directive].Replace("#", "1", StringComparison.Ordinal);
        var (text, line) = Host(place, snippet);
        var analysis = Analysis.Program(
            (Analysis.Path, text),
            ("other.nt65", ".module other\n.export OUTSIDE = 1\n"));
        var last = line + snippet.Split('\n').Length;
        return
        [
            .. analysis.DiagnosticsFor(Analysis.Path)
                .Where(problem => problem.Severity == Severity.Error
                    && problem.Span.Line - 1 >= line && problem.Span.Line - 1 < last)
                .Select(problem => $"{problem.Id}: {problem.Message}"),
        ];
    }

    /// <summary>
    /// A program holding <paramref name="written"/> in <paramref name="place"/>, and the line
    /// it starts on. The preamble declares the things a snippet names, so that what is reported
    /// about a line is about where it is written and not about what it could not find.
    /// </summary>
    private static (string Text, int Line) Host(string place, string written)
    {
        const string Preamble = """
            .module main
            .cpu 65816
            .export .enum Kind {
            one
            }
            .export .struct Thing {
            wide: .byte
            }
            .segment CODE

            """;
        var (before, after) = place switch
        {
            "a routine" => (".export .proc host {\n", "rts\n}\n"),
            "a macro body" => (".macro host() {\n", "}\n"),
            "a repetition" => (".repeat 2 {\n", "}\n"),
            _ => ("", ""),
        };
        var head = Preamble.ReplaceLineEndings("\n") + before;
        return (head + written + (written.Length > 0 ? "\n" : "") + after, head.Split('\n').Length - 1);
    }
}
