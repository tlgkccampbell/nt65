using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// A parsed node: a kind and the children the parser put under it, tokens included. Every
/// token of a line ends up in exactly one node, so a statement's text is its line's text.
/// </summary>
/// <param name="kind">What the node is.</param>
/// <param name="children">The node's children, in source order.</param>
public sealed class GreenSyntax(SyntaxKind kind, ImmutableArray<GreenNode> children)
    : GreenNode(kind, SumWidths(children))
{
    /// <summary>The node's children, in source order.</summary>
    public ImmutableArray<GreenNode> Children { get; } = children;

    /// <inheritdoc/>
    public override int SlotCount => Children.Length;

    /// <inheritdoc/>
    public override GreenNode GetSlot(int index) => Children[index];

    internal override SyntaxNode CreateRed(SyntaxTree tree, SyntaxNode? parent, int position) => Kind switch
    {
        SyntaxKind.BlankLine => new BlankLineSyntax(tree, parent, this, position),
        SyntaxKind.BlockCloseLine => new BlockCloseLineSyntax(tree, parent, this, position),
        SyntaxKind.BlockContinuation => new BlockContinuationSyntax(tree, parent, this, position),
        SyntaxKind.LabeledLine => new LabeledLineSyntax(tree, parent, this, position),
        SyntaxKind.Label => new LabelSyntax(tree, parent, this, position),
        SyntaxKind.ConstantDeclaration => new ConstantDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.InstructionStatement => new InstructionStatementSyntax(tree, parent, this, position),
        SyntaxKind.DataDirective => new DataDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.DataDeclaration => new DataDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ElementCount => new ElementCountSyntax(tree, parent, this, position),
        SyntaxKind.ElementIndex => new ElementIndexSyntax(tree, parent, this, position),
        SyntaxKind.DataValues => new DataValuesSyntax(tree, parent, this, position),
        SyntaxKind.ValueList => new ValueListSyntax(tree, parent, this, position),
        SyntaxKind.RecordValues => new RecordValuesSyntax(tree, parent, this, position),
        SyntaxKind.MemberValue => new MemberValueSyntax(tree, parent, this, position),
        SyntaxKind.CpuDirective => new CpuDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ExportDirective => new ExportDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ExportItem => new ExportItemSyntax(tree, parent, this, position),
        SyntaxKind.ModuleDirective => new ModuleDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.UseDirective => new UseDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.UseItem => new UseItemSyntax(tree, parent, this, position),
        SyntaxKind.ImportDirective => new ImportDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ImportItem => new ImportItemSyntax(tree, parent, this, position),
        SyntaxKind.ImportSignature => new ImportSignatureSyntax(tree, parent, this, position),
        SyntaxKind.SegmentDeclaration => new SegmentDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.SegmentAttribute => new SegmentAttributeSyntax(tree, parent, this, position),
        SyntaxKind.BankRange => new BankRangeSyntax(tree, parent, this, position),
        SyntaxKind.SegmentBlock => new SegmentBlockSyntax(tree, parent, this, position),
        SyntaxKind.SegmentRegion => new SegmentRegionSyntax(tree, parent, this, position),
        SyntaxKind.ProcDeclaration => new ProcDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ExternProcDeclaration => new ExternProcDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.MultiProcDeclaration => new MultiProcDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ScopeDeclaration => new ScopeDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.EnumDeclaration => new EnumDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.StructDeclaration => new StructDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.UnionDeclaration => new UnionDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.CharmapDeclaration => new CharmapDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ListDeclaration => new ListDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.FuncDeclaration => new FuncDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ParameterList => new ParameterListSyntax(tree, parent, this, position),
        SyntaxKind.SignatureDeclaration => new SignatureDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.ConfigDeclaration => new ConfigDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.EnumMember => new EnumMemberSyntax(tree, parent, this, position),
        SyntaxKind.CharmapEntry => new CharmapEntrySyntax(tree, parent, this, position),
        SyntaxKind.ListItems => new ListItemsSyntax(tree, parent, this, position),
        SyntaxKind.IfDirective => new IfDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ElseIfDirective => new ElseIfDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ElseDirective => new ElseDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.RepeatDirective => new RepeatDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.EachDirective => new EachDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.AssertDirective => new AssertDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ErrorDirective => new ErrorDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.MacroDeclaration => new MacroDeclarationSyntax(tree, parent, this, position),
        SyntaxKind.MacroParameterList => new MacroParameterListSyntax(tree, parent, this, position),
        SyntaxKind.MacroParameter => new MacroParameterSyntax(tree, parent, this, position),
        SyntaxKind.ParameterKind => new ParameterKindSyntax(tree, parent, this, position),
        SyntaxKind.EmptyBlock => new EmptyBlockSyntax(tree, parent, this, position),
        SyntaxKind.MacroCall => new MacroCallSyntax(tree, parent, this, position),
        SyntaxKind.BlockSplice => new BlockSpliceSyntax(tree, parent, this, position),
        SyntaxKind.NamedArgument => new NamedArgumentSyntax(tree, parent, this, position),
        SyntaxKind.NextDirective => new NextDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.PatchDirective => new PatchDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.StateDirective => new StateDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.EnsureDirective => new EnsureDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.FrameDirective => new FrameDirectiveSyntax(tree, parent, this, position),
        SyntaxKind.ErrorLine => new ErrorLineSyntax(tree, parent, this, position),
        SyntaxKind.SkippedTokens => new SkippedTokensSyntax(tree, parent, this, position),
        SyntaxKind.ProcSignature => new ProcSignatureSyntax(tree, parent, this, position),
        SyntaxKind.StateList => new StateListSyntax(tree, parent, this, position),
        SyntaxKind.StateItem => new StateItemSyntax(tree, parent, this, position),
        SyntaxKind.BinaryExpression => new BinaryExpressionSyntax(tree, parent, this, position),
        SyntaxKind.UnaryExpression => new UnaryExpressionSyntax(tree, parent, this, position),
        SyntaxKind.ParenthesizedExpression => new ParenthesizedExpressionSyntax(tree, parent, this, position),
        SyntaxKind.NumberExpression => new NumberExpressionSyntax(tree, parent, this, position),
        SyntaxKind.CharacterExpression => new CharacterExpressionSyntax(tree, parent, this, position),
        SyntaxKind.StringExpression => new StringExpressionSyntax(tree, parent, this, position),
        SyntaxKind.CpuNameExpression => new CpuNameExpressionSyntax(tree, parent, this, position),
        SyntaxKind.CurrentAddressExpression => new CurrentAddressExpressionSyntax(tree, parent, this, position),
        SyntaxKind.NameExpression => new NameExpressionSyntax(tree, parent, this, position),
        SyntaxKind.CallExpression => new CallExpressionSyntax(tree, parent, this, position),
        SyntaxKind.ArgumentList => new ArgumentListSyntax(tree, parent, this, position),
        SyntaxKind.ErrorExpression => new ErrorExpressionSyntax(tree, parent, this, position),
        SyntaxKind.ImmediateOperand => new ImmediateOperandSyntax(tree, parent, this, position),
        SyntaxKind.AccumulatorOperand => new AccumulatorOperandSyntax(tree, parent, this, position),
        SyntaxKind.AbsoluteOperand => new AbsoluteOperandSyntax(tree, parent, this, position),
        SyntaxKind.IndirectOperand => new IndirectOperandSyntax(tree, parent, this, position),
        SyntaxKind.IndexedIndirectOperand => new IndexedIndirectOperandSyntax(tree, parent, this, position),
        SyntaxKind.LongIndirectOperand => new LongIndirectOperandSyntax(tree, parent, this, position),
        SyntaxKind.AddressPrefix => new AddressPrefixSyntax(tree, parent, this, position),
        SyntaxKind.BracedOperand => new BracedOperandSyntax(tree, parent, this, position),
        _ => throw new InvalidOperationException($"{Kind} is not the kind of a parsed node"),
    };
}
