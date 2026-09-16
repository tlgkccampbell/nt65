using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What one file means: its scopes and declarations (§6), what every name in it refers to,
/// and what its expressions are worth (§9).
/// <para>
/// A model is built once from a syntax tree and is then read-only, so an editor may ask it
/// anything from any thread. A file is analyzed on its own; names another file exports
/// arrive with Stage 6.
/// </para>
/// </summary>
public sealed class SemanticModel
{
    private readonly Dictionary<int, Symbol> resolved;
    private readonly ILookup<Symbol, SymbolReference> bySymbol;

    private SemanticModel(SyntaxTree tree, SegmentTable segments, Binder.Result bound)
    {
        Tree = tree;
        Segments = segments;
        FileScope = bound.FileScope;
        Symbols = bound.Symbols;
        References = bound.References;
        resolved = bound.References
            .Where(reference => !reference.IsDeclaration)
            .ToDictionary(reference => reference.Span.Start, reference => reference.Symbol);

        Evaluator.EvaluateSymbols(segments, Symbols, resolved, bound.Diagnostics);
        Diagnostics = Norristown.Diagnostics.Ordered(bound.Diagnostics);
        bySymbol = References.ToLookup(reference => reference.Symbol);
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

    /// <summary>Builds the model for <paramref name="tree"/> against <paramref name="segments"/>.</summary>
    public static SemanticModel Create(SyntaxTree tree, SegmentTable segments) =>
        new(tree, segments, Binder.Bind(tree, segments));

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
    /// The address size of an expression (§7.2). <c>*</c> takes the size of
    /// <paramref name="segment"/>, or of the default segment when none is named.
    /// </summary>
    public AddressSize? AddressSizeOf(SyntaxNode expression, string? segment = null) =>
        Evaluator.AddressSizeOf(expression, segment ?? SegmentTable.DefaultSegment, Segments, resolved);
}
