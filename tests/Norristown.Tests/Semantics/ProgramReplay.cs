using System.Text;
using System.Text.Json;
using Norristown.Emit;
using Norristown.LanguageServer;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// Represents a program as an editor holds it. The trees are edited in place and reanalyzed
/// incrementally from the previous analysis. After each edit, the result is compared with the
/// program parsed and analyzed from scratch.
/// </summary>
internal sealed class ProgramReplay
{
    public static readonly ProjectSettings Project = ProjectSettings.None with
    {
        Defines = [new Define("DEBUG", 1, default)],
    };

    public static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        // This file is read before every other file. So when the whole program is analyzed, a
        // cycle through `BASE` is reached from here first, and when only part of it is analyzed,
        // the cycle is reached from elsewhere.
        ["app.nt65"] = """
            .module app
            .cpu 65816
            .use main::BASE

            ENTRY = BASE

            """,
        ["defs.nt65"] = """
            .module defs
            .cpu 65816
            .use main::BASE
            .use gfx::{relay, COLORS}

            .export SCREEN, WIDTH, HEIGHT, Point, set16, rgb, LIMIT, scaled, fill_screen, FILL, ping, std

            SCREEN = $2000
            WIDTH  = 32
            HEIGHT = WIDTH - 4
            LIMIT  = BASE + 1
            PRIVATE_K = 7
            SCALE  = 3
            FILL   = $20

            ; The state gfx.nt65's routines take.
            .signature std = a8, i8

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
            .module main
            .cpu 65816
            .use defs::{WIDTH, HEIGHT, SCREEN, LIMIT, set16, fill_screen, ping}
            .use gfx::{draw, COLORS}
            .use types::Line

            .export main, BASE

            BASE = 3
            MAIN_PRIVATE = 9

            .segment BSS
            .data cursor: .word
            .data track:  .type Line

            .segment RODATA
            .data route:  .type Line { from = { x = 1 } }

            .if DEBUG {
            TRACE = 1
            }

            .segment CODE
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
            .module gfx
            .cpu 65816
            .use defs::{rgb, scaled, SCREEN, WIDTH, ping, std}

            .export draw, clear, COLORS, relay

            COLORS = rgb(31, 0, 0)
            STEP   = scaled(2)

            .segment RODATA
            .data sprites:    .incbin "sprites.bin"
            .data palette:    .word COLORS, rgb(0, 31, 0)

            .segment CODE
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

            .proc clear: std {
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
            .module types
            .cpu 65816
            .use defs::Point

            .export Line

            ; A type laid out from another file's type, and used by a third file.
            .struct Line {
            from:   .type Point
            to:     .type Point
            }

            .segment BSS
            .data origin: .type Point

            .segment CODE
            .proc home: a8, i8 {
                lda origin + Point::y
                rts
            }

            """,
        ["errors.nt65"] = """
            .module errors
            .cpu 65816

            .segment CODE
            .proc broken: a8, i8 {
                lda #main::MAIN_PRIVATE
                lda #defs::PRIVATE_K
                lda undeclared
                jsr gfx::draw
                rts
            }

            """,
        ["segs.nt65"] = """
            .module segs
            .cpu 65816

            .export hud_value

            ; Nothing names it, so this file is always warned about it — until something goes
            ; wrong here, when a file's warnings give way to what is actually wrong with it.
            HUD_SPARE = 3

            .segment HUD: zp

            .segment HUD
            .data hud_value:  .byte

            .segment CODE
            .proc hud: a8, i8 {
                lda hud_value
                rts
            }

            """,
    };

    private readonly Dictionary<string, SyntaxTree> trees;
    private ProgramAnalysis analysis;

    public ProgramReplay()
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

    /// <summary>
    /// Renders everything an analysis answers as text, so that two analyses can be compared.
    /// </summary>
    private static string Snapshot(ProgramAnalysis analysis)
    {
        var text = new StringBuilder();
        foreach (var d in analysis.Diagnostics)
            text.Append($"{Format(d)}\n");

        // Every file is emitted even though the program has errors. The program contains
        // mistakes on purpose, and compiling a program with errors writes nothing.
        var emitted = new List<Diagnostic>();
        foreach (var file in analysis.Files)
        {
            var model = file.Model;
            if (model.Tree != analysis.Defines)
            {
                var output = Emitter.Emit(
                    model, file.Layout, FlatNames.Create(model, analysis.Cpu, emitted), emitted, Project.Out);
                text.Append($"== {output.Path}\n{output.Text}");
            }
        }
        foreach (var d in Diagnostics.Ordered(emitted))
            text.Append($"emit {Format(d)}\n");

        foreach (var model in analysis.Program.Files.OrderBy(file => file.Tree.Path, StringComparer.Ordinal))
        {
            var tree = model.Tree;
            text.Append($"== {tree.Path} omits {string.Join(", ", analysis.Configuration.Omitted(tree))}\n");
            text.Append($"imports {string.Join(", ", model.ExternalSymbols.Select(symbol => $"{symbol.Tree.Path} {symbol.QualifiedName}"))}\n");
            foreach (var start in tree.LineStarts)
                text.Append($"{start}: {Json(Lsp.ToHover(analysis, model, start))}\n");
            foreach (var reference in model.References)
            {
                var position = reference.Span.Start;
                text.Append($"{position} {Json(Lsp.ToHover(analysis, model, position))}"
                    + $" -> {Json(Lsp.ToDefinition(analysis.Program, model, position))}"
                    + $" all {Json(Lsp.ToReferences(analysis.Program, model, position, includeDeclaration: true))}\n");
            }
        }
        return text.ToString();

        static string Format(Diagnostic d) =>
            $"{d.Span} {d.Severity} {d.Message} [{string.Join("; ", d.Related.Select(r => $"{r.Span} {r.Message}"))}]";

        static string Json(object? value) => JsonSerializer.Serialize(value);
    }

    private long? BinaryLength(string path) => path.EndsWith("sprites.bin", StringComparison.Ordinal) ? Length : null;
}
