using System.Text;
using System.Text.Json;
using Norristown.Emit;
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
public sealed class IncrementalAnalysisTests(ITestOutputHelper output)
{
    private static readonly ProjectSettings Project = ProjectSettings.None with
    {
        Defines = [new Define("DEBUG", 1, default)],
    };

    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        // Read before every other file, so a cycle through `BASE` is reached from here first
        // when the whole program is analyzed, and from elsewhere when only part of it is.
        ["app.nt65"] = """
            .cpu 65816

            ENTRY = BASE

            """,
        ["defs.nt65"] = """
            .cpu 65816

            .export SCREEN, WIDTH, HEIGHT, Point, set16, rgb, LIMIT, scaled, fill_screen, FILL, ping

            SCREEN = $2000
            WIDTH  = 32
            HEIGHT = WIDTH - 4
            LIMIT  = BASE + 1
            PRIVATE_K = 7
            SCALE  = 3
            FILL   = $20

            .struct Point {
            x:      .word
            y:      .word
            }

            .func rgb(r, g, b) = r | (g << 5) | (b << 10)

            ; Bodies that name what their callers never look up themselves.
            .func scaled(v) = v * SCALE

            .macro fill_screen(count) {
                lda #FILL
                ldx #count
                sta SCREEN,x
            }

            ; Calls a macro of gfx.nt65, which an edit there makes call this one back, and uses a
            ; constant of it.
            .macro ping() {
                relay!(1)
                lda #COLORS
            }

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
            track:  .tag Line
            }

            .rodata {
            route:  .tag Line { from = { x = 1 } }
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
                fill_screen!(4)
                ping!()
                lda #COLORS
                rts
            }

            """,
        ["gfx.nt65"] = """
            .cpu 65816

            .export draw, clear, COLORS, relay

            COLORS = rgb(31, 0, 0)
            STEP   = scaled(2)

            .rodata {
            sprites:    .incbin "sprites.bin"
            palette:    .word COLORS, rgb(0, 31, 0)
            }

            .proc draw: a8, i8 {
                ldy #STEP
            @next:
                lda sprites,y
                sta SCREEN,y
                iny
                cpy #.sizeof(sprites)
                bne @next
                jsr clear
                rts
            }

            .macro relay(n) {
                nop
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

            .export Line

            ; A type laid out from another file's type, and used by a third file.
            .struct Line {
            from:   .tag Point
            to:     .tag Point
            }

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
    /// Each edit says why the whole program should be analyzed again, or null when it should
    /// not be, and how many files should be: the file it is in, and every file the change reaches.
    /// </summary>
    [Fact]
    public void ScriptedEditsMatchAnalyzingFromScratch()
    {
        (string Path, string Find, string Replace, WholeProgramReason? Reason, int Files)[] edits =
        [
            ("main.nt65", "lda #HEIGHT", "lda #HEIGHT + 1", null, 1),                                // a routine body
            ("main.nt65", ".proc main", "\n\n.proc main", null, 1),                                  // lines move below the edit
            ("main.nt65", "MAIN_PRIVATE = 9", "MAIN_PRIVATE  = 9", null, 1),                         // errors.nt65 names it, and it is still 9
            ("main.nt65", "BASE = 3", "BASE = 4", null, 3),                                          // app and defs look it up
            ("gfx.nt65", "    iny\n", "    iny\n    iny\n", null, 3),                                // `relay` moves down: defs calls it, main calls defs' `ping`
            ("gfx.nt65", "rgb(31, 0, 0)", "rgb(31, 1, 0)", null, 3),                                 // main and defs look it up
            ("errors.nt65", "lda undeclared", "lda #1", null, 1),
            ("segs.nt65", "lda hud_value", "ldx hud_value", WholeProgramReason.SegmentsDeclared, 8), // the file declares a segment
            ("defs.nt65", "WIDTH  = 32", "WIDTH  = 30", null, 3),                                    // main and gfx look it up
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 7 ; seven", null, 1),                        // `origin` holds `Point`, which is the same
            ("types.nt65", "Point::y", "Point::x", null, 1),
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 8", null, 2),                                // errors.nt65 looks it up
            ("main.nt65", "    rts\n}", "extra:\n    rts\n}", null, 1),                              // a new name, which nobody looks up
            ("main.nt65", "BASE = 4", "BASE = LIMIT", null, 3),                                      // a cycle through two files
            ("main.nt65", "sta cursor", "sta cursor+1", null, 2),                                    // still in it, and app.nt65 sees no change
            ("main.nt65", "BASE = LIMIT", "BASE = 4", null, 3),
            ("main.nt65", "dex", "dex\n    dex", null, 1),
            ("main.nt65", "MAIN_PRIVATE  = 9", "MAIN_PRIVATE = 10", null, 2),                        // errors.nt65 looks it up
            ("errors.nt65", "jsr draw", "jsr clear", null, 1),
            ("main.nt65", ".if DEBUG {", ".if !DEBUG {", null, 1),                                   // `TRACE` is gone, and nobody looked it up
            ("main.nt65", "BASE = 4", "BASE = 4 ; four", null, 1),                                   // what it means is the same
            ("defs.nt65", "SCALE  = 3", "SCALE  = 4", null, 2),                                      // gfx calls `scaled`, whose body names it
            ("defs.nt65", "FILL   = $20", "FILL   = $2e", null, 2),                                  // main expands `fill_screen`, whose body names it
            ("gfx.nt65", "    lda #0\n", "    lda #0 ; clear\n", null, 1),                           // defs' `ping` goes on holding the old `COLORS`
            ("main.nt65", "sta cursor+1", "sta cursor", null, 1),                                    // which main's expansion of `ping` reaches
            ("defs.nt65", "y:      .word\n", "y:      .word\nz:      .word\n", null, 3),             // types lays `Line` out from it, main uses `Line`
            ("defs.nt65", "z:      .word", "w:      .word", null, 3),                                // the same size, and main writes the names out
            ("gfx.nt65", "    nop\n", "    ping!()\n", null, 3),                                     // two macros in two files now call each other
            ("defs.nt65", "relay!(1)", "relay!(2)", null, 3),                                        // still
            ("defs.nt65", "; Calls a macro", "; It calls a macro", null, 1),                         // gfx's `relay` goes on holding the old `ping`
            ("defs.nt65", "; It calls", "; Here.\n; It calls", null, 3),                             // `ping` moves, and callers write its calls' lines
            ("gfx.nt65", "    ping!()\n", "    nop\n", null, 3),
        ];

        var replay = new Replay();
        foreach (var (path, find, replace, reason, count) in edits)
        {
            var at = replay.Text(path).IndexOf(find, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{path} has no \"{find}\"");
            var analysis = replay.Change(path, at, find.Length, replace);
            Assert.True(analysis.WholeProgram == reason && analysis.Reanalyzed == count,
                $"{path}: \"{find}\" analyzed {analysis.Reanalyzed} file(s), because {analysis.WholeProgram}");
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
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var step = 0; step < 60; step++)
        {
            var path = paths[random.Next(paths.Length)];
            var length = replay.Text(path).Length;
            var at = random.Next(length + 1);
            var analysis = random.Next(3) == 0
                ? replay.Change(path, at, Math.Min(random.Next(1, 6), length - at), "")
                : replay.Insert(path, at, snippets[random.Next(snippets.Length)]);
            var reason = analysis.WholeProgram?.ToString() ?? $"{analysis.Reanalyzed} of {Sources.Count} files";
            why[reason] = why.GetValueOrDefault(reason) + 1;
        }
        foreach (var (reason, count) in why.OrderByDescending(pair => pair.Value))
            output.WriteLine($"{count,3} {reason}");
        Assert.True(why.ContainsKey($"1 of {Sources.Count} files"), "no random edit was analyzed on its own");
    }

    /// <summary>An <c>.incbin</c> file that changed on disk is a change to the files that include it.</summary>
    [Fact]
    public void AnIncbinThatChangedIsAnalyzedAgain()
    {
        var replay = new Replay();
        Assert.Equal(1, replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("dex", StringComparison.Ordinal), 3, "inx").Reanalyzed);
        replay.Length = 32;
        Assert.Equal(WholeProgramReason.BinaryFileChanged,
            replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("inx", StringComparison.Ordinal), 3, "dex").WholeProgram);
    }

    /// <summary>Everything an analysis answers, written out so two analyses can be compared.</summary>
    private static string Snapshot(ProgramAnalysis analysis)
    {
        var text = new StringBuilder();
        foreach (var d in analysis.Diagnostics)
            text.Append($"{Spell(d)}\n");

        // Every file is written out, whatever is wrong with the program: the program has
        // mistakes in it on purpose, and a compilation of a wrong program writes nothing.
        var emitted = new List<Diagnostic>();
        for (var i = 0; i < analysis.Layouts.Count; i++)
        {
            var model = analysis.Program.Files[i];
            if (model.Tree != analysis.Defines)
            {
                var output = Emitter.Emit(model, analysis.Layouts[i], FlatNames.Create(model, emitted), emitted, Project.Out);
                text.Append($"== {output.Path}\n{output.Text}");
            }
        }
        foreach (var d in Diagnostics.Ordered(emitted))
            text.Append($"emit {Spell(d)}\n");

        foreach (var model in analysis.Program.Files.OrderBy(file => file.Tree.Path, StringComparer.Ordinal))
        {
            var tree = model.Tree;
            text.Append($"== {tree.Path} omits {string.Join(", ", analysis.Configuration.Omitted(tree))}\n");
            var layout = analysis.LayoutFor(tree.Path);
            var flow = analysis.FlowFor(tree.Path);
            var states = analysis.StatesFor(tree.Path);
            text.Append($"imports {string.Join(", ", model.ExternalSymbols.Select(symbol => $"{symbol.Tree.Path} {symbol.QualifiedName}"))}\n");
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
                Assert.Fail($"after replacing {length} character(s) of {path} at {at} with {JsonSerializer.Serialize(text)}, "
                    + $"{analysis.Reanalyzed} file(s) analyzed ({analysis.WholeProgram}):\n"
                    + $"from scratch: {line.First}\nincremental:  {line.Second}");
            }
            return analysis;
        }

        private long? BinaryLength(string path) => path.EndsWith("sprites.bin", StringComparison.Ordinal) ? Length : null;
    }
}
