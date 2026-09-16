using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// The declarative constructs: enumerations, structures, unions, lists, character mappings
/// and functions. They are read and their names are bound; what the output writes for them
/// is not built yet, so a file that uses one is refused rather than written out short.
/// </summary>
public sealed class TypeSyntaxTests
{
    private const string Source = """
        .enum Color {
            red
            green = 5
            blue
        }

        .struct Point {
        x:      .word
        y:      .word
        }

        .union Value {
        b:      .byte
        w:      .word
        }

        .list handlers {
            first
            second
        }

        .charmap screen {
            'A'..'Z' = $01
            ' '      = $20
        }

        .func rgb15(r, g, b) = r | (g << 5) | (b << 10)

        first  = 1
        second = 2
        """;

    /// <summary>Each construct declares a symbol of its own kind, with its members inside it.</summary>
    [Fact]
    public void EachConstructDeclaresWhatItIs()
    {
        var model = Analysis.Model(Source);

        Assert.Equal(SymbolKind.Enum, model.Symbol("Color").Kind);
        Assert.Equal(SymbolKind.Struct, model.Symbol("Point").Kind);
        Assert.Equal(SymbolKind.Union, model.Symbol("Value").Kind);
        Assert.Equal(SymbolKind.List, model.Symbol("handlers").Kind);
        Assert.Equal(SymbolKind.Charmap, model.Symbol("screen").Kind);
        Assert.Equal(SymbolKind.Func, model.Symbol("rgb15").Kind);
        Assert.Empty(model.Problems());
    }

    /// <summary>A member is named through the type that holds it, and nowhere else.</summary>
    [Fact]
    public void MembersLiveInTheirType()
    {
        var model = Analysis.Model(Source);

        Assert.Equal("Color::green", model.Symbol("green").QualifiedName);
        Assert.Equal("Point::y", model.Symbol("y").QualifiedName);
        Assert.Equal(SymbolKind.Member, model.Symbol("x").Kind);
        Assert.Equal(SymbolKind.Constant, model.Symbol("red").Kind);

        // The output flattens the path, the way every other scoped name is flattened.
        Assert.Equal("Point__y", model.Symbol("y").FlatName);
    }

    /// <summary>A function keeps its parameters and its body; a list keeps its items.</summary>
    [Fact]
    public void AFunctionAndAListKeepWhatTheyAreMadeOf()
    {
        var model = Analysis.Model(Source);

        Assert.Equal(["r", "g", "b"], model.Symbol("rgb15").Parameters.Select(p => p.Text));
        Assert.Single(model.Symbol("rgb15").Items);
        Assert.Equal(2, model.Symbol("handlers").Items.Count);
        Assert.Equal(2, model.Symbol("screen").Entries.Count);
    }

    /// <summary>A label written on a <c>.tag</c> is an instance of that type, not a plain label.</summary>
    [Fact]
    public void ATagLabelIsAnInstance()
    {
        var model = Analysis.Model(".struct Point {\nx:      .word\n}\n\nhere:   .tag Point\nthere:  .res 2\n");

        Assert.Equal(SymbolKind.Instance, model.Symbol("here").Kind);
        Assert.Equal(SymbolKind.Label, model.Symbol("there").Kind);
        Assert.NotNull(model.Symbol("here").TypeExpression);
    }

    /// <summary>
    /// Nothing is written for any of them yet. A file that uses one produces no output and
    /// says so, rather than quietly leaving the construct out of the ca65.
    /// </summary>
    [Theory]
    [InlineData(".enum Color {\nred\n}\n", ".enum")]
    [InlineData(".struct Point {\nx:      .word\n}\n", ".struct")]
    [InlineData(".union Value {\nb:      .byte\n}\n", ".union")]
    [InlineData(".charmap screen {\n' ' = $20\n}\n", ".charmap")]
    [InlineData(".list items {\n1, 2\n}\n", ".list")]
    [InlineData(".func twice(n) = n * 2\n", ".func")]
    [InlineData("here:   .tag Point\n", ".tag")]
    [InlineData("    .align 256\n", ".align")]
    [InlineData("    .incbin \"sprites.bin\"\n", ".incbin")]
    [InlineData("    .lobytes 1, 2\n", ".lobytes")]
    [InlineData("    .hibytes 1, 2\n", ".hibytes")]
    public void NothingIsWrittenForThemYet(string source, string directive)
    {
        var compilation = Compiler.Compile([new SourceFile("main.nt65", source)]);

        Assert.Empty(compilation.Outputs);
        Assert.Contains(compilation.Diagnostics, d => d.Message == $"`{directive}` is not transpiled yet");
    }

    /// <summary>The design's own examples of each construct read without complaint.</summary>
    [Fact]
    public void TheDesignsExamplesParse()
    {
        var tree = SyntaxTree.Parse("main.nt65", Source);

        Assert.Empty(tree.Diagnostics);
    }
}
