using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What one file means: its scopes and declarations (§6), what every name in it refers to,
/// and what its expressions are worth (§9).
/// <para>
/// A model is built once and is then read-only, so an editor may ask it anything from any
/// thread. A file is part of a program (§12): a name it does not declare may be one another
/// file exports, so models are built together by <see cref="ProgramModel"/>.
/// </para>
/// </summary>
public sealed class SemanticModel
{
    private readonly IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved;
    private readonly ILookup<Symbol, SymbolReference> bySymbol;
    private readonly Func<string, long?>? binaryLength;

    internal SemanticModel(
        SyntaxTree tree,
        SegmentTable segments,
        Binder.Result bound,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        IEnumerable<Diagnostic> fromTheProgram,
        Func<string, long?>? binaryLength = null)
    {
        this.binaryLength = binaryLength;
        Tree = tree;
        Segments = segments;
        FileScope = bound.FileScope;
        Symbols = bound.Symbols;
        References = bound.References;
        this.resolved = resolved;

        Diagnostics = Norristown.Diagnostics.Ordered(bound.Diagnostics.Concat(fromTheProgram));
        bySymbol = References.ToLookup(reference => reference.Symbol);
        // A struct member is written out as the number it is, so it is no symbol to the
        // linker either, any more than a define is.
        ExternalSymbols = [.. References
            .Where(reference => !reference.IsDeclaration
                && reference.Symbol.Tree != tree
                && !reference.Symbol.IsDefine
                && reference.Symbol.Kind != SymbolKind.Member)
            .Select(reference => reference.Symbol)
            .Distinct()];
    }

    /// <summary>The file this model is of.</summary>
    public SyntaxTree Tree { get; }

    /// <summary>The program's segments, which is what address sizes come from (§5.2, §7.2).</summary>
    public SegmentTable Segments { get; }

    /// <summary>The file's top-level scope.</summary>
    public Scope FileScope { get; }

    /// <summary>Every symbol the file declares, in source order.</summary>
    public IReadOnlyList<Symbol> Symbols { get; }

    /// <summary>Every place a name is written, declarations included, ordered by position.</summary>
    public IReadOnlyList<SymbolReference> References { get; }

    /// <summary>What is wrong with the file's names and constants, ordered by line and column.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// The symbols this file names but another file declares, in the order it first names
    /// them (§12). These are what its output imports; a define is not among them, because a
    /// define is written as its value and is no symbol to the linker (§5.3).
    /// </summary>
    public IReadOnlyList<Symbol> ExternalSymbols { get; }

    /// <summary>Builds the model for <paramref name="tree"/> alone, seeing no other file.</summary>
    public static SemanticModel Create(SyntaxTree tree, SegmentTable segments) =>
        ProgramModel.Create([tree], segments).Files[0];

    /// <summary>The name written at <paramref name="position"/>, or null if there is none.</summary>
    public SymbolReference? ReferenceAt(int position)
    {
        // References do not overlap, so the last one starting at or before the position is
        // the only one that can hold it.
        var low = 0;
        var high = References.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var span = References[middle].Span;
            if (position < span.Start)
                high = middle - 1;
            else if (position > span.End)
                low = middle + 1;
            else
                return References[middle];
        }
        return null;
    }

    /// <summary>Every place <paramref name="symbol"/> is written, its declaration included.</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(Symbol symbol) => [.. bySymbol[symbol]];

    /// <summary>What an expression is worth, for an editor to show (§9).</summary>
    public Value ValueOf(SyntaxNode expression) => Evaluator.ValueOf(expression, Segments, resolved);

    /// <summary>
    /// How much room a data directive takes: the bytes it generates and how many elements
    /// they are. Null where nt65 cannot say, such as for an <c>.align</c>.
    /// </summary>
    public DataSize? RoomFor(SyntaxNode directive) =>
        Evaluator.DataSizeOf(directive, Segments, resolved, binaryLength);

    /// <summary>
    /// Evaluates an expression and reports what is wrong with it into
    /// <paramref name="diagnostics"/>. Used for the operands of a data directive, which no
    /// symbol holds and which nothing else would ever evaluate with anything to say.
    /// </summary>
    public void Check(SyntaxNode expression, List<Diagnostic> diagnostics) =>
        Evaluator.Check(expression, Segments, resolved, diagnostics, binaryLength);

    /// <summary>The bytes an operand becomes: a literal, or text a charmap maps.</summary>
    public IReadOnlyList<long>? BytesOf(SyntaxNode operand) =>
        Evaluator.BytesOf(operand, Segments, resolved);

    /// <summary>The items an operand stands for when it names a list, or null when it does not.</summary>
    public IReadOnlyList<SyntaxNode>? ItemsOf(SyntaxNode operand) => Evaluator.ItemsOf(operand, resolved);

    /// <summary>
    /// The address size of an expression (§7.2). <c>*</c> takes the size of
    /// <paramref name="segment"/>, or of the default segment when none is named.
    /// </summary>
    public AddressSize? AddressSizeOf(SyntaxNode expression, string? segment = null) =>
        Evaluator.AddressSizeOf(expression, segment ?? SegmentTable.DefaultSegment, Segments, resolved);
}
