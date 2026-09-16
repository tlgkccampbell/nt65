using Norristown.Semantics;

namespace Norristown.Emit;

/// <summary>
/// The name every symbol gets in the output. Names in the output are flat: the
/// output contains no ca65 <c>.proc</c>, <c>.scope</c> or cheap local labels, so it never
/// depends on how ca65 resolves names.
/// <para>
/// A top-level name keeps its spelling and a scoped name <c>outer::inner</c> becomes
/// <c>outer__inner</c>. Those spellings are fixed, because other files and hand-written
/// ca65 refer to them once exported, so two of them that collide is an error rather than a
/// rename. Everything else — cheap locals, and names inside an anonymous scope — is derived
/// from the source and made unique, as <c>draw__loop</c> and <c>draw__loop_2</c>.
/// </para>
/// <para>
/// What a macro body declares is local to each expansion, so one symbol there is many names
/// in the output, one per call. Those are handed out as the expansions are written, which is
/// as deterministic as the writing itself, and each is made unique against everything
/// already claimed.
/// </para>
/// </summary>
public sealed class FlatNames
{
    private readonly Dictionary<Symbol, string> names = [];
    private readonly Dictionary<(Symbol Symbol, Expansion? At), string> perExpansion = [];
    private readonly Dictionary<string, Symbol?> taken = new(StringComparer.Ordinal);

    private FlatNames() { }

    /// <summary>Assigns every symbol in <paramref name="model"/> its output name.</summary>
    public static FlatNames Create(SemanticModel model, List<Diagnostic> diagnostics)
    {
        var flat = new FlatNames();
        var taken = flat.taken;

        // What another file exports keeps the spelling it was exported under, because that is
        // the name in the object file; a local name claims its spelling after them.
        foreach (var symbol in model.ExternalSymbols)
            taken[symbol.FlatName] = symbol;

        // The fixed spellings next, so that a generated name gives way to them and not the
        // other way round.
        foreach (var symbol in model.Symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var name = symbol.FlatName;
            if (taken.TryGetValue(name, out var other))
            {
                // Two declarations of the same name in the same scope are one problem, which
                // binding has already reported; only names that differ in the source and meet
                // in the output are news.
                if (other is not null && other.QualifiedName != symbol.QualifiedName)
                {
                    diagnostics.Add(new Diagnostic(symbol.DeclarationSpan, Severity.Error,
                        $"`{symbol.QualifiedName}` and `{other.QualifiedName}` both become `{name}` in the output",
                        [new RelatedSpan(other.DeclarationSpan, "the other declaration")]));
                }
                flat.names[symbol] = name;
                continue;
            }
            taken[name] = symbol;
            flat.names[symbol] = name;
        }

        // What a macro body declares is not one name but one per expansion, so those are
        // claimed as the expansions are written rather than here.
        foreach (var symbol in model.Symbols.Where(symbol =>
            !symbol.IsReachableByPath && !IsLocalToAnExpansion(symbol)))
        {
            var basis = symbol.FlatName;
            var name = basis;
            for (var n = 2; taken.ContainsKey(name); n++)
                name = $"{basis}_{n}";
            taken[name] = symbol;
            flat.names[symbol] = name;
        }
        return flat;
    }

    /// <summary>
    /// What <paramref name="symbol"/> is called in the output. A symbol another file
    /// declares keeps its own file's spelling, which is what the linker sees.
    /// </summary>
    public string Of(Symbol symbol) => names.GetValueOrDefault(symbol, symbol.FlatName);

    /// <summary>
    /// The same, for a symbol being written out at <paramref name="on"/>. A name a macro
    /// body declares is a different name at every expansion, so each gets one of its own,
    /// derived from the source and made unique: <c>times_x__loop</c>, then
    /// <c>times_x__loop_2</c>.
    /// </summary>
    public string Of(Symbol symbol, Expansion? on)
    {
        if (on is null || !IsLocalToAnExpansion(symbol))
            return Of(symbol);

        var at = (symbol, Expansion.Owning(on, symbol));
        if (at.Item2 is null)
            return Of(symbol);
        if (perExpansion.TryGetValue(at, out var already))
            return already;

        var basis = symbol.FlatName;
        var name = basis;
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = symbol;
        perExpansion[at] = name;
        return name;
    }

    /// <summary>
    /// A name for something the output needs and the source never wrote, such as the label a
    /// long branch skips over. It is derived from the source like every other generated
    /// name, and made unique against everything already claimed.
    /// </summary>
    public string Generated(string basis)
    {
        var name = basis;
        for (var n = 2; taken.ContainsKey(name); n++)
            name = $"{basis}_{n}";
        taken[name] = null;
        return name;
    }

    /// <summary>
    /// Whether the symbol is one a macro body declares, and so one name per expansion rather
    /// than one name. Nothing outside a body can reach it, which is why it can be renamed.
    /// </summary>
    private static bool IsLocalToAnExpansion(Symbol symbol)
    {
        for (var scope = symbol.Scope; scope is not null; scope = scope.Parent)
        {
            if (scope.Kind == ScopeKind.Macro)
                return true;
        }
        return false;
    }
}
