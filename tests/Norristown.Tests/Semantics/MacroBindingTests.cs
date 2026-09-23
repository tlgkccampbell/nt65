using Norristown.Semantics;

namespace Norristown.Tests.Semantics;

/// <summary>
/// What a macro definition declares, what its body may say, and what the two scopes around
/// an expansion — the body's own and the caller's — each hold.
/// </summary>
public sealed class MacroBindingTests
{
    [Fact]
    public void AMacroDeclaresItsNameAndItsParameters()
    {
        var model = Analysis.Model("""
            .module main
            .macro set16(dest: operand, value) {
                lda #<value
                sta dest
            }
            """);

        Assert.Empty(model.Problems());
        var macro = model.Symbol("set16");
        Assert.Equal(SymbolKind.Macro, macro.Kind);
        Assert.Equal(["dest", "value"], macro.Parameters.Select(p => p.Name));

        // A parameter with no kind written takes an expression, the kind that accepts the most.
        Assert.Equal(ParameterKind.Operand, macro.Parameters[0].Kind);
        Assert.Equal(ParameterKind.Expr, macro.Parameters[1].Kind);

        // The body's names are the parameters, resolved where they are written.
        Assert.Same(macro.Parameters[1].Symbol, model.SymbolAt("value", 2));
        Assert.Same(macro.Parameters[0].Symbol, model.SymbolAt("dest", 2));
    }

    [Fact]
    public void EveryKindOfParameterIsRead()
    {
        var model = Analysis.Model("""
            .module main
            .macro m(n: const, t: ident, c: one(eq, ne), r: list(one(x, y)), then: block, el: block = {}) {
                then
            }
            """);

        Assert.Empty(model.Problems());
        var parameters = model.Symbol("m").Parameters;
        Assert.Equal(
            ["const", "ident", "one(eq, ne)", "list(one(x, y))", "block", "block"],
            parameters.Select(p => p.Accepts.ToString()));

        // The `list` and the parameter with a default may be left out; that block then stands for
        // an empty one.
        Assert.Equal([false, false, false, true, false, true], parameters.Select(p => p.IsOptional));
        Assert.True(parameters[5].Empty);
    }

    /// <summary>A default is written in the header, so its names resolve where the macro is declared.</summary>
    [Fact]
    public void ADefaultResolvesWhereTheMacroIsDeclared()
    {
        var model = Analysis.Model("""
            .module main
            ONE = 1

            .macro note(pitch: const, frames: const = ONE) {
                .byte pitch, frames
            }
            """);

        Assert.Empty(model.Problems());
        Assert.Same(model.Symbol("ONE"), model.SymbolAt("ONE", 2));
    }

    /// <summary>
    /// A body sees the scope the macro is declared in, and what it declares stays there:
    /// nothing outside the body can name it, so no expansion declares a name in its caller.
    /// </summary>
    [Fact]
    public void ABodysOwnNamesAreItsAlone()
    {
        var model = Analysis.Model("""
            .module main
            SCREEN = $0400

            .macro times_x(count) {
                ldx #count
            @loop:
                sta SCREEN
                dex
                bne @loop
            }

            .proc draw {
            @loop:
                lda #0
                bne @loop
                rts
            }
            """);

        Assert.Empty(model.Problems());

        // Two `@loop`s, one in each scope, and neither is the other.
        Assert.NotSame(model.SymbolAt("@loop:"), model.SymbolAt("@loop:", 2));
        Assert.Equal(ScopeKind.Macro, model.SymbolAt("@loop:").Scope.Kind);
        Assert.Equal(ScopeKind.Proc, model.SymbolAt("@loop:", 2).Scope.Kind);

        // A body reads the scope it is written in, which is how it names a file's constants.
        Assert.Same(model.Symbol("SCREEN"), model.SymbolAt("SCREEN", 2));
    }

    /// <summary>Nothing outside a body can reach what it declares, so a path into one fails.</summary>
    [Fact]
    public void ABodysNamesAreNotReachableFromOutside()
    {
        var model = Analysis.Model("""
            .module main
            .macro m() {
            LOCAL = 1
            }

                .res m::LOCAL
            """);

        Assert.Equal(["6: `m` is a macro, not a scope"], model.Problems());
    }

