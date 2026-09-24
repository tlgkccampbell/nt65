using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Checks what the generator writes for a node, from a table of its own rather than the
/// repository's. It writes the red class, whose every property reads a slot of the green one. It
/// also writes the green class, whose constructor takes one parameter per slot and keeps each in
/// a property of its own name.
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

        // No property searches the children or keeps a value in a field, so the class needs no
        // ImmutableArray, and no using directive for it.
        Assert.DoesNotContain("ImmutableArray", red);
    }

    /// <summary>
    /// A member, which is a property that is not one of the node's slots, returns the expression
    /// its <c>Read</c> gives.
    /// </summary>
    [Fact]
    public void AMemberReturnsWhatTheTableDeclares()
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

    /// <summary>
    /// A field reads its slot and may not have a <c>Read</c>. A member is computed from other
    /// properties and must have one.
    /// </summary>
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

    /// <summary>
    /// The green class exposes each slot as a named property of the slot's type, so the parser
    /// can read back what it built by name.
    /// </summary>
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
    /// A node slot takes the green class of its own type. Where the table generates no class for
    /// the type, as for <c>SyntaxNode</c> itself or a hand-written class, it takes a bare green
    /// node.
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

    /// <summary>
    /// A node gives its slot order in a <c>Layout</c> where its own slots come between the ones it
    /// inherits from its base classes.
    /// </summary>
    [Fact]
    public void ALayoutPutsTheBaseClassesSlotsAroundTheNodesOwnSlots()
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

    /// <summary>
    /// A <c>Layout</c> must name every slot of the node, its own and the inherited ones, exactly
    /// once.
    /// </summary>
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
    /// A node is changed by being rebuilt. It has one <c>Update</c> over every slot, in source
    /// order, and one <c>With</c> per slot that calls it. When every slot gets the same green
    /// node, the same node comes out, so a rewrite that changes nothing returns the tree it was
    /// given.
    /// </summary>
    [Fact]
    public void EverySlotHasAWithAndTheNodeHasAnUpdate()
    {
        var red = Red(Widget);
        Assert.Contains(
            "    public WidgetSyntax Update(\n"
            + "        SyntaxToken keyword,\n"
            + "        SyntaxToken name,\n"
            + "        SeparatedSyntaxList<WidgetSyntax> parts,\n"
            + "        SyntaxToken? closeBraceToken) =>\n",
            red);
        Assert.Contains(
            "ReferenceEquals(keyword.Green, Keyword.Green)\n"
            + "        && ReferenceEquals(name.Green, Name.Green)\n"
            + "        && ReferenceEquals(parts.Green, Parts.Green)\n"
            + "        && ReferenceEquals(closeBraceToken?.Green, CloseBraceToken?.Green)\n"
            + "            ? this\n"
            + "            : Annotated(SyntaxFactory.Widget(keyword, name, parts, closeBraceToken));",
            red);
        Assert.Contains(
            "    public WidgetSyntax WithName(SyntaxToken name) =>\n"
            + "        Update(Keyword, name, Parts, CloseBraceToken);",
            red);
        Assert.Contains(
            "    public WidgetSyntax WithCloseBraceToken(SyntaxToken? closeBraceToken) =>\n"
            + "        Update(Keyword, Name, Parts, closeBraceToken);",
            red);
    }

    /// <summary>
    /// A green node rolls up what its slots hold as it is built — a diagnostic, an annotation —
    /// so that a walk looking for one of those follows only the slots that lead to one. They are
    /// bits of one flags word, so each slot is read once no matter how many things are rolled up.
    /// </summary>
    [Fact]
    public void AGreenNodeRollsUpWhatItsSlotsHold()
    {
        Assert.Contains(
            "        Flags =\n"
            + "            keyword.Flags\n"
            + "            | name.Flags\n"
            + "            | (parts?.Flags ?? GreenFlags.None)\n"
            + "            | (closeBraceToken?.Flags ?? GreenFlags.None);",
            Green(Widget));
    }

    /// <summary>
    /// The factory takes a node's child elements as red-tree values and hands their green nodes to
    /// the typed green constructor. An optional child element may be left out of the call too, as
    /// long as every child element after it is optional as well.
    /// </summary>
    [Fact]
    public void TheFactoryBuildsANodeFromItsPieces()
    {
        var factory = Files(Widget)["SyntaxFactory.g.cs"];
        Assert.Contains("public static partial class SyntaxFactory", factory);
        Assert.Contains(
            "    public static WidgetSyntax Widget(\n"
            + "        SyntaxToken keyword,\n"
            + "        SyntaxToken name,\n"
            + "        SeparatedSyntaxList<WidgetSyntax> parts,\n"
            + "        SyntaxToken? closeBraceToken = null) =>\n"
            + "        (WidgetSyntax)Detached(new InternalSyntax.WidgetSyntax(\n"
            + "            keyword.Green,\n"
            + "            name.Green,\n"
            + "            parts.Green,\n"
            + "            closeBraceToken?.Green));",
            factory);
    }

    /// <summary>The rewriter rewrites a node's slots and calls <c>Update</c>, which does the rest.</summary>
    [Fact]
    public void TheRewriterRewritesEverySlotAndUpdates()
    {
        var rewriter = Files(Widget)["SyntaxRewriter.g.cs"];
        Assert.Contains("public abstract partial class SyntaxRewriter", rewriter);
        Assert.Contains(
            "    public override SyntaxNode? VisitWidget(WidgetSyntax node) =>\n"
            + "        node.Update(\n"
            + "            VisitToken(node.Keyword),\n"
            + "            VisitToken(node.Name),\n"
            + "            VisitList(node.Parts),\n"
            + "            VisitToken(node.CloseBraceToken));",
            rewriter);
    }

    /// <summary>
    /// The table allows one row per class, because a second row of the same name would write the
    /// same file twice.
    /// </summary>
    [Fact]
    public void ANodeIsWrittenOnce()
    {
        var twice = Assert.Throws<InvalidOperationException>(() => Files($"{Widget}\n{Widget}"));
        Assert.Contains("WidgetSyntax is declared twice", twice.Message);
    }

    /// <summary>
    /// A node's chain of base classes must end. A chain that loops back on itself would have the
    /// generator walk up the hierarchy forever, so it is reported instead.
    /// </summary>
    [Fact]
    public void AClassDoesNotDeriveFromItself()
    {
        var circle = Assert.Throws<InvalidOperationException>(() => Files("""
            <Node Name="WidgetSyntax" Base="LidSyntax">
              <Kind Name="Widget"/>
              <TypeComment><summary>A widget.</summary></TypeComment>
            </Node>
            <AbstractNode Name="LidSyntax" Base="WidgetSyntax">
              <TypeComment><summary>A lid.</summary></TypeComment>
            </AbstractNode>
            """));
        Assert.Contains("run in a circle", circle.Message);
    }

    /// <summary>
    /// Checks that every generated file ends its lines with LF alone, as the repository does,
    /// whatever newline the platform the compiler runs on uses.
    /// </summary>
    [Fact]
    public void EveryFileUsesLineFeeds()
    {
        foreach (var (path, text) in Files(Widget))
            Assert.False(text.Contains('\r'), $"{path} contains a carriage return");
    }

    /// <summary>
    /// Returns the files generated for <paramref name="nodes"/>, whose XML omits the enclosing
    /// <c>Tree</c> element. An abstract <c>StatementSyntax</c> is added for them to derive from,
    /// since every node's base must be a node of the table.
    /// </summary>
    private static SortedDictionary<string, string> Files(string nodes) =>
        SyntaxWriter.Files(NodeTable.Read(
            $"<Tree>\n<AbstractNode Name=\"StatementSyntax\" Base=\"SyntaxNode\"/>\n{nodes}\n</Tree>"));

    private static string Red(string table) =>
        Files(table)["Nodes/WidgetSyntax.g.cs"];

    private static string Green(string table) =>
        Files(table)["InternalSyntax/WidgetSyntax.g.cs"];
}
