using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Finds the symbol that a path in a file names, without reporting anything, as the file's
/// names are being bound. The path is looked for from the scope it appears in, then among what
/// the file's <c>.use</c> items bring in, and then in the program's modules. The type that a
/// <c>.type</c> names is resolved on demand, because nothing has been evaluated yet.
/// <para>
/// The binder reads through one of these while it resolves a file. A <see cref="FamilyDeclarer"/>
/// reads through another before the modules have exported, to find the enum a family walks.
/// </para>
/// </summary>
/// <param name="program">The program whose modules a path may name.</param>
/// <param name="brought">The names the file's <c>.use</c> items bring in, which may still be filling.</param>
/// <param name="globs">The modules whose exports a <c>.use module::*</c> brings in.</param>
/// <param name="touched">The action told each name looked for in a module.</param>
/// <param name="keepsTypes">
/// Whether the type found for a <c>.type</c> is kept on its symbol. Only a reading against the
/// complete program keeps it, because one against a program that has not exported yet may differ.
/// </param>
internal sealed class NamePaths(
    ProgramSymbols program,
    IReadOnlyDictionary<string, BroughtName> brought,
    IReadOnlyList<ProgramSymbols.Module> globs,
    Action<string?, string> touched,
    bool keepsTypes)
{
    // The symbols whose bodies are being found, so that a type that names itself ends the search.
    private readonly HashSet<Symbol> resolving = [];

    /// <summary>
    /// Returns the symbol a path names, resolved from <paramref name="at"/>, or null. The path is
    /// walked as far as its first missing part, and the symbol reached before that part is
    /// returned.
    /// </summary>
    public Symbol? NamedByPath(ExpressionSyntax? expression, Scope at)
    {
        if (expression is not NameExpressionSyntax name)
            return null;
        var parts = name.Parts;
        var present = parts.TakeWhile(part => part.Name is { IsMissing: false }).Select(part => part.Name!.Text).ToList();
        if (present.Count == 0)
            return null;
        var start = Lookup.First(present[0], parts.Count == 1, name.GlobalToken is not null, at, program, brought, globs, touched);
        return Lookup.Walk(start, present, program, BodyOf, touched)?.Symbol;
    }

    /// <summary>
    /// Returns the place that one more part of a path reaches after <paramref name="before"/>, or
    /// null. A module leads to what it declares, and a symbol to what its body declares.
    /// </summary>
    public Resolution? Step(Resolution before, string name) =>
        before.Module is { } prefix ? Lookup.InModule(name, prefix, program, touched)
            : BodyOf(before.Symbol!)?.FindMember(name) is { } member ? new Resolution(member)
            : null;

    /// <summary>
    /// Returns the scope that a path can look into after <paramref name="symbol"/>. This is the
    /// scope a routine or a scope opens, or the scope of the type that a member or a data
    /// declaration names. The type's scope makes the fields of <c>.type T</c> data reachable
    /// through the data.
    /// </summary>
    public Scope? BodyOf(Symbol symbol)
    {
        if (Lookup.BodyOf(symbol) is { } known)
            return known;
        if (symbol.Kind == SymbolKind.Macro || !resolving.Add(symbol))
            return null;
        try
        {
            if (symbol.TypeExpression is not null)
                return TypeOf(symbol)?.Body;

            // Data found elsewhere that states no type has the type of the data its address names,
            // or of one element of it. An offset is not evaluated yet, so data at an offset has
            // no fields here.
            return symbol is { Kind: SymbolKind.AddressAlias, ValueExpression: NameExpressionSyntax { Parent: DataDeclarationSyntax } address }
                && NamedByPath(address, symbol.Scope) is { } named
                    ? BodyOf(named)
                    : null;
        }
        finally
        {
            resolving.Remove(symbol);
        }
    }

    /// <summary>
    /// Resolves the type that a <c>.type</c> names, from where the <c>.type</c> appears. This runs
    /// on demand rather than in order, because a name may reach into a type that the file declares
    /// later.
    /// </summary>
    private Symbol? TypeOf(Symbol symbol)
    {
        var type = NamedByPath(symbol.TypeExpression, symbol.Scope);

        // A symbol of a file that was not read again after an edit belongs to a completed model,
        // which other threads may be reading, so the type is kept only on a symbol still being
        // built.
        if (keepsTypes && !symbol.IsFrozen)
            symbol.Type = type;
        return type;
    }
}
