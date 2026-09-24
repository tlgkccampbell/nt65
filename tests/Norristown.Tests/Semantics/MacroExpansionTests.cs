namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks what a call becomes. The body is written out with each parameter standing for what the
/// call gave it, the locals are renamed per expansion, and a comment names the call.
/// </summary>
public sealed class MacroExpansionTests
{
    [Fact]
    public void ACallBecomesItsBodyWithTheArgumentsInPlace()
    {
        Assert.Equal("""
            SCREEN = $0400
            ptr = $10

            .segment "CODE": absolute
            ; .proc main  main.nt65:13
            main:
                ; set16!(ptr, SCREEN)  main.nt65:14
                lda #<SCREEN
                sta z:ptr
                lda #>SCREEN
                sta z:ptr+1
                ; end of set16!
                rts
            ; end of main
            """, Body("""
            .macro set16(dest: operand, value) {
                lda #<value
                sta dest
                lda #>value
                sta dest+1
            }

            SCREEN = $0400
            ptr = $10

            .proc main {
                set16!(ptr, SCREEN)
                rts
            }
            """));
    }

    /// <summary>An operand argument is substituted as a whole operand, index included.</summary>
    [Fact]
    public void AnOperandArgumentKeepsItsIndex()
    {
        Assert.Contains("sta a:buf,x", Body("""
            .macro store(dest: operand) {
                sta dest
            }

            buf = $0200

            .proc main {
                store!({buf,x})
                rts
            }
            """));
    }

    /// <summary>
    /// An argument is substituted as a parenthesized whole, so <c>value * 2</c> with the
    /// argument <c>1 + 2</c> is 6, where ca65's textual substitution would give 5.
    /// </summary>
    [Fact]
    public void AnArgumentGoesInAsAParenthesizedWhole()
    {
        Assert.Contains(".byte (1 + 2) * 2", Body("""
            .macro twice(value) {
                .byte value * 2
            }

            .segment RODATA
            .data doubled {
                twice!(1 + 2)
            }
            """));
    }

    /// <summary>Each expansion's local labels are its own, and get distinct names in the output.</summary>
    [Fact]
    public void EachExpansionRenamesWhatItsBodyDeclares()
    {
        var written = Body("""
            .macro delay(count) {
                ldx #count
            @loop:
                dex
                bne @loop
            }

            .proc main {
                delay!(8)
                delay!(16)
                rts
            }
            """);

        Assert.Contains("delay__loop:", written);
        Assert.Contains("delay__loop_2:", written);
        Assert.Contains("bne delay__loop\n", written);
        Assert.Contains("bne delay__loop_2\n", written);
    }

    /// <summary>A block argument is spliced where the body names it, and is the caller's code.</summary>
    [Fact]
    public void ABlockArgumentIsSplicedWhereTheBodyNamesIt()
    {
        Assert.Equal("""
            ptr = $10

            .segment "CODE": absolute
            ; .proc main  main.nt65:13
            main:
                ldy #0
                ; times_x!(8)  main.nt65:15
                ldx #8
            times_x__loop:
                sta (ptr),y
                iny
                dex
                bne times_x__loop
                ; end of times_x!
                rts
            ; end of main
            """, Body("""
            .macro times_x(count, body: block) {
                ldx #count
            @loop:
                body
                dex
                bne @loop
            }

            ptr = $10

            .proc main {
                ldy #0
                times_x!(8) {
                    sta (ptr),y
                    iny
                }
                rts
            }
            """));
    }

    /// <summary>
    /// <c>.byteof</c> gives byte n of an operand. For an immediate it is a shift and a mask of the
    /// value, and for a mode that addresses memory it is the address plus n. One macro then serves
    /// constants and memory alike.
    /// </summary>
    [Fact]
    public void ByteofServesConstantsAndMemoryAlike()
    {
        var written = Body("""
            .macro mov16(dest: operand, src: operand) {
                lda .byteof(src, 0)
                sta dest
                lda .byteof(src, 1)
                sta dest+1
            }

            ptr = $10
            other = $12
            SCREEN = $0400

            .proc main {
                mov16!(ptr, {#SCREEN})
                mov16!(ptr, other)
                rts
            }
            """);

        Assert.Contains("lda #$00", written);
        Assert.Contains("lda #$04", written);
        Assert.Contains("lda z:other\n", written);
        Assert.Contains("lda z:other+1", written);
    }

