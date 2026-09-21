using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

/// <summary>
/// What the generator writes for a node, from a table of its own rather than the repository's:
/// the red class, whose every property reads a slot of the green one, and the green class, whose
/// constructor takes one parameter per slot and names each of them back.
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
          </Field>
          <Field Name="Name" Type="SyntaxToken">
            <PropertyComment><summary>The name.</summary></PropertyComment>
            <Kind Name="Identifier"/>
          </Field>
          <Field Name="Parts" Type="SeparatedSyntaxList&lt;WidgetSyntax&gt;">
            <PropertyComment><summary>The parts.</summary></PropertyComment>
          </Field>
          <Field Name="CloseBraceToken" Type="SyntaxToken" Optional="true">
            <PropertyComment><summary>The <c>}</c>, or null.</summary></PropertyComment>
            <Kind Name="CloseBrace"/>
          </Field>
        </Node>
        """;

    [Fact]
    public void EveryPropertyReadsItsSlotAndARequiredPieceIsNotNullable()
    {
        var red = Red(Widget);
        Assert.Contains("public SyntaxToken Keyword => SlotToken(0);", red);
        Assert.Contains("public SyntaxToken Name => SlotToken(1);", red);
        Assert.Contains("public SeparatedSyntaxList<WidgetSyntax> Parts => SlotSeparatedList<WidgetSyntax>(2);", red);
        Assert.Contains("public SyntaxToken? CloseBraceToken => SlotTokenOrNull(3);", red);

        // Nothing is searched for and nothing is kept in a field, so the array and its using go too.
        Assert.DoesNotContain("ImmutableArray", red);
    }

    /// <summary>A property that is no piece of the node says what it returns, and returns that.</summary>
    [Fact]
    public void AMemberReturnsWhatTheTableSaysItDoes()
    {
        const string member = """
            <Node Name="WidgetSyntax" Base="StatementSyntax">
              <Kind Name="Widget"/>
              <TypeComment><summary>A widget.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
              </Field>
              <Member Name="IsWide" Type="bool">
                <PropertyComment><summary>Whether it is wide.</summary></PropertyComment>
                <Read><![CDATA[Keyword.Text.Length > 4]]></Read>
              </Member>
            </Node>
            """;
        var red = Red(member);
        Assert.Contains("public SyntaxToken Keyword => SlotToken(0);", red);
        Assert.Contains("public bool IsWide => Keyword.Text.Length > 4;", red);
        Assert.Contains("public override int SlotCount => 1;", Green(member));
    }

    /// <summary>A field reads its slot and a member is read from other properties, and neither is the other.</summary>
    [Fact]
    public void AFieldTakesNoReadAndAMemberWantsOne()
    {
        var read = Assert.Throws<InvalidOperationException>(() => Files("""
            <Node Name="WidgetSyntax" Base="StatementSyntax">
              <Kind Name="Widget"/>
              <TypeComment><summary>A widget.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
                <Read><![CDATA[ChildTokens[0]]]></Read>
              </Field>
            </Node>
            """));
        Assert.Contains("Keyword", read.Message);

        var unread = Assert.Throws<InvalidOperationException>(() => Files("""
            <Node Name="WidgetSyntax" Base="StatementSyntax">
              <Kind Name="Widget"/>
              <TypeComment><summary>A widget.</summary></TypeComment>
              <Member Name="IsWide" Type="bool">
                <PropertyComment><summary>Whether it is wide.</summary></PropertyComment>
              </Member>
            </Node>
            """));
        Assert.Contains("IsWide", unread.Message);
    }

    [Fact]
    public void TheGreenClassTakesOneParameterPerSlot()
    {
        var green = Green(Widget);
        Assert.Contains("internal sealed class WidgetSyntax : StatementSyntax", green);
        Assert.Contains("GreenToken keyword,", green);
        Assert.Contains("GreenToken name,", green);
        Assert.Contains("GreenSeparatedList? parts,", green);
        Assert.Contains("GreenToken? closeBraceToken)", green);
        Assert.Contains(
            ": base(SyntaxKind.Widget, keyword.FullWidth + name.FullWidth + (parts?.FullWidth ?? 0) "
            + "+ (closeBraceToken?.FullWidth ?? 0))",
            green);
        Assert.Contains("public override int SlotCount => 4;", green);
        Assert.Contains("2 => Parts,", green);
        Assert.Contains("new Red.WidgetSyntax(tree, parent, this, position);", green);
    }

    /// <summary>The parser reads back what it built by name, with the type the slot holds.</summary>
    [Fact]
    public void TheGreenClassNamesEachSlot()
    {
        var green = Green(Widget);
        Assert.Contains("        Keyword = keyword;", green);
        Assert.Contains("    /// <summary>The word.</summary>\n    public GreenToken Keyword { get; }", green);
        Assert.Contains("    public GreenToken Name { get; }", green);
        Assert.Contains("    public GreenSeparatedList? Parts { get; }", green);
        Assert.Contains("    public GreenToken? CloseBraceToken { get; }", green);
    }

    /// <summary>
    /// A node slot takes the green class of its own type, and a bare green node where the table
    /// writes none: <c>SyntaxNode</c> itself, or a class written by hand.
    /// </summary>
    [Fact]
    public void ANodeSlotTakesTheGreenClassOfItsType()
    {
        const string holder = """
            <Node Name="LidSyntax" Base="SyntaxNode">
              <Kind Name="Lid"/>
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
              </Field>
            </Node>
            <Node Name="BoxSyntax" Base="SyntaxNode">
              <Kind Name="Box"/>
              <TypeComment><summary>A box.</summary></TypeComment>
              <Field Name="Lid" Type="LidSyntax" Optional="true">
                <PropertyComment><summary>The lid, or null.</summary></PropertyComment>
              </Field>
              <Field Name="Packed" Type="SyntaxNode" Optional="true">
                <PropertyComment><summary>What is inside, or null.</summary></PropertyComment>
              </Field>
            </Node>
            """;
        var box = Files(holder)["InternalSyntax/BoxSyntax.g.cs"];
        Assert.Contains("LidSyntax? lid,", box);
        Assert.Contains("GreenNode? packed)", box);
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
              </Field>
            </AbstractNode>
            <Node Name="ShutLidSyntax" Base="LidSyntax" Layout="CloseBraceToken Keyword">
              <Kind Name="ShutLid"/>
              <TypeComment><summary>A shut lid.</summary></TypeComment>
              <Field Name="CloseBraceToken" Type="SyntaxToken">
                <PropertyComment><summary>The <c>}</c>.</summary></PropertyComment>
                <Kind Name="CloseBrace"/>
              </Field>
            </Node>
            """;
        var shut = Files(family)["Nodes/ShutLidSyntax.g.cs"];
        Assert.Contains("public SyntaxToken CloseBraceToken => SlotToken(0);", shut);
        Assert.Contains("public override SyntaxToken Keyword => SlotToken(1);", shut);
        Assert.Contains("public abstract SyntaxToken Keyword { get; }",
            Files(family)["Nodes/LidSyntax.g.cs"]);
    }

    /// <summary>A node's slot order names each of its slots, its own and the ones above it.</summary>
    [Fact]
    public void ALayoutNamesEverySlotExactlyOnce()
    {
        var missed = Assert.Throws<InvalidOperationException>(() => Files("""
            <Node Name="LidSyntax" Base="SyntaxNode" Layout="Keyword">
              <Kind Name="Lid"/>
              <TypeComment><summary>A lid.</summary></TypeComment>
              <Field Name="Keyword" Type="SyntaxToken">
                <PropertyComment><summary>The word.</summary></PropertyComment>
                <Kind Name="Directive"/>
              </Field>
              <Field Name="CloseBraceToken" Type="SyntaxToken">
                <PropertyComment><summary>The <c>}</c>.</summary></PropertyComment>
                <Kind Name="CloseBrace"/>
              </Field>
            </Node>
            """));
        Assert.Contains("LidSyntax", missed.Message);
    }

    /// <summary>
    /// The files these nodes make; they are written without the <c>Tree</c> around them, and
    /// with <c>StatementSyntax</c> under them, since every node is under a node of the table.
    /// </summary>
    private static SortedDictionary<string, string> Files(string nodes) =>
        SyntaxWriter.Files(NodeTable.Read(
            $"<Tree>\n<AbstractNode Name=\"StatementSyntax\" Base=\"SyntaxNode\"/>\n{nodes}\n</Tree>"));

    private static string Red(string table) =>
        Files(table)["Nodes/WidgetSyntax.g.cs"];

    private static string Green(string table) =>
        Files(table)["InternalSyntax/WidgetSyntax.g.cs"];
}
