using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What the names written in a program mean, by file and position. It is kept file by file,
/// so that the map for a program in which one file changed shares every other file's part
/// with the map it came from.
/// <para>
/// A file whose analysis was kept across an edit to another file still names what that file
/// declared when it was analyzed, which is no longer the symbol the program holds; the
/// interface did not change, so it means the same, and <see cref="ProgramModel.Current"/> answers the
/// symbol it stands for now.
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

    /// <summary>The names written in <paramref name="tree"/>, by position, for a map built from this one.</summary>
    public Dictionary<int, Symbol>? For(SyntaxTree tree) => files.GetValueOrDefault(tree);

    /// <summary>The same map with <paramref name="before"/>'s names replaced by <paramref name="after"/>'s.</summary>
    public Dictionary<SyntaxTree, Dictionary<int, Symbol>> Replacing(
        SyntaxTree before, SyntaxTree after, Dictionary<int, Symbol> names)
    {
        var replaced = new Dictionary<SyntaxTree, Dictionary<int, Symbol>>(files);
        replaced.Remove(before);
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
