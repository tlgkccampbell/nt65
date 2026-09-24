using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// An analysis that starts from the one before an edit gives exactly what analyzing the
/// program from scratch gives, with the same diagnostics, the same output, and the same answers
/// to every question an editor asks. Edit sequences are replayed both ways and compared after
/// every edit.
/// </summary>
public sealed class IncrementalAnalysisTests
{
    /// <summary>
    /// The analysis after each edit reports why the whole program should be analyzed again, or
    /// null when it should not be. It also reports how many files should be analyzed again, which
    /// are the file the edit is in and every file the change reaches.
    /// </summary>
    [Fact]
    public void ScriptedEditsMatchAnalyzingFromScratch()
    {
        (string Path, string Find, string Replace, WholeProgramReason? Reason, int Files)[] edits =
        [
            ("main.nt65", "lda #HEIGHT", "lda #HEIGHT + 1", null, 1),                                // a routine body
            ("main.nt65", ".proc main", "\n\n.proc main", null, 1),                                  // lines move below the edit
            ("main.nt65", "MAIN_PRIVATE = 9", "MAIN_PRIVATE  = 9", null, 1),                         // errors.nt65 names it, but its value is still 9
            ("main.nt65", "BASE = 3", "BASE = 4", null, 3),                                          // app and defs look it up
            ("gfx.nt65", "    iny\n", "    iny\n    iny\n", null, 3),                                // `relay` moves down: defs calls it, main calls defs' `ping`
            ("gfx.nt65", "rgb(31, 0, 0)", "rgb(31, 1, 0)", null, 3),                                 // main and defs look it up
            ("errors.nt65", "lda undeclared", "lda #1", null, 1),
            ("segs.nt65", "lda hud_value", "ldx hud_value", WholeProgramReason.SegmentsDeclared, 8), // the file declares a segment
            ("defs.nt65", "WIDTH  = 32", "WIDTH  = 30", null, 3),                                    // main and gfx look it up
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 7 ; seven", null, 1),                        // a comment only; `origin` holds a `Point`, which is unchanged
            ("types.nt65", "Point::y", "Point::x", null, 1),
            ("defs.nt65", "PRIVATE_K = 7", "PRIVATE_K = 8", null, 2),                                // errors.nt65 looks it up
            ("main.nt65", "    rts\n}", "extra:\n    rts\n}", null, 1),                              // a new name, which nobody looks up
            ("main.nt65", "BASE = 4", "BASE = LIMIT", null, 3),                                      // a cycle through two files
            ("main.nt65", "sta cursor", "sta cursor+1", null, 2),                                    // still in the cycle, and app.nt65 sees no change
            ("main.nt65", "BASE = LIMIT", "BASE = 4", null, 3),
            ("main.nt65", "dex", "dex\n    dex", null, 1),
            ("main.nt65", "MAIN_PRIVATE  = 9", "MAIN_PRIVATE = 10", null, 2),                        // errors.nt65 looks it up
            ("errors.nt65", "jsr gfx::draw", "jsr gfx::clear", null, 1),
            // Naming `HUD_SPARE`, which segs.nt65 does not export, is still a use of it, so
            // segs.nt65 stops being warned that nothing uses it, and removing the name brings the
            // warning back: the use is in this file and the warning it silences is in segs.nt65.
            ("errors.nt65", "lda #1\n", "lda #1\n    lda #segs::HUD_SPARE\n", null, 2),
            ("errors.nt65", "    lda #segs::HUD_SPARE\n", "", null, 2),
            ("main.nt65", ".if DEBUG {", ".if !DEBUG {", null, 1),                                   // `TRACE` is gone, and nobody looked it up
            ("main.nt65", "BASE = 4", "BASE = 4 ; four", null, 1),                                   // what it means is the same
            ("defs.nt65", "SCALE  = 3", "SCALE  = 4", null, 2),                                      // gfx calls `scaled`, whose body names it
            ("defs.nt65", "FILL   = $20", "FILL   = $2e", null, 2),                                  // main expands `fill_screen`, whose body names it
            ("gfx.nt65", "    lda #0\n", "    lda #0 ; clear\n", null, 1),                           // a comment only; defs' `ping` keeps the `COLORS` from before the edit
            ("main.nt65", "sta cursor+1", "sta cursor", null, 1),                                    // main is analyzed again, and its expansion of `ping` reaches that older `COLORS`
            ("defs.nt65", "y:      .word\n", "y:      .word\nz:      .word\n", null, 4),             // types lays `Line` out from it, main uses `Line`, and gfx brings in `ping`, which moves
            ("defs.nt65", "z:      .word", "w:      .word", null, 3),                                // a member renamed at the same size; main writes the member names into its output
            ("gfx.nt65", "    nop\n", "    ping!()\n", null, 3),                                     // two macros in two files now call each other
            ("defs.nt65", "relay!(1)", "relay!(2)", null, 3),                                        // the two macros still call each other
            ("defs.nt65", "; Calls a macro", "; It calls a macro", null, 1),                         // a comment only; gfx's `relay` keeps the `ping` from before the edit
            ("defs.nt65", "; It calls", "; Here.\n; It calls", null, 3),                             // `ping` moves down, and its callers' output gives the lines of its calls
            ("gfx.nt65", "    ping!()\n", "    nop\n", null, 3),
            ("defs.nt65", "std = a8, i8", "std = a16, i8", null, 3),                                 // gfx's `clear` takes the signature `std`, and errors calls `clear`
            ("defs.nt65", "std = a16, i8", "std = a8, i8", null, 3),
            // Two names that collide under one linker name are reported on the file that sorts
            // later, which is not the file that changed. That file is analyzed again all the same.
            ("app.nt65", "ENTRY = BASE", "ENTRY = BASE\n.export ENTRY as \"segs__hud_value\"", null, 2),
            ("app.nt65", "\n.export ENTRY as \"segs__hud_value\"", "", null, 2),
            ("main.nt65", "MAIN_PRIVATE = 10", "MAIN_PRIVATE = 10\n.config TRIAL = 1", WholeProgramReason.SettingsDeclared, 8),
            ("main.nt65", "TRIAL = 1", "TRIAL = 2", WholeProgramReason.SettingsDeclared, 8),                // any file's conditions may read it
        ];

        var replay = new ProgramReplay();
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
    /// A change to something decided for the program as a whole — the files in it, the project,
    /// the CPU — makes the whole program be analyzed again, and the analysis reports which it was.
    /// </summary>
    [Fact]
    public void WhatIsDecidedForTheWholeProgramIsAnalyzedAgainWithAReason()
    {
        var main = SyntaxTree.Parse("main.nt65", ".module main\n.cpu 6502\n.segment CODE\n.proc main {\n    rts\n}\n");
        var other = SyntaxTree.Parse("other.nt65", ".module other\nSPARE = 1\n");
        var project = ProjectSettings.None;

        var first = Compiler.Analyze([main], project, Nothing);
        Assert.Equal(WholeProgramReason.NoPreviousAnalysis, first.WholeProgram);

        // Nothing changed at all, so the analysis before it is the answer.
        Assert.Same(first, Compiler.Analyze([main], project, Nothing, first));

        Assert.Equal(
            WholeProgramReason.ProjectChanged,
            Compiler.Analyze([main], project with { Out = "elsewhere" }, Nothing, first).WholeProgram);
        Assert.Equal(
            WholeProgramReason.FilesAddedOrRemoved,
            Compiler.Analyze([main, other], project, Nothing, first).WholeProgram);

        var native = main.WithChange(new TextChange(main.Text.IndexOf("6502", StringComparison.Ordinal), 4, "65816"));
        Assert.Equal(
            WholeProgramReason.CpuChanged,
            Compiler.Analyze([native], project, Nothing, first).WholeProgram);
    }

    /// <summary>
    /// Changing only which registers a routine keeps is a change to its signature, so a file
    /// that calls the routine is analyzed again. Here the caller promises to keep X and relies
    /// on the routine it calls to keep it too.
    /// </summary>
    [Fact]
    public void ChangingOnlyWhatARoutineKeepsReachesItsCallers()
    {
        var lib = SyntaxTree.Parse("lib.nt65", ".module lib\n.export .proc helper = $fff0: keeps x\n");
        var main = SyntaxTree.Parse("main.nt65",
            ".module main\n.use lib::helper\n.segment CODE\n.export .proc main: keeps x {\n    jsr helper\n    rts\n}\n");
        var project = ProjectSettings.None;

        var first = Compiler.Analyze([lib, main], project, Nothing);
        Assert.Empty(first.Diagnostics);

        var edited = lib.WithChange(new TextChange(lib.Text.IndexOf("keeps x", StringComparison.Ordinal), 7, "keeps a"));
        var incremental = Compiler.Analyze([edited, main], project, Nothing, first);
        var scratch = Compiler.Analyze([edited, main], project, Nothing);

        Assert.NotEmpty(scratch.Diagnostics);
        Assert.Equal(scratch.Problems(), incremental.Problems());
    }

    /// <summary>
    /// Returns no length for any path, because no file of these programs has an <c>.incbin</c> in
    /// it.
    /// </summary>
    private static long? Nothing(string path) => null;

    /// <summary>An <c>.incbin</c> file that changed on disk is a change to the files that include it.</summary>
    [Fact]
    public void AnIncbinThatChangedIsAnalyzedAgain()
    {
        var replay = new ProgramReplay();
        Assert.Equal(1, replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("dex", StringComparison.Ordinal), 3, "inx").Reanalyzed);
        replay.Length = 32;
        Assert.Equal(WholeProgramReason.BinaryFileChanged,
            replay.Change("main.nt65", replay.Text("main.nt65").IndexOf("inx", StringComparison.Ordinal), 3, "dex").WholeProgram);
    }
}
