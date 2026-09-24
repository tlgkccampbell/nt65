namespace Norristown.Semantics;

/// <summary>
/// Decides, in one place, what a name means where it appears. The binder resolves a file with
/// this class and a <see cref="SemanticModel"/> answers an editor with it, so the names offered
/// at the caret and the symbol a name binds to can never be computed two different ways.
/// <para>
/// Nothing here reads the tree. The class is given the scope a name appears in, the file's
/// <c>.use</c> items and the program's table, and returns the <see cref="Resolution"/> the name
/// reaches. It reports a diagnostic only through the callback a caller passes, and the caller
/// decides what to report about the result.
/// </para>
/// </summary>
internal static class Lookup
{
    /// <summary>
    /// Returns what a name means when the scopes around it do not declare it. The name can be
    /// something a <c>.use</c> brought in, a define, the first part of a module's path, or
    /// something a <c>.use module::*</c> brought in. <paramref name="last"/> indicates that the
    /// name is the whole reference, not a step on a path, because a module is the start of a path
    /// and never a value.
    /// </summary>
    public static Resolution? Outside(
        string name,
        bool last,
        ProgramSymbols program,
        IReadOnlyDictionary<string, Resolution> brought,
        IReadOnlyList<ProgramSymbols.Module> globs,
        Action<string?, string>? touched = null,
        Action<DiagnosticMessage, DiagnosticFix?>? report = null)
    {
        if (brought.TryGetValue(name, out var found))
            return found with { IsAlias = found.Symbol is { } target && target.Name != name };
        if (program.Define(name) is { } define)
            return new Resolution(define);
        if (!last && program.IsModulePath(name))
            return new Resolution(null, name);

        // A module on its own is not a value, so a name that appears alone means what a `*`
        // brought in, if anything. Only when nothing else matches is it the module, which the
        // caller reports as a module used as a name.
        Resolution? chosen = null;
        foreach (var module in globs)
        {
            if (program.Member(module, name, touched) is not { } exported
                || exported.Tree == module.Tree && !exported.IsExported || chosen?.Symbol == exported)
            {
                continue;
            }
            if (chosen is { } other)
            {
                report?.Invoke(Catalogue.ExportAmbiguous.Message(name, other.From, module.Name, module.Name, name), null);
                return Resolution.Reported;
            }
            chosen = new Resolution(exported, From: module.Name);
        }
        return chosen ?? (last && program.IsModulePath(name) ? new Resolution(null, name) : null);
    }

    /// <summary>
    /// Returns the place that the first part of a path from the root of the modules reaches, or
    /// reports a diagnostic and returns null when no module path starts with that name.
    /// </summary>
    public static Resolution? ModuleRoot(
        string name, ProgramSymbols program, Action<DiagnosticMessage, DiagnosticFix?>? report = null)
    {
        if (program.IsModulePath(name))
            return new Resolution(null, name);
        report?.Invoke(Catalogue.ModuleUnknown.Message(name), null);
        return null;
    }

    /// <summary>
    /// Returns the place that <paramref name="name"/> reaches after <paramref name="prefix"/>,
    /// which is a module or the start of a module's name. The caller checks whether the result
    /// may be named from outside its module.
    /// </summary>
    public static Resolution? InModule(
        string name,
        string prefix,
        ProgramSymbols program,
        Action<string?, string>? touched = null,
        Action<DiagnosticMessage, DiagnosticFix?>? report = null)
    {
        var path = $"{prefix}::{name}";
        if (program.IsModulePath(path))
            return new Resolution(null, path);
        if (program.ModuleNamed(prefix) is not { } module)
        {
            report?.Invoke(Catalogue.ModuleUnknown.Message(path), null);
            return null;
        }
        if (program.Member(module, name, touched) is not { } member)
        {
            var near = Spelling.Nearest(name, Members(module));
            report?.Invoke(
                Catalogue.NotDeclaredIn.Message(name, $"module `{prefix}`", Suggesting(near)),
                near is null ? null : new DiagnosticFix(FixKind.NearestName, near));
            return null;
        }
        return new Resolution(member);
    }

    /// <summary>
    /// Returns what the first part of a name means at <paramref name="at"/>, without reporting
    /// anything. The part is looked up from the root of the modules when
    /// <paramref name="fromRoot"/> is true, and otherwise in the scopes around it and then as
    /// <see cref="Outside"/> looks.
    /// </summary>
    public static Resolution? First(
        string name,
        bool last,
        bool fromRoot,
        Scope at,
        ProgramSymbols program,
        IReadOnlyDictionary<string, Resolution> brought,
        IReadOnlyList<ProgramSymbols.Module> globs,
        Action<string?, string>? touched = null) =>
        fromRoot ? ModuleRoot(name, program)
            : at.Lookup(name) is { } local ? new Resolution(local)
            : Outside(name, last, program, brought, globs, touched);

