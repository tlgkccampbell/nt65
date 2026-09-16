using System.Collections.Concurrent;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The symbol a program holds now for one declared in an earlier version of a file. A file
/// that was not analyzed again after an edit elsewhere still names what the edited file
/// declared before the edit; the edit left that file's interface alone, so the name means
/// the same, and the symbol it means now is found by its qualified name.
/// </summary>
internal sealed class Forwarding(IReadOnlyDictionary<string, SyntaxTree> trees, Func<string, IEnumerable<Symbol>> symbolsOf)
{
    private readonly ConcurrentDictionary<string, Dictionary<string, Symbol>> byName = new(StringComparer.Ordinal);

    /// <summary>Whether no symbol of <paramref name="symbols"/> shares a qualified name another has.</summary>
    public static bool HasDistinctNames(IEnumerable<Symbol> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return symbols.Where(symbol => symbol.IsReachableByPath).All(symbol => seen.Add(symbol.QualifiedName));
    }

    /// <summary>What <paramref name="symbol"/> stands for in the program as it is now.</summary>
    public Symbol Current(Symbol symbol)
    {
        if (!trees.TryGetValue(symbol.Tree.Path, out var tree) || tree == symbol.Tree || !symbol.IsReachableByPath)
            return symbol;
        var names = byName.GetOrAdd(symbol.Tree.Path, path =>
        {
            var found = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            foreach (var declared in symbolsOf(path).Where(declared => declared.IsReachableByPath))
                found.TryAdd(declared.QualifiedName, declared);
            return found;
        });
        return names.GetValueOrDefault(symbol.QualifiedName) ?? symbol;
    }
}
