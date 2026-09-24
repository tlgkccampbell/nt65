using Norristown.LanguageServer;
using Norristown.Syntax;
using Norristown.Tests.Semantics;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Checks the server's lists of which directives may start a line in each kind of place against
/// the binder, which is what actually decides. Nothing in the code ties the two together. A
/// directive offered where the binder rejects it is a completion that inserts a broken line, and
/// a directive the binder accepts but the list leaves out is never offered.
/// <para>
/// Each directive is placed in its smallest complete form at the start of a line in each kind
/// of place, and the program is built. "Allowed" here means no error is reported on those lines;
/// "offered" means <see cref="Directives"/> lists it for a caret at the same position.
/// </para>
/// </summary>
public sealed class DirectivePlacesTests
{
    /// <summary>
    /// The smallest complete form of each directive, as a line of its own. A <c>#</c> marks where
    /// a number is appended to a name the snippet declares, so that snippets' names would not
    /// collide. A block opener closes its own block, so each snippet is complete by itself.
    /// </summary>
    private static readonly Dictionary<string, string> Snippets = new(StringComparer.Ordinal)
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
    /// The directives that cannot be tested as a line of their own, and so are not checked here.
    /// <c>.else</c> and <c>.elseif</c> continue a block that must be open above them.
    /// <c>.next</c> and <c>.patch</c> describe the statement above them. <c>.fallthrough</c> may
    /// only be the last line of a routine's body. <c>.frame</c> describes where the stack has
    /// been left. <c>.module</c> names the module and may only be a file's first line.
    /// <c>.place</c> emits a module that must declare that it may be placed. <c>.incbin</c>
    /// names a file on disk. <c>.error</c> fails the build on purpose.
    /// </summary>
    private static readonly string[] Partial =
    [
        ".else", ".elseif", ".error", ".fallthrough", ".frame", ".incbin", ".module", ".next", ".patch", ".place",
    ];

    /// <summary>
    /// The directives the server offers at a place but the binder rejects there. Every list is
    /// empty, because nothing the server offers may produce a line that does not build.
    /// </summary>
    private static readonly Dictionary<string, string[]> Refused = new(StringComparer.Ordinal)
    {
        ["file level"] = [],
        ["a routine"] = [],
        ["a macro body"] = [],
        ["a repetition"] = [],
    };

    /// <summary>
    /// The directives the binder accepts at a place but the server does not offer there, where
    /// the server is deliberately stricter. A nested <c>.proc</c> is code the outer routine's
    /// flow analysis cannot follow; a <c>.cpu</c> inside a routine sets the program's processor
    /// from a place nobody would look for it; and a <c>.segment</c> block inside a macro body or
    /// a repetition would be written out once per expansion.
    /// </summary>
    private static readonly Dictionary<string, string[]> Stricter = new(StringComparer.Ordinal)
    {
        ["file level"] = [],
        ["a routine"] = [".cpu", ".proc"],
        ["a macro body"] = [".segment"],
        ["a repetition"] = [".segment"],
    };

    /// <summary>Every directive the server knows either has a complete form above or is listed as partial.</summary>
    [Fact]
    public void EveryDirectiveTheListsKnowHasASnippetHere()
    {
        Assert.Equal(
            Directives.All.Order(StringComparer.Ordinal),
            Snippets.Keys.Concat(Partial).Order(StringComparer.Ordinal));
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
        foreach (var directive in Snippets.Keys.Order(StringComparer.Ordinal))
        {
            if (Errors(place, directive).Count == 0)
                allowed.Add(directive);
        }

        // Nothing is offered twice, and everything offered is a line the binder accepts.
        Assert.Equal(offered.Distinct(StringComparer.Ordinal), offered);
        Assert.Equal(
            Refused[place],
            offered.Where(name => !allowed.Contains(name)));

        // And everything the binder accepts is offered, except the few the server is stricter about.
        Assert.Equal(Stricter[place], allowed.Where(name => !offered.Contains(name)));
    }

    /// <summary>
    /// <c>.config</c> is offered only where a setting is allowed, which is before anything has
    /// been opened. The places tested above all sit under the preamble's <c>.segment CODE</c>, which
    /// the binder counts as opened, so this test covers the other case, a file that has opened
    /// nothing yet.
    /// </summary>
    [Fact]
    public void ASettingIsOfferedWhileNothingIsOpenAndNotAfterwards()
    {
        var text = ".module main\n\n.segment CODE\n\n.proc host {\n\n}\n";
        var tree = SyntaxTree.Parse(Analysis.Path, text);
        string[] Offers(int line) => [.. Directives.At(LineContext.At(tree, tree.LineStarts[line])).Select(item => item.Name)];

        Assert.Contains(".config", Offers(1));
        Assert.DoesNotContain(".config", Offers(3));
        Assert.DoesNotContain(".config", Offers(5));
    }

    /// <summary>
    /// A <c>.place</c> is offered at file level, both before any segment region and inside one,
    /// but not inside a block, because which modules share a translation unit cannot depend on
    /// anything a block could decide.
    /// </summary>
    [Fact]
    public void APlaceIsOfferedAtFileLevelOnly()
    {
        var text = ".module main\n\n.segment CODE\n\n.scope area {\n\n}\n\n.if 1 {\n\n}\n";
        var tree = SyntaxTree.Parse(Analysis.Path, text);
        string[] Offers(int line) => [.. Directives.At(LineContext.At(tree, tree.LineStarts[line])).Select(item => item.Name)];

        Assert.Contains(".place", Offers(1));
        Assert.Contains(".place", Offers(3));
        Assert.DoesNotContain(".place", Offers(5));
        Assert.DoesNotContain(".place", Offers(9));
    }

    /// <summary>
    /// Returns the directives, other than the partial ones, that the server would offer at the
    /// start of a line in <paramref name="place"/>.
    /// </summary>
    private static IReadOnlyList<string> Offered(string place)
    {
        var (text, line) = Host(place, "");
        var tree = SyntaxTree.Parse(Analysis.Path, text);
        var caret = LineContext.At(tree, tree.LineStarts[line]);
        return [.. Directives.At(caret).Select(item => item.Name).Where(name => !Partial.Contains(name))];
    }

    /// <summary>
    /// Returns the errors reported on the lines that <paramref name="directive"/> occupies when
    /// it is placed in <paramref name="place"/>.
    /// </summary>
    private static IReadOnlyList<string> Errors(string place, string directive)
    {
        var snippet = Snippets[directive].Replace("#", "1", StringComparison.Ordinal);
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
    /// Builds a program that contains <paramref name="snippet"/> in <paramref name="place"/>, and
    /// returns it with the line on which <paramref name="snippet"/> starts. The preamble declares
    /// the things a snippet names, so that what is reported about a line concerns where the line
    /// is and not a name it could not find.
    /// </summary>
    private static (string Text, int Line) Host(string place, string snippet)
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
            "file level" => ("", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(place), place, "no such place to write a snippet in"),
        };
        var head = Preamble.ReplaceLineEndings("\n") + before;
        return (head + snippet + (snippet.Length > 0 ? "\n" : "") + after, head.Split('\n').Length - 1);
    }
}
