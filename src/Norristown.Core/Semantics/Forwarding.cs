using System.Collections.Concurrent;

namespace Norristown.Semantics;

/// <summary>
/// Finds the symbol a program holds now for a symbol declared in an earlier version of a file. A
/// file that was not re-analyzed after an edit elsewhere still refers to what the edited file
/// declared before. The edit changed nothing that file looked up, so the name still means the
/// same thing, and the current symbol is found by its qualified name.
/// </summary>
/// <param name="symbolsOf">
/// Returns every symbol the file at a path declares now, or null for a path the program does not
/// have.
/// </param>
internal sealed class Forwarding(Func<string, IReadOnlyList<Symbol>?> symbolsOf)
{
    private readonly ConcurrentDictionary<string, (HashSet<Symbol> Current, Dictionary<string, Symbol> ByName)?> files =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a forwarding for a program in which every symbol is current, such as a program
    /// analyzed as a whole.
    /// </summary>
    public static Forwarding None { get; } = new(_ => null);

    /// <summary>
    /// Returns a value indicating whether no two symbols in <paramref name="symbols"/> share a
    /// qualified name.
    /// </summary>
    public static bool HasDistinctNames(IEnumerable<Symbol> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return symbols.Where(symbol => symbol.IsReachableByPath).All(symbol => seen.Add(symbol.QualifiedName));
    }

    /// <summary>
    /// Returns the symbol in the current program that corresponds to <paramref name="symbol"/>.
    /// </summary>
    public Symbol Current(Symbol symbol)
    {
        // A file that is read again may have kept its tree, so whether a symbol is current depends
        // on which symbols the file declares now, not on which tree the symbol came from.
        if (!symbol.IsReachableByPath
            || files.GetOrAdd(symbol.Tree.Path, Index) is not { } file
            || file.Current.Contains(symbol))
        {
            return symbol;
        }
        return file.ByName.GetValueOrDefault(symbol.QualifiedName) ?? symbol;
    }

    private (HashSet<Symbol> Current, Dictionary<string, Symbol> ByName)? Index(string path)
    {
        if (symbolsOf(path) is not { } symbols)
            return null;
        var byName = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        foreach (var declared in symbols.Where(declared => declared.IsReachableByPath))
            byName.TryAdd(declared.QualifiedName, declared);
        return ([.. symbols], byName);
    }
}
