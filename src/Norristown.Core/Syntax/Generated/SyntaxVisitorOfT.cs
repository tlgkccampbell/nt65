// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.

namespace Norristown.Syntax;

/// <summary>
/// Dispatches on what a node is: <see cref="Visit"/> hands a node to the method its class
/// has here, and every one of those hands it on to <see cref="DefaultVisit"/> unless it is
/// overridden. Override the nodes a feature is about and leave the rest;
/// <see cref="SyntaxWalker"/> is the one that goes on down the tree.
/// </summary>
/// <typeparam name="TResult">What visiting a node works out.</typeparam>
public abstract class SyntaxVisitor<TResult>
{
    /// <summary>Hands <paramref name="node"/> to the method its class has, and does nothing for null.</summary>
    /// <param name="node">The node to visit, or null.</param>
    /// <returns>What the method for its class worked out, or the default for none.</returns>
    public virtual TResult? Visit(SyntaxNode? node) => node is null ? default : node.Accept(this);

    /// <summary>What every method below does unless it is overridden.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>The default of <typeparamref name="TResult"/>.</returns>
    public virtual TResult? DefaultVisit(SyntaxNode node) => default;

    /// <summary>Visits <see cref="AbsoluteOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitAbsoluteOperand(AbsoluteOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AccumulatorOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitAccumulatorOperand(AccumulatorOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AddressPrefixSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitAddressPrefix(AddressPrefixSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ArgumentListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitArgumentList(ArgumentListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="AssertDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitAssertDirective(AssertDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BankRangeSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBankRange(BankRangeSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BinaryExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBinaryExpression(BinaryExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlankLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBlankLine(BlankLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockCloseLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBlockCloseLine(BlockCloseLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockContinuationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBlockContinuation(BlockContinuationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockSpliceSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBlockSplice(BlockSpliceSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBlock(BlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="BracedOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitBracedOperand(BracedOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CallExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCallExpression(CallExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharacterExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCharacterExpression(CharacterExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharmapDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCharmapDeclaration(CharmapDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CharmapEntrySyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCharmapEntry(CharmapEntrySyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ConfigDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitConfigDeclaration(ConfigDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ConstantDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitConstantDeclaration(ConstantDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CpuDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCpuDirective(CpuDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CpuNameExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCpuNameExpression(CpuNameExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="CurrentAddressExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitCurrentAddressExpression(CurrentAddressExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitDataDeclaration(DataDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitDataDirective(DataDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="DataValuesSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitDataValues(DataValuesSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EachDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitEachDirective(EachDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElementCountSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitElementCount(ElementCountSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElementIndexSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitElementIndex(ElementIndexSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElseDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitElseDirective(ElseDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ElseIfDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitElseIfDirective(ElseIfDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EmptyBlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitEmptyBlock(EmptyBlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnsureDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitEnsureDirective(EnsureDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnumDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitEnumDeclaration(EnumDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="EnumMemberSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitEnumMember(EnumMemberSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitErrorDirective(ErrorDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitErrorExpression(ErrorExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ErrorLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitErrorLine(ErrorLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExportDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitExportDirective(ExportDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExportItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitExportItem(ExportItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ExternProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitExternProcDeclaration(ExternProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FileSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitFile(FileSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FrameDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitFrameDirective(FrameDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="FuncDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitFuncDeclaration(FuncDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IfDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitIfDirective(IfDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImmediateOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitImmediateOperand(ImmediateOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitImportDirective(ImportDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitImportItem(ImportItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ImportSignatureSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitImportSignature(ImportSignatureSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IndexedIndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitIndexedIndirectOperand(IndexedIndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="IndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitIndirectOperand(IndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="InstructionStatementSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitInstructionStatement(InstructionStatementSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LabelSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitLabel(LabelSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LabeledLineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitLabeledLine(LabeledLineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LineSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitLine(LineSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ListDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitListDeclaration(ListDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ListItemsSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitListItems(ListItemsSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="LongIndirectOperandSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitLongIndirectOperand(LongIndirectOperandSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroCallSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMacroCall(MacroCallSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMacroDeclaration(MacroDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroParameterListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMacroParameterList(MacroParameterListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MacroParameterSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMacroParameter(MacroParameterSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MemberValueSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMemberValue(MemberValueSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ModuleDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitModuleDirective(ModuleDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="MultiProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitMultiProcDeclaration(MultiProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NameExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitNameExpression(NameExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NamedArgumentSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitNamedArgument(NamedArgumentSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NextDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitNextDirective(NextDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="NumberExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitNumberExpression(NumberExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParameterKindSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitParameterKind(ParameterKindSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParameterListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitParameterList(ParameterListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ParenthesizedExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="PatchDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitPatchDirective(PatchDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ProcDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitProcDeclaration(ProcDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ProcSignatureSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitProcSignature(ProcSignatureSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="RecordValuesSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitRecordValues(RecordValuesSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="RepeatDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitRepeatDirective(RepeatDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ScopeDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitScopeDeclaration(ScopeDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentAttributeSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSegmentAttribute(SegmentAttributeSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentBlockSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSegmentBlock(SegmentBlockSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSegmentDeclaration(SegmentDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SegmentRegionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSegmentRegion(SegmentRegionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SignatureDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSignatureDeclaration(SignatureDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="SkippedTokensSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitSkippedTokens(SkippedTokensSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitStateDirective(StateDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitStateItem(StateItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StateListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitStateList(StateListSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StringExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitStringExpression(StringExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="StructDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitStructDeclaration(StructDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UnaryExpressionSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitUnaryExpression(UnaryExpressionSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UnionDeclarationSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitUnionDeclaration(UnionDeclarationSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UseDirectiveSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitUseDirective(UseDirectiveSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="UseItemSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitUseItem(UseItemSyntax node) => DefaultVisit(node);

    /// <summary>Visits <see cref="ValueListSyntax"/>.</summary>
    /// <param name="node">The node visited.</param>
    /// <returns>What visiting it worked out.</returns>
    public virtual TResult? VisitValueList(ValueListSyntax node) => DefaultVisit(node);
}
