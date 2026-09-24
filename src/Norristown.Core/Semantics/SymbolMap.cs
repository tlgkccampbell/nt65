using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Maps each name in a program, by file and position, to the symbol it means. The map is kept
/// file by file, so that the map for a program in which one file changed shares every other
/// file's part with the map it came from.
/// <para>
/// A file whose analysis was kept across an edit to another file still refers to what that
/// file declared when it was analyzed, which is no longer the symbol the program holds. The
/// interface did not change, so the name means the same thing, and
/// <see cref="ProgramModel.Current"/> gives the symbol it corresponds to now.
/// </para>
/// </summary>
internal sealed class SymbolMap : IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol>
{
    private readonly Dictionary<SyntaxTree, Dictionary<int, Symbol>> files;
    private readonly Func<Symbol, Symbol> current;

    public SymbolMap(Dictionary<SyntaxTree, Dictionary<int, Symbol>> files, Func<Symbol, Symbol>? current = null)
    {
        this.files = files;
        this.current = current ?? (symbol => symbol);
    }

    public int Count => files.Values.Sum(file => file.Count);

    public IEnumerable<(SyntaxTree Tree, int Position)> Keys => this.Select(pair => pair.Key);

    public IEnumerable<Symbol> Values => this.Select(pair => pair.Value);

    public Symbol this[(SyntaxTree Tree, int Position) key] =>
        TryGetValue(key, out var symbol) ? symbol : throw new KeyNotFoundException();

    /// <summary>
    /// Returns the current version of <paramref name="symbol"/>, for a symbol that another symbol
    /// holds a direct reference to rather than naming it.
    /// </summary>
    public Symbol Current(Symbol symbol) => current(symbol);

    /// <summary>
    /// Returns a copy of the map's files in which each listed file's names are replaced by those
    /// of the version that was read again.
    /// </summary>
    public Dictionary<SyntaxTree, Dictionary<int, Symbol>> Replacing(
        IEnumerable<(SyntaxTree Before, SyntaxTree After, Dictionary<int, Symbol> Names)> files)
    {
        var replaced = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>(this.files);
        foreach (var (before, _, _) in files)
            replaced.Remove(before);
        foreach (var (_, after, names) in files)
            replaced[after] = names;
        return replaced;
    }

    public bool ContainsKey((SyntaxTree Tree, int Position) key) => TryGetValue(key, out _);

    public bool TryGetValue((SyntaxTree Tree, int Position) key, [MaybeNullWhen(false)] out Symbol value)
    {
        if (files.TryGetValue(key.Tree, out var file) && file.TryGetValue(key.Position, out var found))
        {
            value = current(found);
            return true;
        }
        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<(SyntaxTree Tree, int Position), Symbol>> GetEnumerator()
    {
        foreach (var (tree, file) in files)
        {
            foreach (var (position, symbol) in file)
                yield return new((tree, position), current(symbol));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
