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
        <Node Name="WidgetSyntax" Base="StatementSyntax">
          <Kind Name="Widget"/>
          <TypeComment><summary>A widget.</summary></TypeComment>
          <Field Name="Keyword" Type="SyntaxToken">
            <PropertyComment><summary>The word.</summary></PropertyComment>
            <Kind Name="Directive"/>
            <Read><![CDATA[ChildTokens[0]]]></Read>
          </Field>
          <Field Name="Name" Type="SyntaxToken" Today="SyntaxToken?">
            <PropertyComment><summary>The name.</summary></PropertyComment>
            <Kind Name="Identifier"/>
            <Read><![CDATA[NameAt(1)]]></Read>
          </Field>
          <Field Name="Parts" Type="SeparatedSyntaxList&lt;WidgetSyntax&gt;" Today="ImmutableArray&lt;WidgetSyntax&gt;">
            <PropertyComment><summary>The parts.</summary></PropertyComment>
            <Nodes/>
          </Field>
          <Field Name="CloseBraceToken" Type="SyntaxToken" Optional="true">
            <PropertyComment><summary>The <c>}</c>, or null.</summary></PropertyComment>
            <Kind Name="CloseBrace"/>
            <Read><![CDATA[FirstToken(SyntaxKind.CloseBrace)]]></Read>
          </Field>
        </Node>
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
        var red = Red(Converted(Widget, "WidgetSyntax"));
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
        foreach (var green in new[] { Green(Widget), Green(Converted(Widget, "WidgetSyntax")) })
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
            <Node Name="LidSyntax" Base="SyntaxNode">
              <Kind Name="Lid"/>
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </Node>
            <Node Name="BoxSyntax" Base="SyntaxNode">
              <Kind Name="Box"/>
              <TypeComment><summary>A box.</summary></TypeComment>
              <Field Name="Lid" Type="LidSyntax" Optional="true">
                <PropertyComment><summary>The lid, or null.</summary></PropertyComment>
                <Read><![CDATA[FirstNode<LidSyntax>()]]></Read>
              </Field>
            </Node>
            """;
        Assert.Contains("GreenNode? lid)", Box(holder));
        Assert.Contains("LidSyntax? lid)", Box(Converted(holder, "LidSyntax")));

        static string Box(string table) =>
            Files(table)["InternalSyntax/BoxSyntax.g.cs"];
    }

    /// <summary>A node says its slot order where its own slots come between the ones above it.</summary>
    [Fact]
    public void ALayoutPutsTheSlotsOfTheClassesAboveWhereTheSourceWritesThem()
    {
        const string family = """
            <AbstractNode Name="LidSyntax" Base="SyntaxNode">
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </AbstractNode>
            <Node Name="ShutLidSyntax" Base="LidSyntax" Layout="CloseBraceToken Keyword">
              <Kind Name="ShutLid"/>
              <TypeComment><summary>A shut lid.</summary></TypeComment>
              <Field Name="CloseBraceToken" Type="SyntaxToken">
                <PropertyComment><summary>The <c>}</c>.</summary></PropertyComment>
                <Kind Name="CloseBrace"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </Node>
            """;
        var shut = Files(family)["Nodes/ShutLidSyntax.g.cs"];
        Assert.Contains("public SyntaxToken CloseBraceToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);", shut);
        Assert.Contains("public override SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(1);", shut);
        Assert.Contains("public abstract SyntaxToken Keyword { get; }",
            Files(family)["Nodes/LidSyntax.g.cs"]);
    }

    /// <summary>A kind cannot convert without the class that writes the slots it inherits.</summary>
    [Fact]
    public void AKindConvertsWithTheFamilyThatWritesItsSlots()
    {
        const string family = """
            <AbstractNode Name="LidSyntax" Base="SyntaxNode">
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </AbstractNode>
            <Node Name="ShutLidSyntax" Base="LidSyntax" Converted="true">
              <Kind Name="ShutLid"/>
              <TypeComment><summary>A shut lid.</summary></TypeComment>
            </Node>
            """;
        var alone = Assert.Throws<InvalidOperationException>(() => Files(family));
        Assert.Contains("LidSyntax", alone.Message);

        const string half = """
            <AbstractNode Name="LidSyntax" Base="SyntaxNode" Converted="true">
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </AbstractNode>
            <Node Name="ShutLidSyntax" Base="LidSyntax">
              <Kind Name="ShutLid"/>
              <TypeComment><summary>A shut lid.</summary></TypeComment>
            </Node>
            """;
        var behind = Assert.Throws<InvalidOperationException>(() => Files(half));
        Assert.Contains("ShutLidSyntax", behind.Message);
    }

    /// <summary>The files these nodes make; they are written without the <c>Tree</c> around them.</summary>
    private static SortedDictionary<string, string> Files(string nodes) =>
        SyntaxWriter.Files(NodeTable.Read($"<Tree>\n{nodes}\n</Tree>"));

    /// <summary>The table with <paramref name="node"/> converted, which is the one word it takes.</summary>
    private static string Converted(string table, string node) =>
        table.Replace($"Name=\"{node}\"", $"Name=\"{node}\" Converted=\"true\"");

    private static string Red(string table) =>
        Files(table)["Nodes/WidgetSyntax.g.cs"];

    private static string Green(string table) =>
        Files(table)["InternalSyntax/WidgetSyntax.g.cs"];
}
