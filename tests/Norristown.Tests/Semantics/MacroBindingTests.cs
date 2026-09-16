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
            .macro set16(dest: operand, value) {
                lda #<value
                sta dest
            }
            """);

        Assert.Empty(model.Problems());
        var macro = model.Symbol("set16");
        Assert.Equal(SymbolKind.Macro, macro.Kind);
        Assert.Equal(["dest", "value"], macro.Parameters.Select(p => p.Name));

        // A parameter with no kind written takes an expression, which is what accepts most.
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
            .macro m(n: const, t: ident, c: one(eq, ne), r: list(one(x, y)), then: block, el: block = {}) {
                then
            }
            """);

        Assert.Empty(model.Problems());
        var parameters = model.Symbol("m").Parameters;
        Assert.Equal(
            ["const", "ident", "one(eq, ne)", "list(one(x, y))", "block", "block"],
            parameters.Select(p => p.Accepts.ToString()));

        // Only the last may be left out, and what it stands for then is a block with nothing in it.
        Assert.Equal([false, false, false, true, false, true], parameters.Select(p => p.IsOptional));
        Assert.True(parameters[5].Empty);
    }

    /// <summary>A default is written in the header, so it means what it means there.</summary>
    [Fact]
    public void ADefaultResolvesWhereTheMacroIsDeclared()
    {
        var model = Analysis.Model("""
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
            .macro m() {
            LOCAL = 1
            }

                .res m::LOCAL
            """);

        Assert.Equal(["5: `m` is a macro, not a scope"], model.Problems());
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
        var model = Analysis.Model(".macro m() {\n" + item + "\n}\n");

        Assert.Contains(model.Problems(), problem => problem.StartsWith("2: " + message, StringComparison.Ordinal));
    }

    /// <summary>
    /// A macro declared in a proc would see that proc's cheap locals, and an expansion
    /// elsewhere would branch into them, out of sight of the proc's flow analysis.
    /// </summary>
    [Fact]
    public void AMacroBelongsOutsideAnyRoutine()
    {
        var model = Analysis.Model(".proc p {\n.macro m() {\n}\n}\n");

        Assert.Equal(["2: a `.macro` belongs at file level or in a `.scope`, not inside a routine"],
            model.Problems());
    }

    /// <summary>A macro in a <c>.scope</c> outside any routine is where one belongs.</summary>
    [Fact]
    public void AMacroMayBeDeclaredInAScope()
    {
        var model = Analysis.Model(".scope gfx {\n.macro m() {\n    nop\n}\n}\n");

        Assert.Empty(model.Problems());
        Assert.Equal("gfx::m", model.Symbol("m").QualifiedName);
    }

    /// <summary>A body that declared what an <c>ident</c> parameter names would name it in the caller.</summary>
    [Fact]
    public void AnIdentParameterCannotBeDeclaredInTheBody()
    {
        var model = Analysis.Model(".macro m(target: ident) {\ntarget:\n    nop\n}\n");

        Assert.Equal(["2: `target` is an `ident` parameter, and a body may not declare the name it stands for"],
            model.Problems());
    }

    /// <summary>A <c>list</c> takes every remaining argument, so nothing positional may follow it.</summary>
    [Fact]
    public void AListParameterComesLast()
    {
        var model = Analysis.Model(".macro m(regs: list(expr), n) {\n    nop\n}\n");

        Assert.Equal(["1: `n` comes after the `list` parameter `regs`, which takes every remaining argument"],
            model.Problems());
    }

    /// <summary>A block is written after the parentheses, so nothing in them may follow one.</summary>
    [Fact]
    public void ABlockParameterComesAfterTheOthers()
    {
        var model = Analysis.Model(".macro m(body: block, n) {\n    body\n}\n");

        Assert.Equal(
            ["1: `n` comes after the `block` parameter `body`, and a block is written after the parentheses"],
            model.Problems());
    }

    /// <summary>A name on its own splices a block, and only a block parameter is one.</summary>
    [Fact]
    public void ASpliceNamesABlockParameter()
    {
        var model = Analysis.Model(".macro m(n: const, body: block) {\n    body\n    n\n}\n");

        Assert.Equal(
            ["3: `n` is a macro parameter; a name written on its own splices a `block` parameter, "
                + "and nothing else belongs on a line alone"],
            model.Problems());
    }

    /// <summary>Outside a macro body a name alone is simply a line that reads as nothing.</summary>
    [Fact]
    public void ANameAloneOutsideAMacroIsNotALine()
    {
        var model = Analysis.Model(".proc p {\n    rts\n    body\n}\n");

        Assert.Equal(["3: expected a label, a constant, an instruction or a directive"], model.Problems());
    }
}
