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
            ("errors.nt65", "jsr gfx::draw", "jsr gfx::clear", null, 1),
            ("main.nt65", ".if DEBUG {", ".if !DEBUG {", null, 1),                                   // `TRACE` is gone, and nobody looked it up
            ("main.nt65", "BASE = 4", "BASE = 4 ; four", null, 1),                                   // what it means is the same
            ("defs.nt65", "SCALE  = 3", "SCALE  = 4", null, 2),                                      // gfx calls `scaled`, whose body names it
            ("defs.nt65", "FILL   = $20", "FILL   = $2e", null, 2),                                  // main expands `fill_screen`, whose body names it
            ("gfx.nt65", "    lda #0\n", "    lda #0 ; clear\n", null, 1),                           // defs' `ping` goes on holding the old `COLORS`
            ("main.nt65", "sta cursor+1", "sta cursor", null, 1),                                    // which main's expansion of `ping` reaches
            ("defs.nt65", "y:      .word\n", "y:      .word\nz:      .word\n", null, 4),             // types lays `Line` out from it, main uses `Line`, and gfx brings in `ping`, which moves
            ("defs.nt65", "z:      .word", "w:      .word", null, 3),                                // the same size, and main writes the names out
            ("gfx.nt65", "    nop\n", "    ping!()\n", null, 3),                                     // two macros in two files now call each other
            ("defs.nt65", "relay!(1)", "relay!(2)", null, 3),                                        // still
            ("defs.nt65", "; Calls a macro", "; It calls a macro", null, 1),                         // gfx's `relay` goes on holding the old `ping`
            ("defs.nt65", "; It calls", "; Here.\n; It calls", null, 3),                             // `ping` moves, and callers write its calls' lines
            ("gfx.nt65", "    ping!()\n", "    nop\n", null, 3),
            ("defs.nt65", "std = a8, i8", "std = a16, i8", null, 3),                                 // gfx's `clear` takes the set, and errors calls `clear`
            ("defs.nt65", "std = a16, i8", "std = a8, i8", null, 3),
            // A collision under one linker name is reported on the file that sorts later, which
            // is not the file that changed: that file is read again all the same.
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
    /// What is decided for the program as a whole changing — the files in it, the project, the
    /// CPU — is a reason to analyze all of it, and each says which it was.
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

    /// <summary>No file of these programs has an <c>.incbin</c> in it.</summary>
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
