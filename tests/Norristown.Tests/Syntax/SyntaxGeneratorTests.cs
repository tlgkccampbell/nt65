using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

/// <summary>
/// What the generator writes for a node, from a table of its own rather than the repository's.
/// A kind is converted by one word in the table, and these are the two things that word changes:
/// the type of every property of the kind, and whether it searches for its pieces or reads its
/// slots.
/// </summary>
public sealed class SyntaxGeneratorTests
{
    private const string Widget = """
        node WidgetSyntax : StatementSyntax
          kind Widget
          summary A widget.
          slot Keyword : SyntaxToken
            summary The word.
            kinds Directive
            read ChildTokens[0]
          slot Name : SyntaxToken
            summary The name.
            kinds Identifier
            today SyntaxToken?
            read NameAt(1)
          slot Parts : SeparatedSyntaxList<WidgetSyntax>
            summary The parts.
            today ImmutableArray<WidgetSyntax>
            nodes
          slot CloseBraceToken : SyntaxToken?
            summary The <c>}</c>, or null.
            kinds CloseBrace
            read FirstToken(SyntaxKind.CloseBrace)
        """;

    [Fact]
    public void AnUnconvertedKindSearchesForItsPiecesAndReadsASlotWhenItHasOne()
    {
        var red = Red(Widget);
        Assert.Contains("public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);", red);
        Assert.Contains("public SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotToken(1);", red);
        Assert.Contains("public ImmutableArray<WidgetSyntax> Parts => Nodes(ref parts);", red);
        Assert.Contains("private ImmutableArray<WidgetSyntax> parts;", red);
        Assert.Contains("public SyntaxToken? CloseBraceToken =>", red);
        Assert.Contains("Green is GreenSyntax ? FirstToken(SyntaxKind.CloseBrace) : SlotTokenOrNull(3);", red);
    }

    [Fact]
    public void AConvertedKindReadsItsSlotsAndItsRequiredPiecesAreNotNullable()
    {
        var red = Red(Widget + "\n  converted\n");
        Assert.Contains("public SyntaxToken Keyword => SlotToken(0);", red);
        Assert.Contains("public SyntaxToken Name => SlotToken(1);", red);
        Assert.Contains("public SeparatedSyntaxList<WidgetSyntax> Parts => SlotSeparatedList<WidgetSyntax>(2);", red);
        Assert.Contains("public SyntaxToken? CloseBraceToken => SlotTokenOrNull(3);", red);

        // Nothing is kept in a field any more, so the array and its using go with the search.
        Assert.DoesNotContain("ImmutableArray", red);
        Assert.DoesNotContain("Green is GreenSyntax", red);
    }

    /// <summary>The green class is the target shape whether the kind is converted or not.</summary>
    [Fact]
    public void TheGreenClassTakesOneParameterPerSlot()
    {
        foreach (var green in new[] { Green(Widget), Green(Widget + "\n  converted\n") })
        {
            Assert.Contains("internal sealed class WidgetSyntax : GreenNode", green);
            Assert.Contains("GreenToken keyword,", green);
            Assert.Contains("GreenToken name,", green);
            Assert.Contains("GreenSeparatedList? parts,", green);
            Assert.Contains("GreenToken? closeBraceToken)", green);
            Assert.Contains(
                ": base(SyntaxKind.Widget, keyword.FullWidth + name.FullWidth + (parts?.FullWidth ?? 0) "
                + "+ (closeBraceToken?.FullWidth ?? 0))",
                green);
            Assert.Contains("public override int SlotCount => 4;", green);
            Assert.Contains("2 => this.parts,", green);
            Assert.Contains("new Red.WidgetSyntax(tree, parent, this, position);", green);
        }
    }

    /// <summary>
    /// A node slot takes a bare green node until every kind it may hold builds its own, so a kind
    /// can have its slots fixed without waiting for whatever it holds.
    /// </summary>
    [Fact]
    public void ANodeSlotTakesItsOwnGreenClassOnceWhatItHoldsBuildsOne()
    {
        const string holder = """
            node LidSyntax : SyntaxNode
              kind Lid
              summary A lid.
              slot Keyword : SyntaxToken
                summary The word.
                kinds Directive
                read ChildTokens[0]
            node BoxSyntax : SyntaxNode
              kind Box
              summary A box.
              slot Lid : LidSyntax?
                summary The lid, or null.
                read FirstNode<LidSyntax>()
            """;
        Assert.Contains("GreenNode? lid)", Box(holder));
        Assert.Contains("LidSyntax? lid)", Box(holder.Replace("kind Lid", "kind Lid\n  converted")));

        static string Box(string table) =>
            Files(table)["src/Norristown.Core/Syntax/InternalSyntax/Generated/Nodes/BoxSyntax.cs"];
    }

    /// <summary>A node says its slot order where its own slots come between the ones above it.</summary>
    [Fact]
    public void ALayoutPutsTheSlotsOfTheClassesAboveWhereTheSourceWritesThem()
    {
        const string family = """
            node LidSyntax : SyntaxNode
              abstract
              summary A lid.
              slot Keyword : SyntaxToken
                summary The word.
                kinds Directive
                read ChildTokens[0]
            node ShutLidSyntax : LidSyntax
              kind ShutLid
              summary A shut lid.
              layout CloseBraceToken Keyword
              slot CloseBraceToken : SyntaxToken
                summary The <c>}</c>.
                kinds CloseBrace
                read ChildTokens[0]
            """;
        var shut = Files(family)["src/Norristown.Core/Syntax/Nodes/Generated/ShutLidSyntax.cs"];
        Assert.Contains("public SyntaxToken CloseBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);", shut);
        Assert.Contains("public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(1);", shut);
        Assert.Contains("public abstract SyntaxToken Keyword { get; }",
            Files(family)["src/Norristown.Core/Syntax/Nodes/Generated/LidSyntax.cs"]);
    }

    /// <summary>A kind cannot convert without the class that writes the slots it inherits.</summary>
    [Fact]
    public void AKindConvertsWithTheFamilyThatWritesItsSlots()
    {
        const string family = """
            node LidSyntax : SyntaxNode
              abstract
              summary A lid.
              slot Keyword : SyntaxToken
                summary The word.
                kinds Directive
                read ChildTokens[0]
            node ShutLidSyntax : LidSyntax
              kind ShutLid
              converted
              summary A shut lid.
            """;
        var alone = Assert.Throws<InvalidOperationException>(() => Files(family));
        Assert.Contains("LidSyntax", alone.Message);

        const string half = """
            node LidSyntax : SyntaxNode
              abstract
              converted
              summary A lid.
              slot Keyword : SyntaxToken
                summary The word.
                kinds Directive
                read ChildTokens[0]
            node ShutLidSyntax : LidSyntax
              kind ShutLid
              summary A shut lid.
            """;
        var behind = Assert.Throws<InvalidOperationException>(() => Files(half));
        Assert.Contains("ShutLidSyntax", behind.Message);
    }

    private static SortedDictionary<string, string> Files(string table) =>
        SyntaxWriter.Files(NodeTable.Read(table));

    private static string Red(string table) =>
        Files(table)["src/Norristown.Core/Syntax/Nodes/Generated/WidgetSyntax.cs"];

    private static string Green(string table) =>
        Files(table)["src/Norristown.Core/Syntax/InternalSyntax/Generated/Nodes/WidgetSyntax.cs"];
}
