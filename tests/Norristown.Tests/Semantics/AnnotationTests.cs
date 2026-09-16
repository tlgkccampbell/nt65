namespace Norristown.Tests.Semantics;

/// <summary>
/// <c>.next</c> and <c>.patch</c>: what they may stand after, and what their names mean.
/// On the 6502 and the 65C02 nothing consumes processor state, so an annotation is
/// accepted and its names are checked, and none is required.
/// </summary>
public sealed class AnnotationTests
{
    [Fact]
    public void ANextNamesTheLabelsFlowReaches()
    {
        var model = Analysis.Model("""
            .proc dispatch {
                lda cmd
                jmp (@table)
                .next @move, @fire

            @table: .addr @move, @fire
            @move:  rts
            @fire:  rts
            }

            cmd: .byte 0
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.SymbolAt("@move:"), model.SymbolAt("@move", 1));
        Assert.Same(model.SymbolAt("@fire:"), model.SymbolAt("@fire", 1));
    }

    /// <summary>A target may be a scoped path, which resolves as any other path does.</summary>
    [Fact]
    public void ATargetMayBeAScopedPath()
    {
        var model = Analysis.Model("""
            .scope gfx {
            .proc init {
                rts
            }
            }

            .proc start {
                jmp (vector)
                .next gfx::init
            }

            vector: .addr gfx::init
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("init"), model.SymbolAt("init", 2));
    }

    /// <summary><c>.next ?</c> names nothing: the path ends there and nothing beyond it is checked.</summary>
    [Fact]
    public void NextQuestionNamesNothing()
    {
        var model = Analysis.Model(".proc jump {\n    jmp (ptr)\n    .next ?\n}\n\nptr: .addr 0\n");

        Assert.Empty(model.Problems());
    }

    [Fact]
    public void ATargetThatNamesNothingIsReported()
    {
        var model = Analysis.Model(".proc p {\n    jmp (ptr)\n    .next @gone\n}\n\nptr: .addr 0\n");

        Assert.Equal(["3: `@gone` is not declared"], model.Problems());
    }

    [Fact]
    public void APatchNamesTheInstructionWrittenInto()
    {
        var model = Analysis.Model("""
            .proc poke {
                lda #0
            @op:
                sta $0400
                sta @op+1
                .patch @op
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.SymbolAt("@op:"), model.SymbolAt("@op", 3));
    }

    /// <summary>
    /// An annotation is a claim about the statement above it. With nothing above, it claims
    /// nothing, which is worth saying rather than quietly ignoring.
    /// </summary>
    [Theory]
    [InlineData(".proc p {\n    .next @a\n@a: rts\n}\n", ".next")]
    [InlineData(".proc p {\n    rts\n@a:\n    .patch @a\n}\n", ".patch")]
    public void AnAnnotationWithNoStatementAboveIsReported(string text, string directive)
    {
        var model = Analysis.Model(text);

        Assert.Contains($"`{directive}` is about the statement above it, and there is none here",
            string.Join("\n", model.Problems()), StringComparison.Ordinal);
    }

    /// <summary>A blank line between the statement and its annotation does not separate them.</summary>
    [Fact]
    public void ABlankLineDoesNotSeparateAnAnnotationFromItsStatement()
    {
        var model = Analysis.Model(".proc p {\n    jmp (ptr)\n\n    .next ?\n}\n\nptr: .addr 0\n");

        Assert.Empty(model.Problems());
    }

    /// <summary>Inside a macro body a target may be an <c>ident</c> parameter, as any name may.</summary>
    [Fact]
    public void ATargetMayBeAnIdentParameter()
    {
        var model = Analysis.Model("""
            .macro go(target: ident) {
                jmp (ptr)
                .next target
            }

            .proc p {
                go!(@out)
            @out:
                rts
            }

            ptr: .addr 0
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("go").Parameters[0].Symbol, model.SymbolAt("target", 2));
    }

    /// <summary>Neither annotation reaches ca65: both exist only for the analysis.</summary>
    [Fact]
    public void NeitherAnnotationIsWritten()
    {
        var written = Analysis.Outputs(("main.nt65", """
            .proc p {
            @op:
                sta $0400
                sta @op+1
                .patch @op
                jmp (ptr)
                .next ?
            }

            ptr: .addr 0
            """))["main.s"];

        Assert.DoesNotContain(".next", written, StringComparison.Ordinal);
        Assert.DoesNotContain(".patch", written, StringComparison.Ordinal);
    }
}
