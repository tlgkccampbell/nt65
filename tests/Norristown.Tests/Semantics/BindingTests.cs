using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>The scoping rules, one test per rule.</summary>
public sealed class BindingTests
{
    [Fact]
    public void LookupRunsFromTheInnermostScopeOutward()
    {
        var model = Analysis.Model("""
            .module main
            COUNT = 4
            .scope gfx {
                COUNT = 8
                .proc init {
                    lda #COUNT
                    rts
                }
            }
            .proc main {
                lda #COUNT
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(8, model.SymbolAt("COUNT", occurrence: 3).Value.Number);
        Assert.Equal(4, model.SymbolAt("COUNT", occurrence: 4).Value.Number);
    }

    [Fact]
    public void ScopedNamesWalkIntoAScopeAndDoubleColonStartsAtTheModules()
    {
        var model = Analysis.Model("""
            .module main
            .scope gfx {
                .proc init {
                    rts
                }
            }
            .proc init {
                jsr gfx::init
                jsr ::main::init
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(ScopeKind.Scope, model.SymbolAt("init", occurrence: 3).Scope.Kind);
        Assert.Equal(ScopeKind.File, model.SymbolAt("init", occurrence: 4).Scope.Kind);
    }

    /// <summary>A proc is a scope as well as a label, so its interior labels have a path.</summary>
    [Fact]
    public void AProcIsAScope()
    {
        var model = Analysis.Model("""
            .module main
            .proc draw {
                .segment RODATA {
                table:  .byte 1, 2
                }
                rts
            }
            .proc main {
                lda draw::table
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal("draw::table", model.Symbol("table").QualifiedName);
    }

    [Fact]
    public void CheapLocalsBelongToTheNearestProcOrScope()
    {
        var model = Analysis.Model("""
            .module main
            .proc reset {
                .scope {
                    bne @loop
                @loop:
                }
                .scope {
                @loop:
                    bne @loop
                }
                bne @done
            @done:
                rts
            }
            """);

        Assert.Empty(model.Problems());

        // Two `@loop`s, each in its own anonymous scope, and neither is the other.
        var first = model.SymbolAt("@loop", occurrence: 1);
        var second = model.SymbolAt("@loop", occurrence: 3);
        Assert.NotSame(first, second);
        Assert.All([first, second], local => Assert.Equal(ScopeKind.Scope, local.Scope.Kind));
        Assert.Equal(ScopeKind.Proc, model.Symbol("@done").Scope.Kind);
    }

    /// <summary>A nested scope can branch to its proc's cheap local, which is what the language asks for.</summary>
    [Fact]
    public void ACheapLocalIsFoundOutwardThroughScopes()
    {
        var model = Analysis.Model("""
            .module main
            .proc draw {
                .scope {
                    bne @done
                }
            @done:
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("@done"), model.SymbolAt("@done", occurrence: 1));
    }

    /// <summary>Cheap locals have a namespace of their own, so `@loop` never collides with `loop`.</summary>
    [Fact]
    public void ACheapLocalAndAPlainNameDoNotCollide()
    {
        var model = Analysis.Model("""
            .module main
            .proc draw {
            loop:
            @loop:
                jmp loop
                jmp @loop
            }
            """);

        Assert.Empty(model.Problems());
        Assert.NotSame(model.Symbol("loop"), model.Symbol("@loop"));
    }

    [Fact]
    public void ANameMayBeDeclaredOnceInItsScope()
    {
        var model = Analysis.Model("""
            .module main
            SIZE = 1
            SIZE = 2
            .proc p {
            @a:
            @a:
                rts
            }
            """);

        Assert.Equal(
            ["3: `SIZE` is already declared in this scope", "6: `@a` is already declared in this scope"],
            model.Problems());
    }

    /// <summary>A duplicate points at the declaration that got there first.</summary>
    [Fact]
    public void ADuplicateNamesTheFirstDeclaration()
    {
        var model = Analysis.Model(".module main\nSIZE = 1\nSIZE = 2\n");

        var related = Assert.Single(Assert.Single(model.Diagnostics).Related);
        Assert.Equal(2, related.Span.Line);
        Assert.Equal("declared here", related.Message);
    }

    /// <summary>
    /// A register is the one word a name may not be: <c>asl a</c> is a question about an
    /// operand, which position cannot answer. A mnemonic is a name, and only warned about.
    /// </summary>
    [Fact]
    public void ARegisterIsTheOneWordANameMayNotBe()
    {
        var model = Analysis.Model(".module main\nlda = 5\n.data X: .byte 0\njeq = 1\n");

        Assert.Equal(
            [
                "2: `lda` is an instruction on the 6502; as a name it is legal and easy to misread",
                "3: `X` is a register name and cannot be used as a name",
                "4: `jeq` is an instruction on the 6502; as a name it is legal and easy to misread",
            ],
            model.Problems());
    }

    [Fact]
    public void ACheapLocalNeedsAnEnclosingProcOrScope()
    {
        var model = Analysis.Model(".module main\n@stray:\n");

        Assert.Equal(["2: `@stray` is a cheap local, which needs an enclosing `.proc` or `.scope`"],
            model.Problems());
    }

    [Fact]
    public void ACheapLocalCannotBeReachedWithAPath()
    {
        var model = Analysis.Model(".module main\n.proc p {\n@loop:\n    lda ::@loop\n}\n");

        Assert.Equal(["4: `@loop` is a cheap local and cannot be reached with `::`"], model.Problems());
    }

    [Fact]
    public void AnUndeclaredNameIsReportedOncePerUse()
    {
        var model = Analysis.Model(".module main\n.proc p {\n    jsr missing\n    jmp missing\n}\n");

        Assert.Equal(["3: `missing` is not declared", "4: `missing` is not declared"], model.Problems());
    }

    /// <summary>A path whose first part is unknown is one error, not one per part.</summary>
    [Fact]
    public void ABrokenPathIsReportedOnce()
    {
        var model = Analysis.Model(".module main\n.proc p {\n    lda nowhere::inner::deeper\n}\n");

        Assert.Equal(["3: `nowhere` is not declared, and no module `nowhere` is in this build"], model.Problems());
    }

    [Fact]
    public void APathIntoSomethingThatIsNotAScopeSaysSo()
    {
        var model = Analysis.Model(".module main\nSIZE = 1\n.proc p {\n    lda SIZE::inner\n}\n");

        Assert.Equal(["4: `SIZE` is a constant, not a scope"], model.Problems());
    }

    /// <summary>A segment block changes the segment of its contents, not their scope.</summary>
    [Fact]
    public void ASegmentBlockDoesNotStartAScope()
    {
        var model = Analysis.Model("""
            .module main
            .proc draw {
                .segment RODATA {
            @table: .byte 1, 2
                }
                lda @table
                rts
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(ScopeKind.Proc, model.Symbol("@table").Scope.Kind);
        Assert.Equal("RODATA", model.Symbol("@table").Segment);
    }

    /// <summary>
    /// A path reaches only what every scope on the way out has a name for, so a cheap local
    /// and anything inside an anonymous <c>.scope</c> are named by themselves alone.
    /// </summary>
    [Fact]
    public void OnlyNamesWithAWayInAreQualified()
    {
        var model = Analysis.Model("""
            .module main
            .scope gfx {
                .proc init {
                @loop:
                    .scope {
                    hidden:
                    }
                    rts
                }
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal("gfx::init", model.Symbol("init").QualifiedName);
        Assert.Equal("@loop", model.Symbol("@loop").QualifiedName);
        Assert.Equal("hidden", model.Symbol("hidden").QualifiedName);
        Assert.False(model.Symbol("hidden").IsReachableByPath);
    }

    [Fact]
    public void EveryPartOfAPathIsAReferenceOfItsOwn()
    {
        var model = Analysis.Model(".module main\n.scope gfx {\n.proc init {\nrts\n}\n}\n.proc main {\njsr gfx::init\n}\n");

        Assert.Equal(2, model.ReferencesTo(model.Symbol("gfx")).Count);
        Assert.Same(model.Symbol("init"), model.SymbolAt("init", occurrence: 2));
    }

    /// <summary>
    /// A member a record gives a value names the member of its type, on one line or several, in
    /// an array's body and inside a record member, so a rename or a highlight finds it there too.
    /// </summary>
    [Fact]
    public void AMemberARecordGivesAValueIsAReferenceToIt()
    {
        var model = Analysis.Model("""
            .module main
            .struct Point {
                x: .byte
                y: .byte
            }
            .struct Box {
                corner: .type Point
                size:   .byte
            }
            .segment RODATA
            .data one: .type Point { x = 1, y = 2 }
            .data box: .type Box { corner = { x = 3, y = 4 }, size = 5 }
            .data many: .type Point[] {
                { x = 5, y = 6 }
            }
            .data long: .type Point {
                x = 7
                y = 8
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Equal(5, model.ReferencesTo(model.Symbol("x")).Count);
        Assert.Equal(2, model.ReferencesTo(model.Symbol("corner")).Count);
    }

    /// <summary>
    /// Every bank range of a <c>mirrors</c> names constants, not only the first: each of them
    /// is resolved where the segment is declared.
    /// </summary>
    [Fact]
    public void EveryBankRangeOfAMirrorsNamesConstants()
    {
        var model = Analysis.Model("""
            .module main
            FIRST  = $00
            MIRROR = $80
            .segment LORAM: abs, bank = $7e, mirrors = [FIRST..$3f, MIRROR..$bf]
            """);

        Assert.Empty(model.Problems());
        Assert.Equal([(0x00L, 0x3fL), (0x80L, 0xbfL)], model.Segments.Find("LORAM")!.Mirrors);
    }
}
