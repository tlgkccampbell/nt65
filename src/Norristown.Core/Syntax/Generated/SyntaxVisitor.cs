// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax;

/// <summary>
/// Dispatches on what a node is: <see cref="Visit"/> hands a node to the method its class
/// has here, and every one of those hands it on to <see cref="DefaultVisit"/> unless it is
/// overridden. Override the nodes a feature is about and leave the rest;
/// <see cref="SyntaxWalker"/> is the one that goes on down the tree.
/// </summary>
public abstract class SyntaxVisitor
{
    /// <summary>Hands <paramref name="node"/> to the method its class has, and does nothing for null.</summary>
    /// <param name="node">The node to visit, or null.</param>
    public virtual void Visit(SyntaxNode? node) => node?.Accept(this);

    /// <summary>What every method below does unless it is overridden.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void DefaultVisit(SyntaxNode node)
    {
    }

    /// <summary>Visits <see cref="AbsoluteOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitAbsoluteOperand(AbsoluteOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AccumulatorOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitAccumulatorOperand(AccumulatorOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AddressPrefixSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitAddressPrefix(AddressPrefixSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ArgumentListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitArgumentList(ArgumentListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AssertDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitAssertDirective(AssertDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BankRangeSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBankRange(BankRangeSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BinaryExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBinaryExpression(BinaryExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlankLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBlankLine(BlankLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockCloseLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBlockCloseLine(BlockCloseLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockContinuationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBlockContinuation(BlockContinuationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockSpliceSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBlockSplice(BlockSpliceSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBlock(BlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BracedDataSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBracedData(BracedDataSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BracedOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitBracedOperand(BracedOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CallExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCallExpression(CallExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharacterExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCharacterExpression(CharacterExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharmapDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCharmapDeclaration(CharmapDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharmapEntrySyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCharmapEntry(CharmapEntrySyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ConfigDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitConfigDeclaration(ConfigDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ConstantDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitConstantDeclaration(ConstantDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CpuDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCpuDirective(CpuDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CpuNameExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCpuNameExpression(CpuNameExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CurrentAddressExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitCurrentAddressExpression(CurrentAddressExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataBodySyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitDataBody(DataBodySyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitDataDeclaration(DataDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitDataDirective(DataDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataValuesSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitDataValues(DataValuesSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EachDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitEachDirective(EachDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElementCountSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitElementCount(ElementCountSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElementIndexSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitElementIndex(ElementIndexSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElseDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitElseDirective(ElseDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElseIfDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitElseIfDirective(ElseIfDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EmptyBlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitEmptyBlock(EmptyBlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnsureDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitEnsureDirective(EnsureDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnumDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitEnumDeclaration(EnumDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnumMemberSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitEnumMember(EnumMemberSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitErrorDirective(ErrorDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitErrorExpression(ErrorExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitErrorLine(ErrorLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExportDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitExportDirective(ExportDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExportItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitExportItem(ExportItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExternProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitExternProcDeclaration(ExternProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FileSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitFile(FileSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FrameDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitFrameDirective(FrameDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FuncDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitFuncDeclaration(FuncDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IdentifierNameSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitIdentifierName(IdentifierNameSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IfDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitIfDirective(IfDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImmediateOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitImmediateOperand(ImmediateOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitImportDirective(ImportDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitImportItem(ImportItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportSignatureSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitImportSignature(ImportSignatureSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IndexedIndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitIndexedIndirectOperand(IndexedIndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitIndirectOperand(IndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="InlineDataSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitInlineData(InlineDataSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="InstructionStatementSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitInstructionStatement(InstructionStatementSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LabelSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitLabel(LabelSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LabeledLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitLabeledLine(LabeledLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitLine(LineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ListDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitListDeclaration(ListDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ListItemsSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitListItems(ListItemsSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LongIndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitLongIndirectOperand(LongIndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroCallSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMacroCall(MacroCallSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMacroDeclaration(MacroDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroParameterListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMacroParameterList(MacroParameterListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroParameterSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMacroParameter(MacroParameterSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MemberValueSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMemberValue(MemberValueSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ModuleDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitModuleDirective(ModuleDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MultiProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitMultiProcDeclaration(MultiProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NameExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitNameExpression(NameExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NamedArgumentSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitNamedArgument(NamedArgumentSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NextDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitNextDirective(NextDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NumberExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitNumberExpression(NumberExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParameterKindSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitParameterKind(ParameterKindSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParameterListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitParameterList(ParameterListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParameterSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitParameter(ParameterSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParenthesizedExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="PatchDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitPatchDirective(PatchDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitProcDeclaration(ProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ProcSignatureSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitProcSignature(ProcSignatureSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="RecordValuesSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitRecordValues(RecordValuesSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="RepeatDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitRepeatDirective(RepeatDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ScopeDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitScopeDeclaration(ScopeDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentAttributeSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSegmentAttribute(SegmentAttributeSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentBlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSegmentBlock(SegmentBlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSegmentDeclaration(SegmentDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentRegionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSegmentRegion(SegmentRegionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SignatureDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSignatureDeclaration(SignatureDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SkippedTokensSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitSkippedTokens(SkippedTokensSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateDirective(StateDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateFlagItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateFlagItem(StateFlagItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateInlineItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateInlineItem(StateInlineItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateItem(StateItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateKeepsItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateKeepsItem(StateKeepsItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateList(StateListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateSetItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateSetItem(StateSetItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateUnknownItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateUnknownItem(StateUnknownItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateValueItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStateValueItem(StateValueItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StringExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStringExpression(StringExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StructDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitStructDeclaration(StructDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UnaryExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitUnaryExpression(UnaryExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UnionDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitUnionDeclaration(UnionDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UseDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitUseDirective(UseDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UseItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitUseItem(UseItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ValueListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    public virtual void VisitValueList(ValueListSyntax node) => DefaultVisit(node);
}
