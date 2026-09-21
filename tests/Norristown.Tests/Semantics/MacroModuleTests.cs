namespace Norristown.Tests.Semantics;

/// <summary>
/// A macro crosses a module boundary by being expanded in the module that calls it, not by
/// reaching ca65: nothing of it is in the object file.
/// </summary>
public sealed class MacroModuleTests
{
    private const string Library = """
        .module lib
        .export set16, SCREEN, table

        SCREEN = $0400

        .segment RODATA
        .data table:  .byte 1, 2

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
            .module main
            .use lib::set16
            ptr = $10

            .segment CODE
            .export .proc main {
                set16!(ptr, lib::SCREEN)
                rts
            }
            """));

        var written = outputs["main.s"];

        // The body is written into the caller, with the caller's own names.
        Assert.Contains("lda #<lib__SCREEN", written);
        Assert.Contains("sta z:ptr", written);
        Assert.Contains("sta z:ptr+1", written);

        // What the body uses and did not receive comes with it: a constant by value, as any
        // constant from another module is, and an address as an import.
        Assert.Contains("lib__SCREEN = $0400", written);
        Assert.Contains(".import lib__table", written);

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
            .module lib
            .export show

            PRIVATE = $10

            .macro show() {
                lda PRIVATE
            }
            """),
            ("main.nt65", ".module main\n.use lib::show\n.segment CODE\n.export .proc main {\n    show!()\n    rts\n}\n"));

        Assert.Equal(
            ["lib.nt65:6: `show!` is exported but names `PRIVATE`, which is not: a macro expands in the module "
                + "that calls it, and what it names there has to be exported"],
            program.Problems());
    }

    /// <summary>A macro another module declares but does not export cannot be brought in.</summary>
    [Fact]
    public void AMacroThatIsNotExportedCannotBeCalled()
    {
        var program = Analysis.Program(
            ("lib.nt65", ".module lib\n.macro hidden() {\n    nop\n}\n"),
            ("main.nt65", ".module main\n.use lib::hidden\n.segment CODE\n.export .proc main {\n    hidden!()\n    rts\n}\n"));

        Assert.Contains("main.nt65:2: `lib::hidden` is not exported by module `lib`", program.Problems());
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
            .module lib
            .export delay

            .macro delay(count) {
                ldx #count
            @loop:
                dex
                bne @loop
            }
            """),
            ("main.nt65", """
            .module main
            .use lib::delay
            .segment CODE
            .proc delay__loop {
                rts
            }

            .export .proc main {
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
