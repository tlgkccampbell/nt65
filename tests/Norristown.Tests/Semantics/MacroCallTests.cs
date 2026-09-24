namespace Norristown.Tests.Semantics;

/// <summary>
/// Checks what a call is checked for. Each argument is checked against its parameter's kind, the
/// arguments against the parameters as a whole, and the macro against calling itself, directly
/// or not.
/// </summary>
public sealed class MacroCallTests
{
    private const string Set16 = """
        .module main
        .macro set16(dest: operand, value) {
            lda #<value
            sta dest
            lda #>value
            sta dest+1
        }

        """;

    [Fact]
    public void ACallNamesTheMacroItExpands()
    {
        var model = Analysis.Model(Set16 + """
            ptr = $10
            SCREEN = $0400

            .proc main {
                set16!(ptr, SCREEN)
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("set16"), model.SymbolAt("set16", 2));
        Assert.Same(model.Symbol("SCREEN"), model.SymbolAt("SCREEN", 2));
    }

    /// <summary>An operand is braced at the call unless it is a plain address.</summary>
    [Fact]
    public void ABracedArgumentIsAWholeOperand()
    {
        var model = Analysis.Model(Set16 + """
            buf = $0200

            .proc main {
                set16!({buf,x}, $1234)
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("buf"), model.SymbolAt("buf", 2));
    }

    /// <summary>
    /// An unbraced <c>(ptr)</c> is the expression <c>ptr</c> everywhere else, and reads as
    /// indirect addressing besides, so passing one as an operand can only be a mistake.
    /// </summary>
    [Fact]
    public void AnUnbracedParenthesizedArgumentIsNotAnOperand()
    {
        var model = Analysis.Model(Set16 + "ptr = $10\n\n    set16!((ptr), 0)\n");

        Assert.Equal(
            ["10: `dest` takes an operand, and `(ptr)` is read as an expression in parentheses: "
                + "put it in braces to pass indirect addressing"],
            model.Problems());
    }

    [Fact]
    public void ANamedArgumentBindsThatParameter()
    {
        var model = Analysis.Model("""
            .module main
            C4 = 60

            .macro note(pitch: const, frames: const = 1) {
                .byte pitch, frames
            }

            .segment RODATA
            .data tune {
                note!(C4, frames = 8)
                note!(C4)
            }
            """);

        Assert.Empty(model.Problems());
    }

    [Theory]
    [InlineData("note!(C4, 8, 9)", "`note` takes 1 to 2 arguments, and this call gives more")]
    [InlineData("note!()", "`note` needs an argument for `pitch`")]
    [InlineData("note!(C4, tempo = 1)", "`note` has no parameter called `tempo`")]
    [InlineData("note!(C4, pitch = 1)", "parameter `pitch` is given twice")]
    [InlineData("note!(pitch = 1, C4)", "a positional argument cannot follow a named one")]
    public void ACallIsCheckedAgainstTheParameters(string call, string message)
    {
        var model = Analysis.Model("""
            .module main
            C4 = 60

            .macro note(pitch: const, frames: const = 1) {
                .byte pitch, frames
            }

            .segment RODATA
            """ + "\n" + call + "\n}\n");

        Assert.Equal([$"9: {message}"], model.Problems());
    }

    /// <summary>
    /// A <c>one</c> argument is a word checked against the list and never looked up as a name, so
    /// a register is fine.
    /// </summary>
    [Fact]
    public void AOneArgumentIsAWordRatherThanAName()
    {
        var model = Analysis.Model("""
            .module main
            .macro push(regs: list(one(a, x, y))) {
                nop
            }

            .proc main {
                push!(a, x, y)
                rts
            }
            """);

        Assert.Empty(model.Problems());
    }

    [Fact]
    public void AWordOutsideTheListIsReported()
    {
        var model = Analysis.Model("""
            .module main
            .macro branch_unless(c: one(eq, ne)) {
                nop
            }

            .proc main {
                branch_unless!(cs)
                rts
            }
            """);

        Assert.Equal(["7: `c` takes one of `eq`, `ne`, and this is `cs`"], model.Problems());
    }

    [Fact]
    public void OnlyAMacroIsCalledWithABang()
    {
        var model = Analysis.Model(".module main\nSIZE = 1\n\n.proc main {\n    SIZE!(1)\n    rts\n}\n");

        Assert.Equal(["5: `SIZE` is a constant, not a macro: only a macro is called with `!`"], model.Problems());
    }

    [Fact]
    public void ATrailingBlockBindsTheBlockParameter()
    {
        var model = Analysis.Model("""
            .module main
            ptr = $10

            .macro times_x(count, body: block) {
                ldx #count
            @loop:
                body
                dex
                bne @loop
            }

            .proc main {
                ldy #0
                times_x!(8) {
                    sta (ptr),y
                    iny
                }
                rts
            }
            """);

        Assert.Empty(model.Problems());

        // The block is the caller's code: `ptr` in it is the caller's `ptr`.
        Assert.Same(model.Symbol("ptr"), model.SymbolAt("ptr", 2));
    }

    /// <summary>After the first block, each further block names the parameter it is for.</summary>
    [Fact]
    public void ASecondBlockIsNamedByItsContinuation()
    {
        var model = Analysis.Model("""
            .module main
            .macro wrap(then: block, otherwise: block = {}) {
                then
                otherwise
            }

            .proc main {
                wrap!() {
                    nop
                } otherwise {
                    inx
                }
                rts
            }
            """);

        Assert.Empty(model.Problems());
    }

    [Fact]
    public void ABlockNamingNoParameterIsReported()
    {
        var model = Analysis.Model("""
            .module main
            .macro wrap(then: block) {
                then
            }

            .proc main {
                wrap!() {
                    nop
                } otherwise {
                    inx
                }
                rts
            }
            """);

        Assert.Equal(["9: `wrap` has no `block` parameter called `otherwise`"], model.Problems());
    }

    /// <summary>
    /// A block may be spliced in more than one place, so anything it declared would be
    /// declared once per splice. A cheap local is private to each of them and is allowed.
    /// </summary>
    [Fact]
    public void ABlockArgumentMayDeclareOnlyCheapLocals()
    {
        var model = Analysis.Model("""
            .module main
            .macro wrap(body: block) {
                body
            }

            .proc main {
                wrap!() {
            HERE = 1
            @spin:
                    bne @spin
                }
                rts
            }
            """);

        Assert.Equal(
            ["8: `HERE` is declared in a block argument, which may declare only cheap locals: "
                + "the macro may insert the block more than once"],
            model.Problems());
    }

    [Fact]
    public void AMacroThatCallsItselfIsReported()
    {
        var model = Analysis.Model(".module main\n.macro m(n) {\n    m!(n)\n}\n");

        Assert.Equal(["3: `m` calls itself: a macro cannot be recursive"], model.Problems());
    }

    [Fact]
    public void AMacroThatReachesItselfThroughAnotherIsReported()
    {
        var model = Analysis.Model("""
            .module main
            .macro ping() {
                pong!()
            }

            .macro pong() {
                ping!()
            }
            """);

        Assert.Equal(["7: `ping` calls itself through `pong`: a macro cannot be recursive"],
            model.Problems());
    }

    /// <summary>
    /// Calling a macro that reaches itself reports the cycle once and writes it out no deeper
    /// than itself, rather than expanding until the stack runs out.
    /// </summary>
    [Fact]
    public void CallingAMacroThatReachesItselfIsReportedOnce()
    {
        var analysis = Analysis.Program(("main.nt65", """
            .module main
            .macro ping() {
                pong!()
            }

            .macro pong() {
                ping!()
            }

            .segment CODE
            .proc main {
                ping!()
                rts
            }
            """));

        Assert.Equal(["main.nt65:7: `ping` calls itself through `pong`: a macro cannot be recursive"],
            analysis.Problems());
    }

    /// <summary>
    /// Only a braced operand carries its own index: an unbraced argument is an expression, and a
    /// name in it that reads as a register, such as <c>s</c>, is still only a name. So the name
    /// is the one error, and <c>dest+1</c> is not refused for having no next byte.
    /// </summary>
    [Fact]
    public void AnUnbracedArgumentIsIndexedByNothing()
    {
        var program = Analysis.Program(("main.nt65", Set16 + "\n.segment CODE\n.proc main {\n    set16!(s, 0)\n    rts\n}\n"));

        Assert.Equal(["main.nt65:11: `s` is a register name and cannot be used as a name"], program.Problems());
    }

    /// <summary>Two calls to the same macro are not a cycle, no matter how many there are.</summary>
    [Fact]
    public void CallingTheSameMacroTwiceIsNotRecursion()
    {
        var model = Analysis.Model("""
            .module main
            .macro leaf() {
                nop
            }

            .macro twice() {
                leaf!()
                leaf!()
            }

            .proc main {
                twice!()
                rts
            }
            """);

        Assert.Empty(model.Problems());
    }
}
