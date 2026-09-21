namespace Norristown.Semantics;

/// <summary>
/// What a name means where it is written, in one place. The binder resolves a file with this
/// and a <see cref="SemanticModel"/> answers an editor with it, so what is offered at the
/// caret and what the name will bind to can never be worked out two different ways.
/// <para>
/// Nothing here reads the tree or reports anything: it is given the scope a name is written
/// in, the file's <c>.use</c> items and the program's table, and answers with the
/// <see cref="Place"/> the name reaches. Whoever asks decides what to say about the answer.
/// </para>
/// </summary>
internal static class Lookup
{
    /// <summary>
    /// What a name the scopes around it do not declare means: what a <c>.use</c> brought in, a
    /// define, the first part of a module's path, or what a <c>.use module::*</c> brought in.
    /// <paramref name="last"/> says the name is the whole of what is written rather than a
    /// step on a path, because a module is the start of a path and never a value.
    /// </summary>
    public static Place? Outside(
        string name,
        bool last,
        ProgramSymbols program,
        IReadOnlyDictionary<string, Place> brought,
        IReadOnlyList<ProgramSymbols.Module> globs,
        Action<string>? touched = null,
        Action<DiagnosticMessage>? report = null)
    {
        if (brought.TryGetValue(name, out var found))
            return found with { IsAlias = found.Symbol is { } target && target.Name != name };
        if (program.Define(name) is { } define)
            return new Place(define);
        if (!last && program.IsModulePath(name))
            return new Place(null, name);

        // A module on its own is no value, so a name a `*` brought in is what one standing
        // alone means; with nothing else, it is the module, which is reported as one.
        Place? chosen = null;
        foreach (var module in globs)
        {
            if (program.Member(module, name, touched) is not { } exported
                || exported.Tree == module.Tree && !exported.IsExported || chosen?.Symbol == exported)
            {
                continue;
            }
            if (chosen is { } other)
            {
                report?.Invoke(Catalogue.ExportAmbiguous.Says(name, other.From, module.Name, module.Name, name));
                return Place.Reported;
            }
            chosen = new Place(exported, From: module.Name);
        }
        return chosen ?? (last && program.IsModulePath(name) ? new Place(null, name) : null);
    }

    /// <summary>The first part of a path written from the root of the modules.</summary>
    public static Place? ModuleRoot(string name, ProgramSymbols program, Action<DiagnosticMessage>? report = null)
    {
        if (program.IsModulePath(name))
            return new Place(null, name);
        report?.Invoke(Catalogue.ModuleUnknown.Says(name));
        return null;
    }

    /// <summary>
    /// The part after <paramref name="prefix"/>, which is a module or the start of one's name.
    /// Whether what it reaches may be named from outside its module is the caller's to say.
    /// </summary>
    public static Place? InModule(
        string name,
        string prefix,
        ProgramSymbols program,
        Action<string>? touched = null,
        Action<DiagnosticMessage>? report = null)
    {
        var path = $"{prefix}::{name}";
        if (program.IsModulePath(path))
            return new Place(null, path);
        if (program.ModuleNamed(prefix) is not { } module)
        {
            report?.Invoke(Catalogue.ModuleUnknown.Says(path));
            return null;
        }
        if (program.Member(module, name, touched) is not { } member)
        {
            report?.Invoke(Catalogue.NotDeclaredIn.Says(name, $"module `{prefix}`"));
            return null;
        }
        return new Place(member);
    }

    /// <summary>
    /// What a name may reach into with <c>::</c>: its own scope, or the scope of the type it
    /// names, which is what makes the fields of <c>.type T</c> data reachable through it. A
    /// macro has a body and is not one of them: what a body declares is local to each
    /// expansion, so there is no one symbol to name from outside.
    /// </summary>
    public static Scope? BodyOf(Symbol symbol) =>
        symbol.Kind == SymbolKind.Macro ? null : symbol.Body ?? symbol.Type?.Body;

    /// <summary>
    /// Every name that may be written alone in <paramref name="at"/>, in the order a lookup
    /// tries them: what the scopes from here out to the file declare, the nearest first, what
    /// <c>.use</c> brought in, the defines, and what a <c>.use module::*</c> brings in. Where
    /// two entries share a name the first is what the name means, which is the one rule
    /// <see cref="Outside"/> follows.
    /// <para>
    /// A module is not among them: it is the start of a path rather than a name that stands
    /// for something, and which modules a path may start with is the program's to list.
    /// </para>
    /// </summary>
    public static IEnumerable<(string Name, Place Means)> InScope(
        Scope at,
        ProgramSymbols program,
        IReadOnlyDictionary<string, Place> brought,
        IReadOnlyList<ProgramSymbols.Module> globs)
    {
        for (var scope = at; scope is not null; scope = scope.Parent)
        {
            foreach (var symbol in scope.Symbols)
                yield return (symbol.DisplayName, new Place(symbol));
        }
        foreach (var (name, place) in brought)
            yield return (name, place with { IsAlias = place.Symbol is { } target && target.Name != name });
        foreach (var define in program.Defines)
            yield return (define.Name, new Place(define));
        foreach (var module in globs)
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                if (symbol.IsExported && !symbol.IsCheapLocal)
                    yield return (symbol.Name, new Place(symbol, From: module.Name));
            }
            foreach (var reexport in module.Reexports)
            {
                if (program.Member(module, reexport.Name) is { } exported)
                    yield return (reexport.Name, new Place(exported, From: module.Name));
            }
        }
    }
}