    /// <summary>A condition in a body may test the arguments, and only one branch is written.</summary>
    [Fact]
    public void AConditionInABodyTestsTheArguments()
    {
        Assert.Equal("""
            .segment "CODE": absolute
            ; .proc main  main.nt65:17
            main:
                ; push!(a, x, y)  main.nt65:18
                pha
                phx
                phy
                ; end of push!
                rts
            ; end of main
            """, Body("""
            .cpu 65c02

            .macro push(regs: list(one(a, x, y))) {
                .each regs, r {
                    .if r == a {
                        pha
                    } .elseif r == x {
                        phx
                    } .else {
                        phy
                    }
                }
            }

            .proc main {
                push!(a, x, y)
                rts
            }
            """));
    }

    /// <summary><c>.empty</c> asks whether a block argument has anything in it.</summary>
    [Fact]
    public void EmptyAsksWhetherABlockHasStatements()
    {
        var written = Body("""
            .macro wrap(then: block, otherwise: block = {}) {
                then
                .if !.empty(otherwise) {
                    nop
                }
                otherwise
            }

            .proc main {
                wrap!() {
                    inx
                }
                wrap!() {
                    iny
                } otherwise {
                    dex
                }
                rts
            }
            """);

        // The first call gives no second block, so the `nop` between them is not written.
        Assert.Equal(1, written.Split('\n').Count(line => line.Trim() == "nop"));
    }

    /// <summary>A call that leaves a parameter out gets that parameter's default.</summary>
    [Fact]
    public void ADefaultFillsInWhatACallLeavesOut()
    {
        Assert.Contains(".byte C4, 1", Body("""
            C4 = 60

            .macro note(pitch: const, frames: const = 1) {
                .byte pitch, frames
            }

            .segment RODATA
            .data tune {
                note!(C4)
            }
            """));
    }

    /// <summary>A label on a call names what the expansion emits.</summary>
    [Fact]
    public void ALabelOnACallNamesWhatItEmits()
    {
        var written = Body("""
            .macro pair(a1, b1) {
                .byte a1, b1
            }

            .segment RODATA
            .data tune {
                pair!(1, 2)
            }
            """);

        Assert.Contains("tune:", written);
        Assert.Contains(".byte 1, 2", written);
    }

    /// <summary>
    /// Returns the ca65 a one-file program becomes, without the header lines every output starts
    /// with.
    /// A program that names no segment is given a <c>.segment CODE</c> line before the rest, and
    /// every program is given a <c>.module main</c> line before that.
    /// </summary>
    private static string Body(string text)
    {
        if (!text.Contains(".segment", StringComparison.Ordinal))
            text = ".segment CODE\n" + text;
        text = ".module main\n" + text;
        var outputs = Analysis.Outputs(("main.nt65", text));
        Assert.True(outputs.ContainsKey("main.s"),
            "the program did not transpile:\n" + string.Join(
                "\n", Analysis.Program(("main.nt65", text)).Problems()));
        return string.Join("\n", outputs["main.s"].Split('\n').SkipWhile(IsHeader)).Trim();
    }

    /// <summary>Returns whether a line is part of the header every output starts with.</summary>
    private static bool IsHeader(string line) =>
        line.Length == 0 || line.StartsWith(';') || line.StartsWith(".setcpu", StringComparison.Ordinal)
            || line.StartsWith(".smart", StringComparison.Ordinal) || line.StartsWith(".case", StringComparison.Ordinal)
            || line.StartsWith(".feature", StringComparison.Ordinal);
}
