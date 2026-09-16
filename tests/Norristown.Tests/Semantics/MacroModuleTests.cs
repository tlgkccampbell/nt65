namespace Norristown.Tests.Semantics;

/// <summary>
/// A macro crosses a file boundary by being expanded in the file that calls it, not by
/// reaching ca65: nothing of it is in the object file (§12, §13).
/// </summary>
public sealed class MacroModuleTests
{
    private const string Library = """
        .export set16, SCREEN, table

        SCREEN = $0400

        .rodata {
        table:  .byte 1, 2
        }

        .macro set16(dest: operand, value) {
            lda #<value
            sta dest
            lda #>value
            sta dest+1
            lda table
        }
        """;

    [Fact]
    public void AnExportedMacroExpandsInTheFileThatCallsIt()
    {
        var outputs = Analysis.Outputs(
            ("lib.nt65", Library),
            ("main.nt65", """
            ptr = $10

            .proc main {
                set16!(ptr, SCREEN)
                rts
            }
            """));

        var written = outputs["main.s"];

        // The body is written into the caller, with the caller's own names.
        Assert.Contains("lda #<SCREEN", written);
        Assert.Contains("sta z:ptr", written);
        Assert.Contains("sta z:ptr+1", written);

        // What the body uses and did not receive comes with it: a constant by value, as any
        // cross-file constant does, and an address as an import.
        Assert.Contains("SCREEN = $0400", written);
        Assert.Contains(".import table", written);

        // The macro itself is no symbol to the linker, and the library writes nothing for it.
        Assert.DoesNotContain("set16", outputs["lib.s"]);
        Assert.DoesNotContain(".import set16", written);
    }

    /// <summary>
    /// An expansion lands in the file that calls it, so a name an exported macro uses without
    /// being given it has to be reachable from there too.
    /// </summary>
    [Fact]
    public void AnExportedMacroMayOnlyUseWhatIsExported()
    {
        var program = Analysis.Program(
            ("lib.nt65", """
            .export show

            PRIVATE = $10

            .macro show() {
                lda PRIVATE
            }
            """),
            ("main.nt65", ".proc main {\n    show!()\n    rts\n}\n"));

        Assert.Contains(program.Problems(), problem => problem.StartsWith(
            "lib.nt65:6: `show` is exported and uses `PRIVATE`, which is not.",
            StringComparison.Ordinal));
    }

    /// <summary>A macro another file declares but does not export cannot be called.</summary>
    [Fact]
    public void AMacroThatIsNotExportedCannotBeCalled()
    {
        var program = Analysis.Program(
            ("lib.nt65", ".macro hidden() {\n    nop\n}\n"),
            ("main.nt65", ".proc main {\n    hidden!()\n    rts\n}\n"));

        Assert.Contains(program.Problems(), problem =>
            problem.StartsWith("main.nt65:2: `hidden` is declared in `lib.nt65` and is not exported",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// A body's own labels are named after the macro, and an importing file that has a name
    /// of its own in the way gets a different one rather than a collision.
    /// </summary>
    [Fact]
    public void ExpansionLabelsGiveWayToTheFilesOwnNames()
    {
        var outputs = Analysis.Outputs(
            ("lib.nt65", """
            .export delay

            .macro delay(count) {
                ldx #count
            @loop:
                dex
                bne @loop
            }
            """),
            ("main.nt65", """
            .proc delay__loop {
                rts
            }

            .proc main {
                delay!(4)
                rts
            }
            """));

        var written = outputs["main.s"];
        Assert.Contains("delay__loop:", written);
        Assert.Contains("delay__loop_2:", written);
        Assert.Contains("bne delay__loop_2", written);
    }
}
