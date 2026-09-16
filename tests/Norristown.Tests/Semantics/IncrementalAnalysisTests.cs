using System.Text;
using System.Text.Json;
using Norristown.LanguageServer;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// An analysis that starts from the one before an edit gives exactly what analyzing the
/// program from scratch gives: the same diagnostics, the same output, and the same answers to
/// every question an editor asks. Edit sequences are replayed both ways and compared after
/// every edit.
/// </summary>
public sealed class IncrementalAnalysisTests
{
    private static readonly ProjectSettings Project = ProjectSettings.None with
    {
        Defines = [new Define("DEBUG", 1, default)],
    };

    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        ["defs.nt65"] = """
            .cpu 65816

            .export SCREEN, WIDTH, HEIGHT, Point, set16, rgb, LIMIT

            SCREEN = $2000
            WIDTH  = 32
            HEIGHT = WIDTH - 4
            LIMIT  = BASE + 1
            PRIVATE_K = 7

            .struct Point {
            x:      .word
            y:      .word
            }

            .func rgb(r, g, b) = r | (g << 5) | (b << 10)

            .macro set16(dest: operand, value) {
                lda #<value
                sta dest
                lda #>value
                sta dest+1
            }

            """,
        ["main.nt65"] = """
            .cpu 65816

            .export main, BASE

            BASE = 3
            MAIN_PRIVATE = 9

            .bss {
            cursor: .res 2
            }

            .if DEBUG {
            TRACE = 1
            }

            .proc main: a8, i8 {
                ldx #WIDTH
            @loop:
                lda #HEIGHT
                sta cursor
                dex
                bne @loop
                jsr draw
                set16!(cursor, SCREEN)
                rts
            }

            """,
        ["gfx.nt65"] = """
            .cpu 65816

            .export draw, clear, COLORS

            COLORS = rgb(31, 0, 0)

            .rodata {
            sprites:    .incbin "sprites.bin"
            palette:    .word COLORS, rgb(0, 31, 0)
            }

            .proc draw: a8, i8 {
                ldy #0
            @next:
                lda sprites,y
                sta SCREEN,y
                iny
                cpy #.sizeof(sprites)
                bne @next
                jsr clear
                rts
            }

            .proc clear: a8, i8 {
                lda #0
                ldx #WIDTH - 1
            @wipe:
                sta SCREEN,x
                dex
                bpl @wipe
                rts
            }

            """,
        ["types.nt65"] = """
            .cpu 65816

            .bss {
            origin: .tag Point
            }

            .proc home: a8, i8 {
                lda origin + Point::y
                rts
            }

            """,
        ["errors.nt65"] = """
            .cpu 65816

            .proc broken: a8, i8 {
                lda #MAIN_PRIVATE
                lda #PRIVATE_K
                lda undeclared
                jsr draw
                rts
            }

            """,
        ["segs.nt65"] = """
            .cpu 65816

            .export hud_value

            .segment "HUD": zp

            .segment "HUD" {
            hud_value:  .res 1
            }

            .proc hud: a8, i8 {
                lda hud_value
                rts
            }

            """,
    };

    /// <summary>
    /// Each edit says whether only the file it is in should be analyzed again: an edit inside a
    /// routine body can be, one that changes what another file sees cannot.
    /// </summary>
    [Fact]
    public void ScriptedEditsMatchAnalyzingFromScratch()
    {
        (string Path, string Find, string Replace, bool OneFile)[] edits =
        [
            ("main.nt65", "lda #HEIGHT", "lda #HEIGHT + 1", true),     // a routine body
            ("main.nt65", ".proc main", "\n\n.proc main", true),       // lines move below the edit
            ("main.nt65", "MAIN_PRIVATE = 9", "MAIN_PRIVATE  = 9", true), // named elsewhere, still 9
            ("main.nt65", "BASE = 3", "BASE = 4", false),              // an exported value
            ("gfx.nt65", "    iny\n", "    iny\n    iny\n", true),
            ("gfx.nt65", "rgb(31, 0, 0)", "rgb(31, 1, 0)", false),
            ("errors.nt65", "lda undeclared", "lda #1", true),
            ("segs.nt65", "lda hud_value", "ldx hud_value", false),    // the file declares a segment
            ("defs.nt65", "WIDTH  = 32", "WIDTH  = 30", false),
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 7 ; seven", false), // `origin` holds `Point` itself
            ("types.nt65", "Point::y", "Point::x", true),
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 8", false),    // another file names it
            ("main.nt65", "    rts\n}", "extra:\n    rts\n}", false),  // a new name a path reaches
            ("main.nt65", "BASE = 4", "BASE = LIMIT", false),          // a cycle through two files
            ("main.nt65", "sta cursor", "sta cursor+1", false),        // still in the cycle
            ("main.nt65", "BASE = LIMIT", "BASE = 4", false),
            ("main.nt65", "dex", "dex\n    dex", true),
            ("main.nt65", "MAIN_PRIVATE  = 9", "MAIN_PRIVATE = 10", false),
            ("errors.nt65", "jsr draw", "jsr clear", true),
            ("main.nt65", ".if DEBUG {", ".if !DEBUG {", false),       // TRACE is gone
            ("main.nt65", "BASE = 4", "BASE = 4 ; four", true),        // what it means is the same
        ];

        var replay = new Replay();
        foreach (var (path, find, replace, oneFile) in edits)
        {
            var at = replay.Text(path).IndexOf(find, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{path} has no \"{find}\"");
            var analysis = replay.Change(path, at, find.Length, replace);
            Assert.True((analysis.Reanalyzed == 1) == oneFile,
                $"{path}: \"{find}\" analyzed {analysis.Reanalyzed} file(s)");
        }
    }

    /// <summary>
    /// Edits at random places, most of which leave a file that does not parse. Whatever the
    /// edit breaks, the analysis that starts from the one before it has to break the same way.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomEditsMatchAnalyzingFromScratch(int seed)
    {
        string[] snippets = ["x", "1", " ", "\n", "#", "lda #2\n", ";", "}", "{", ":", "::", "@", "!", ".export ", "WIDTH", "dex\n"];
        var random = new Random(seed);
        var replay = new Replay();
        var paths = Sources.Keys.Order(StringComparer.Ordinal).ToArray();
        var oneFile = 0;
        for (var step = 0; step < 60; step++)
        {
            var path = paths[random.Next(paths.Length)];
            var length = replay.Text(path).Length;
            var at = random.Next(length + 1);
            var analysis = random.Next(3) == 0
                ? replay.Change(path, at, Math.Min(random.Next(1, 6), length - at), "")
                : replay.Insert(path, at, snippets[random.Next(snippets.Length)]);
            if (analysis.Reanalyzed == 1)
                oneFile++;
        }
        Assert.True(oneFile > 0, "no random edit was analyzed on its own");
    }

    /// <summary>An <c>.incbin</c> file that changed on disk is a change to the files that include it.</summary>
    [Fact]
    public void AnIncbinThatChangedIsAnalyzedAgain()
    {
        var replay = new Replay();
        Assert.Equal(1, replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("dex", StringComparison.Ordinal), 3, "inx").Reanalyzed);
        replay.Length = 32;
        Assert.NotEqual(1, replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("inx", StringComparison.Ordinal), 3, "dex").Reanalyzed);
    }

    /// <summary>Everything an analysis answers, written out so two analyses can be compared.</summary>
    private static string Snapshot(ProgramAnalysis analysis)
    {
        var text = new StringBuilder();
        foreach (var d in analysis.Diagnostics)
            text.Append($"{Spell(d)}\n");

        var compilation = Compiler.Emit(analysis, Project);
        foreach (var output in compilation.Outputs)
            text.Append($"== {output.Path}\n{output.Text}");
        foreach (var d in compilation.Diagnostics)
            text.Append($"emit {Spell(d)}\n");

        foreach (var model in analysis.Program.Files.OrderBy(file => file.Tree.Path, StringComparer.Ordinal))
        {
            var tree = model.Tree;
            text.Append($"== {tree.Path} omits {string.Join(", ", analysis.Configuration.Omitted(tree))}\n");
            var layout = analysis.LayoutFor(tree.Path);
            var flow = analysis.FlowFor(tree.Path);
            var states = analysis.StatesFor(tree.Path);
            foreach (var start in tree.LineStarts)
                text.Append($"{start}: {Json(Lsp.ToHover(model, layout, flow, states, start))}\n");
            foreach (var reference in model.References)
            {
                var position = reference.Span.Start;
                text.Append($"{position} {Json(Lsp.ToHover(model, layout, flow, states, position))}"
                    + $" -> {Json(Lsp.ToDefinition(analysis.Program, model, position))}"
                    + $" all {Json(Lsp.ToReferences(analysis.Program, model, position, includeDeclaration: true))}\n");
            }
        }
        return text.ToString();

        static string Spell(Diagnostic d) =>
            $"{d.Span} {d.Severity} {d.Message} [{string.Join("; ", d.Related.Select(r => $"{r.Span} {r.Message}"))}]";

        static string Json(object? value) => JsonSerializer.Serialize(value);
    }

    /// <summary>
    /// The program as an editor holds it: trees edited in place, analyzed from the analysis
    /// before, and compared after each edit with the program parsed and analyzed afresh.
    /// </summary>
    private sealed class Replay
    {
        private readonly Dictionary<string, SyntaxTree> trees;
        private ProgramAnalysis analysis;

        public Replay()
        {
            trees = Sources.ToDictionary(
                pair => pair.Key, pair => SyntaxTree.Parse(pair.Key, pair.Value.ReplaceLineEndings("\n")), StringComparer.Ordinal);
            analysis = Compiler.Analyze(trees.Values, Project, BinaryLength);
        }

        public long Length { get; set; } = 16;

        public string Text(string path) => trees[path].Text;

        public ProgramAnalysis Insert(string path, int at, string text) => Change(path, at, 0, text);

        public ProgramAnalysis Change(string path, int at, int length, string text)
        {
            trees[path] = trees[path].WithChange(new TextChange(at, length, text));
            analysis = Compiler.Analyze(trees.Values, Project, BinaryLength, analysis);

            var fresh = Compiler.Analyze(
                [.. trees.Values.Select(tree => SyntaxTree.Parse(tree.Path, tree.Text))], Project, BinaryLength);
            var expected = Snapshot(fresh);
            var actual = Snapshot(analysis);
            if (expected != actual)
            {
                var line = expected.Split('\n').Zip(actual.Split('\n')).FirstOrDefault(pair => pair.First != pair.Second);
                Assert.Fail($"after editing {path} at {at}, {analysis.Reanalyzed} file(s) analyzed:\n"
                    + $"from scratch: {line.First}\nincremental:  {line.Second}");
            }
            return analysis;
        }

        private long? BinaryLength(string path) => path.EndsWith("sprites.bin", StringComparison.Ordinal) ? Length : null;
    }
}
