using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>The scoping rules, one test per rule.</summary>
public sealed class BindingTests
{
    [Fact]
    public void LookupRunsFromTheInnermostScopeOutward()
    {
        var model = Analysis.Model("""
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
    public void ScopedNamesWalkIntoAScopeAndDoubleColonStartsAtTheFile()
    {
        var model = Analysis.Model("""
            .scope gfx {
                .proc init {
                    rts
                }
            }
            .proc init {
                jsr gfx::init
                jsr ::init
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
            .proc draw {
                .rodata {
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
            SIZE = 1
            SIZE = 2
            .proc p {
            @a:
            @a:
                rts
            }
            """);

        Assert.Equal(
            ["2: `SIZE` is already declared in this scope", "5: `@a` is already declared in this scope"],
            model.Problems());
    }

    /// <summary>A duplicate points at the declaration that got there first.</summary>
    [Fact]
    public void ADuplicateNamesTheFirstDeclaration()
    {
        var model = Analysis.Model("SIZE = 1\nSIZE = 2\n");

        var related = Assert.Single(Assert.Single(model.Diagnostics).Related);
        Assert.Equal(1, related.Span.Line);
        Assert.Equal("declared here", related.Message);
    }

    [Fact]
    public void ReservedWordsCannotBeNames()
    {
        var model = Analysis.Model("lda = 5\nX: .byte 0\njeq = 1\n");

        Assert.Equal(
            [
                "1: `lda` is a mnemonic and cannot be used as a name",
                "2: `X` is a register name and cannot be used as a name",
                "3: `jeq` is a mnemonic and cannot be used as a name",
            ],
            model.Problems());
    }

    [Fact]
    public void ACheapLocalNeedsAnEnclosingProcOrScope()
    {
        var model = Analysis.Model("@stray:\n");

        Assert.Equal(["1: `@stray` is a cheap local, which needs an enclosing `.proc` or `.scope`"],
            model.Problems());
    }

    [Fact]
    public void ACheapLocalCannotBeReachedWithAPath()
    {
        var model = Analysis.Model(".proc p {\n@loop:\n    lda ::@loop\n}\n");

        Assert.Equal(["3: `@loop` is a cheap local and cannot be reached with `::`"], model.Problems());
    }

    [Fact]
    public void AnUndeclaredNameIsReportedOncePerUse()
    {
        var model = Analysis.Model(".proc p {\n    jsr missing\n    jmp missing\n}\n");

        Assert.Equal(["2: `missing` is not declared", "3: `missing` is not declared"], model.Problems());
    }

    /// <summary>A path whose first part is unknown is one error, not one per part.</summary>
    [Fact]
    public void ABrokenPathIsReportedOnce()
    {
        var model = Analysis.Model(".proc p {\n    lda nowhere::inner::deeper\n}\n");

        Assert.Equal(["2: `nowhere` is not declared"], model.Problems());
    }

    [Fact]
    public void APathIntoSomethingThatIsNotAScopeSaysSo()
    {
        var model = Analysis.Model("SIZE = 1\n.proc p {\n    lda SIZE::inner\n}\n");

        Assert.Equal(["3: `SIZE` is a constant, not a scope"], model.Problems());
    }

    /// <summary>
    /// Constructs a later stage brings online declare and resolve nothing yet, so a macro
    /// body's names are not reported as the file's.
    /// </summary>
    [Fact]
    public void BlocksOfALaterStageAreNotBound()
    {
        var model = Analysis.Model("""
            .macro set16(dest, value) {
                lda #<value
                sta dest
            }
            .if DEBUG {
            DEBUG_ONLY = 1
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Empty(model.Symbols);
    }

    /// <summary>A segment block changes the segment of its contents, not their scope.</summary>
    [Fact]
    public void ASegmentBlockDoesNotStartAScope()
    {
        var model = Analysis.Model("""
            .proc draw {
                .rodata {
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
        var model = Analysis.Model(".scope gfx {\n.proc init {\nrts\n}\n}\n.proc main {\njsr gfx::init\n}\n");

        Assert.Equal(2, model.ReferencesTo(model.Symbol("gfx")).Count);
        Assert.Same(model.Symbol("init"), model.SymbolAt("init", occurrence: 2));
    }
}
