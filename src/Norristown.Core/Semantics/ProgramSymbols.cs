using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What the files of a program can see of one another (§12). Every file is a module, and
/// its symbols are private unless exported; a name a file does not declare is looked for
/// here before it is called undeclared.
/// <para>
/// Lookup finds any file-scope symbol of another file, exported or not, because a better
/// diagnostic for a private name is that it exists and is not exported. Whether it may
/// actually be used is <see cref="IsExported"/>, asked of the whole name once it resolves:
/// an interior label reached as <c>outer::inner</c> is exported by <c>inner</c>, and
/// <c>outer</c> itself need not be.
/// </para>
/// </summary>
public sealed class ProgramSymbols
{
    private readonly Dictionary<string, List<Symbol>> atFileScope;
    private readonly HashSet<Symbol> exported;

    private ProgramSymbols(Dictionary<string, List<Symbol>> atFileScope, HashSet<Symbol> exported)
    {
        this.atFileScope = atFileScope;
        this.exported = exported;
    }

    /// <summary>A program of one file, which can see nothing beyond itself.</summary>
    public static ProgramSymbols Empty { get; } = new([], []);

    /// <summary>
    /// The table for a program whose files have been collected but not yet resolved. Two
    /// files exporting the same output name is reported here, because it is the program's
    /// problem rather than either file's.
    /// </summary>
    internal static ProgramSymbols Build(IReadOnlyList<Module> modules, List<Diagnostic> diagnostics)
    {
        var atFileScope = new Dictionary<string, List<Symbol>>(StringComparer.Ordinal);
        var exported = new HashSet<Symbol>();
        var byOutputName = new Dictionary<string, Symbol>(StringComparer.Ordinal);

        foreach (var module in modules.OrderBy(module => module.Tree.Path, StringComparer.Ordinal))
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                if (symbol.IsCheapLocal)
                    continue;
                if (!atFileScope.TryGetValue(symbol.Name, out var declared))
                    atFileScope[symbol.Name] = declared = [];
                declared.Add(symbol);
            }

            foreach (var symbol in module.Exported)
            {
                if (!exported.Add(symbol))
                    continue;
                if (byOutputName.TryGetValue(symbol.FlatName, out var other))
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan, Severity.Error,
                        $"`{symbol.FlatName}` is exported by two files",
                        [new RelatedSpan(other.DeclarationSpan, "also exported here")]));
                    continue;
                }
                byOutputName[symbol.FlatName] = symbol;
            }
        }
        return new ProgramSymbols(atFileScope, exported);
    }

    /// <summary>
    /// The symbol another file declares at its top level under <paramref name="name"/>, or
    /// null when no file does or when more than one private one does and nothing says which.
    /// </summary>
    public Symbol? Lookup(string name, SyntaxTree from)
    {
        if (!atFileScope.TryGetValue(name, out var candidates))
            return null;
        Symbol? onlyPrivate = null;
        var privates = 0;
        foreach (var symbol in candidates)
        {
            if (symbol.Tree == from)
                continue;
            if (exported.Contains(symbol))
                return symbol;
            onlyPrivate ??= symbol;
            privates++;
        }

        // Several files keeping a private name of their own is not an ambiguity anyone can
        // resolve, and saying the name is undeclared is the honest answer.
        return privates == 1 ? onlyPrivate : null;
    }

    /// <summary>Whether <paramref name="symbol"/> is exported, and so may be named from another file.</summary>
    public bool IsExported(Symbol symbol) => exported.Contains(symbol);

    /// <summary>One file as the program sees it before its own names are resolved.</summary>
    /// <param name="Tree">The file.</param>
    /// <param name="FileScope">Its top-level scope, which is what another file can reach into.</param>
    /// <param name="Exported">The symbols its <c>.export</c> items name.</param>
    internal sealed record Module(SyntaxTree Tree, Scope FileScope, IReadOnlyList<Symbol> Exported);
}
