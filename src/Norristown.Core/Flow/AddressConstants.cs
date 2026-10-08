using System.Runtime.CompilerServices;
using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Finds the constants that an instruction uses as an address, for an editor to suggest declaring
/// them as data. A constant is a number to nt65, so what is at the address it names has no type,
/// no size and no fields, and the editor cannot follow values stored there. Declared as data found
/// elsewhere, or as a hardware register with <c>.mmio</c>, the same name says what is there.
/// <para>
/// A constant counts where an instruction reaches memory through it: as a direct or indexed
/// operand, as the pointer of an indirect one, or as the vector of an indirect jump. A constant
/// that a branch, a jump or a call names directly is the address of code, which <c>.proc</c>
/// declares instead, and is left alone.
/// </para>
/// </summary>
public static class AddressConstants
{
    // The constants each file uses as addresses, by the layout they were found in. A file that
    // an edit leaves alone keeps its layout and its model, so what it uses is not looked for again.
    private static readonly ConditionalWeakTable<CodeLayout, Used> usedByLayout = new();

    // The declarations of each tree that the fix can rewrite.
    private static readonly ConditionalWeakTable<SyntaxTree, List<ConstantDeclarationSyntax>> rewritableByTree = new();

    /// <summary>
    /// Returns every constant that some instruction of <paramref name="files"/> uses as an address,
    /// as <paramref name="current"/> gives each one. A file that was not analyzed again after an
    /// edit still refers to the constants another file declared before it, and the current symbol
    /// is the one the declaring file's suggestions look for.
    /// </summary>
    public static IReadOnlySet<Symbol> UsedAsAddresses(IReadOnlyList<FileAnalysis> files, Func<Symbol, Symbol> current)
    {
        var used = new HashSet<Symbol>();
        foreach (var file in files)
        {
            foreach (var symbol in UsedIn(file))
                used.Add(current(symbol));
        }
        return used;
    }

    /// <summary>
    /// Returns a suggestion for each constant <paramref name="file"/> declares that is in
    /// <paramref name="used"/>, at the constant's name. Only a declaration on a line of the file's
    /// own, written with <c>=</c>, is suggested, because the fix rewrites that line.
    /// </summary>
    public static IEnumerable<Diagnostic> For(FileAnalysis file, IReadOnlySet<Symbol> used)
    {
        var tree = file.Model.Tree;
        foreach (var declaration in Rewritable(tree))
        {
            if (used.FirstOrDefault(symbol => symbol.Tree == tree && symbol.NameSpan == declaration.Name.Span) is not { } symbol)
                continue;
            yield return new Diagnostic(
                tree.GetSpan(declaration.Name.Span),
                Catalogue.ConstantUsedAsAddress.Message(symbol.DisplayName))
            {
                Fix = new DiagnosticFix(FixKind.AddressData),
            };
        }
    }

    /// <summary>
    /// Returns the constants that <paramref name="tree"/> declares with <c>=</c> outside every macro,
    /// which are the declarations the fix can rewrite. They are found once for each tree.
    /// </summary>
    private static List<ConstantDeclarationSyntax> Rewritable(SyntaxTree tree) =>
        rewritableByTree.GetValue(tree, tree => [.. tree.Root.DescendantNodes().OfType<ConstantDeclarationSyntax>()
            .Where(declaration => declaration.EqualsToken.Kind == SyntaxKind.Equals
                && !declaration.Ancestors().OfType<BlockSyntax>().Any(block => block.Opener.Statement is MacroDeclarationSyntax))]);

    /// <summary>
    /// Returns the constants that some instruction of <paramref name="file"/> uses as an address,
    /// looking for them only where the file's layout or model is not the one they were found in.
    /// </summary>
    private static IReadOnlyList<Symbol> UsedIn(FileAnalysis file)
    {
        if (usedByLayout.TryGetValue(file.Layout, out var known) && known.Model == file.Model)
            return known.Symbols;
        var used = new List<Symbol>();
        foreach (var step in file.Layout.Steps)
        {
            if (step.Statement is not InstructionStatementSyntax statement
                || !ReachesMemory(statement.MnemonicKind, file.Layout.Of(statement, step.On)?.Mode)
                || StepOperands.Of(file.Model, step) is not { } operand
                || Named(file.Model, CodeLayout.Expression(operand), step.On) is not { } symbol)
            {
                continue;
            }
            if (symbol is { Kind: SymbolKind.Constant, IsSetting: false, IsEnumMember: false, IsCheapLocal: false }
                && symbol.Value.IsNumber)
            {
                used.Add(symbol);
            }
        }
        usedByLayout.AddOrUpdate(file.Layout, new Used(file.Model, used));
        return used;
    }

    /// <summary>
    /// Returns whether an instruction in <paramref name="mode"/> reaches memory through its operand.
    /// A branch, a jump or a call that names its target goes to code, and an immediate, a block move
    /// or a stack offset names no address of data. An indirect jump reads its vector from memory.
    /// </summary>
    private static bool ReachesMemory(MnemonicKind mnemonic, AddressingMode? mode)
    {
        if (mode is null or AddressingMode.Implied or AddressingMode.Accumulator or AddressingMode.Immediate
            or AddressingMode.Relative or AddressingMode.RelativeLong or AddressingMode.DirectRelative
            or AddressingMode.BlockMove or AddressingMode.StackRelative or AddressingMode.StackRelativeIndirectY
            || mnemonic is MnemonicKind.Pea or MnemonicKind.Per)
        {
            return false;
        }
        return !Instructions.IsControlTransfer(mnemonic)
            || mode is AddressingMode.AbsoluteIndirect or AddressingMode.AbsoluteIndirectX or AddressingMode.AbsoluteIndirectLong;
    }

    /// <summary>
    /// Returns the symbol an operand names, with a constant added to it or taken from it, or null
    /// where it names none. A macro parameter is followed to the argument its call gave it.
    /// </summary>
    private static Symbol? Named(SemanticModel model, SyntaxNode? expression, Expansion? on)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return Named(model, parenthesized.Expression, on);
            case BinaryExpressionSyntax binary when binary.OperatorToken.Kind is SyntaxKind.Plus or SyntaxKind.Minus:
                return model.ValueOf(binary.Right, on).IsNumber && Named(model, binary.Left, on) is { } left ? left
                    : binary.OperatorToken.Kind == SyntaxKind.Plus && model.ValueOf(binary.Left, on).IsNumber
                        ? Named(model, binary.Right, on)
                        : null;
            case NameExpressionSyntax name when model.SymbolOf(name, on) is { } symbol:
                if (symbol.Kind != SymbolKind.MacroParameter)
                    return symbol;
                return model.GivenAt(symbol, on) is { Argument.Value: { } given, Caller: var caller }
                    ? Named(model, given, caller)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The constants one file uses as addresses, with the model they were found under.</summary>
    private sealed record Used(SemanticModel Model, IReadOnlyList<Symbol> Symbols);
}