    /// <summary>
    /// Returns the place that a path reaches, walking from <paramref name="start"/>, which is
    /// what its first part means, through each part after it. The walk stops at a part that
    /// means nothing and returns null, or at a name that has already been reported and returns
    /// <see cref="Resolution.Reported"/>. Nothing is reported here.
    /// </summary>
    /// <param name="start">What the first part of the path means.</param>
    /// <param name="parts">Every part of the path, including the first.</param>
    /// <param name="program">The program whose modules the path may name.</param>
    /// <param name="bodyOf">
    /// The function that returns the scope <c>::</c> looks in after a symbol, or null to use
    /// <see cref="BodyOf(Symbol)"/>. The binder passes one that resolves a <c>.type</c> on demand.
    /// </param>
    /// <param name="touched">The action told each name looked for in a module.</param>
    public static Resolution? Walk(
        Resolution? start,
        IReadOnlyList<string> parts,
        ProgramSymbols program,
        Func<Symbol, Scope?>? bodyOf = null,
        Action<string?, string>? touched = null)
    {
        var place = start;
        for (var i = 1; i < parts.Count && place is { IsReported: false } before; i++)
        {
            place = before.Module is { } prefix ? InModule(parts[i], prefix, program, touched)
                : (bodyOf ?? BodyOf)(before.Symbol!)?.FindMember(parts[i]) is { } member ? new Resolution(member)
                : null;
        }
        return place;
    }

    /// <summary>
    /// Returns the names a path may contain after a scope's <c>::</c>, which are the names a
    /// misspelling could have meant.
    /// </summary>
    public static IEnumerable<string> Members(Scope container) =>
        container.Symbols.Where(symbol => !symbol.IsCheapLocal).Select(symbol => symbol.Name);

    /// <summary>
    /// Returns the names a path may contain after a module's <c>::</c>, which are the names its
    /// file declares at the top level and the names it re-exports.
    /// </summary>
    public static IEnumerable<string> Members(ProgramSymbols.Module module) =>
        Members(module.FileScope).Concat(module.Reexports.Select(reexport => reexport.Name));

    /// <summary>
    /// Returns a near miss formatted for a message, such as <c>; did you mean `count`?</c>, or
    /// an empty string when the name resembles nothing declared there.
    /// </summary>
    public static string Suggesting(string? near) => near is null ? "" : $"; did you mean `{near}`?";

    /// <summary>
    /// Returns the scope that <c>::</c> after <paramref name="symbol"/> looks in. This is the
    /// symbol's own scope, or else the scope of the type it names, so the fields of data declared
    /// with <c>.type T</c> are reachable through it. A macro has a body but is excluded, because
    /// what its body declares is local to each <see cref="Expansion"/>, so there is no single
    /// symbol to name from outside.
    /// </summary>
    public static Scope? BodyOf(Symbol symbol) =>
        symbol.Kind == SymbolKind.Macro ? null : symbol.Body ?? symbol.Type?.Body;

    /// <summary>
    /// Returns every name that may appear alone in <paramref name="at"/>, in the order a lookup
    /// tries them. The order is what the scopes from here out to the file declare, nearest
    /// first, then what <c>.use</c> brought in, then the defines, and then what a
    /// <c>.use module::*</c> brings in. Where two entries share a name, the first is what the
    /// name means, which is the same rule <see cref="Outside"/> follows.
    /// <para>
    /// Modules are not included. A module is the start of a path, not a name that refers to
    /// something, and the program lists the modules a path may start with.
    /// </para>
    /// </summary>
    public static IEnumerable<(string Name, Resolution Means)> InScope(
        Scope at,
        ProgramSymbols program,
        IReadOnlyDictionary<string, Resolution> brought,
        IReadOnlyList<ProgramSymbols.Module> globs)
    {
        for (var scope = at; scope is not null; scope = scope.Parent)
        {
            foreach (var symbol in scope.Symbols)
                yield return (symbol.DisplayName, new Resolution(symbol));
        }
        foreach (var (name, place) in brought)
            yield return (name, place with { IsAlias = place.Symbol is { } target && target.Name != name });
        foreach (var define in program.Defines)
            yield return (define.Name, new Resolution(define));
        foreach (var module in globs)
        {
            foreach (var symbol in module.FileScope.Symbols)
            {
                if (symbol.IsExported && !symbol.IsCheapLocal)
                    yield return (symbol.Name, new Resolution(symbol, From: module.Name));
            }
            foreach (var reexport in module.Reexports)
            {
                if (program.Member(module, reexport.Name) is { } exported)
                    yield return (reexport.Name, new Resolution(exported, From: module.Name));
            }
        }
    }
}
