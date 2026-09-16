using System.Collections.Concurrent;

namespace Norristown.Semantics;

/// <summary>
/// The symbol a program holds now for one declared in an earlier version of a file. A file
/// that was not analyzed again after an edit elsewhere still names what the edited file
/// declared before; the edit left what that file looked up alone, so the name means the same,
/// and the symbol it means now is found by its qualified name.
/// </summary>
/// <param name="symbolsOf">Every symbol the file at a path declares now, or null for a path the program does not have.</param>
internal sealed class Forwarding(Func<string, IReadOnlyList<Symbol>?> symbolsOf)
{
    private readonly ConcurrentDictionary<string, (HashSet<Symbol> Current, Dictionary<string, Symbol> ByName)?> files =
        new(StringComparer.Ordinal);

    /// <summary>A program in which every symbol is current, which a program analyzed whole is.</summary>
    public static Forwarding None { get; } = new(_ => null);

    /// <summary>Whether no symbol of <paramref name="symbols"/> shares a qualified name another has.</summary>
    public static bool HasDistinctNames(IEnumerable<Symbol> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return symbols.Where(symbol => symbol.IsReachableByPath).All(symbol => seen.Add(symbol.QualifiedName));
    }

    /// <summary>What <paramref name="symbol"/> stands for in the program as it is now.</summary>
    public Symbol Current(Symbol symbol)
    {
        // A file read again may have kept its tree, so whether a symbol is current is a question
        // of which symbols the file declares now rather than of which tree it came from.
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
