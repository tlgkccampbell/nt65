using Norristown.Semantics;

namespace Norristown.Emit;

/// <summary>
/// The name every symbol gets in the output (§13). Names in the output are flat: the
/// output contains no ca65 <c>.proc</c>, <c>.scope</c> or cheap local labels, so it never
/// depends on how ca65 resolves names.
/// <para>
/// A top-level name keeps its spelling and a scoped name <c>outer::inner</c> becomes
/// <c>outer__inner</c>. Those spellings are fixed, because other files and hand-written
/// ca65 refer to them once exported, so two of them that collide is an error rather than a
/// rename. Everything else — cheap locals, and names inside an anonymous scope — is derived
/// from the source and made unique, as <c>draw__loop</c> and <c>draw__loop_2</c>.
/// </para>
/// </summary>
public sealed class FlatNames
{
    private readonly Dictionary<Symbol, string> names = [];

    private FlatNames() { }

    /// <summary>Assigns every symbol in <paramref name="model"/> its output name.</summary>
    public static FlatNames Create(SemanticModel model, List<Diagnostic> diagnostics)
    {
        var flat = new FlatNames();
        var taken = new Dictionary<string, Symbol>(StringComparer.Ordinal);

        // What another file exports keeps the spelling it was exported under, because that is
        // the name in the object file (§12, §13); a local name claims its spelling after them.
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
                if (other.QualifiedName != symbol.QualifiedName)
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

        foreach (var symbol in model.Symbols.Where(symbol => !symbol.IsReachableByPath))
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
}