    [Theory]
    [InlineData(".export x", "`.export` belongs outside a macro body")]
    [InlineData(".import x", "`.import` belongs outside a macro body")]
    [InlineData(".cpu 6502", "`.cpu` belongs outside a macro body")]
    [InlineData(".segment \"X\": zp", "a segment declaration belongs outside a macro body")]
    [InlineData(".proc p {\n}", "`.proc` belongs outside a macro body")]
    [InlineData(".proc p = $ffd2", "`.proc` belongs outside a macro body")]
    [InlineData(".macro n() {\n}", "`.macro` belongs outside a macro body")]
    [InlineData(".func f(a) = a", "`.func` belongs outside a macro body")]
    public void AMacroBodyRefusesTheItemsThatWouldReachItsCaller(string item, string message)
    {
        var model = Analysis.Model(".module main\n.macro m() {\n" + item + "\n}\n");

        Assert.Contains(model.Problems(), problem => problem.StartsWith("3: " + message, StringComparison.Ordinal));
    }

    /// <summary>
    /// A macro declared in a proc would see that proc's cheap locals, and an expansion
    /// elsewhere would branch into them, out of sight of the proc's flow analysis.
    /// </summary>
    [Fact]
    public void AMacroBelongsOutsideAnyRoutine()
    {
        var model = Analysis.Model(".module main\n.proc p {\n.macro m() {\n}\n}\n");

        Assert.Equal(["3: a `.macro` belongs at file level or in a `.scope`, not inside a routine"],
            model.Problems());
    }

    /// <summary>A macro may be declared in a <c>.scope</c> that is outside any routine.</summary>
    [Fact]
    public void AMacroMayBeDeclaredInAScope()
    {
        var model = Analysis.Model(".module main\n.scope gfx {\n.macro m() {\n    nop\n}\n}\n");

        Assert.Empty(model.Problems());
        Assert.Equal("gfx::m", model.Symbol("m").QualifiedName);
    }

    /// <summary>A body may not declare the name an <c>ident</c> parameter stands for, since that would declare it in the caller.</summary>
    [Fact]
    public void AnIdentParameterCannotBeDeclaredInTheBody()
    {
        var model = Analysis.Model(".module main\n.macro m(target: ident) {\ntarget:\n    nop\n}\n");

        Assert.Equal(["3: `target` is an `ident` parameter, and a body may not declare the name it stands for"],
            model.Problems());
    }

    /// <summary>A <c>list</c> takes every remaining argument, so nothing positional may follow it.</summary>
    [Fact]
    public void AListParameterComesLast()
    {
        var model = Analysis.Model(".module main\n.macro m(regs: list(expr), n) {\n    nop\n}\n");

        Assert.Equal(["2: `n` comes after the `list` parameter `regs`, which takes every remaining argument"],
            model.Problems());
    }

    /// <summary>A block is written after the parentheses, so nothing in them may follow one.</summary>
    [Fact]
    public void ABlockParameterComesAfterTheOthers()
    {
        var model = Analysis.Model(".module main\n.macro m(body: block, n) {\n    body\n}\n");

        Assert.Equal(
            ["2: `n` comes after the `block` parameter `body`, and a block is written after the parentheses"],
            model.Problems());
    }

    /// <summary>A name on a line of its own splices a block, so it must name a <c>block</c> parameter.</summary>
    [Fact]
    public void ASpliceNamesABlockParameter()
    {
        var model = Analysis.Model(".module main\n.macro m(n: const, body: block) {\n    body\n    n\n}\n");

        Assert.Equal(
            ["4: `n` is a macro parameter; a name written on its own splices a `block` parameter, "
                + "and nothing else belongs on a line alone"],
            model.Problems());
    }

    /// <summary>Outside a macro body, a name alone on a line is not a statement at all.</summary>
    [Fact]
    public void ANameAloneOutsideAMacroIsNotALine()
    {
        var model = Analysis.Model(".module main\n.proc p {\n    rts\n    body\n}\n");

        Assert.Equal(["4: expected a label, a constant, an instruction or a directive"], model.Problems());
    }
}
