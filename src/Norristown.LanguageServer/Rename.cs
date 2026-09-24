using Norristown.Semantics;
using Norristown.Standard;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Answers the editor's rename requests. It finds the range a rename would replace, checks the
/// new name, and builds the edit that renames every occurrence across the program.
/// </summary>
internal static class Rename
{
    /// <summary>
    /// Returns the range of the name at <paramref name="position"/>, which is what a rename would
    /// replace, or null where there is no name that can be renamed.
    /// </summary>
    public static Protocol.Range? RangeAt(SemanticModel model, int position) =>
        model.ReferenceAt(position) is { } reference && !IsStandard(reference)
            ? Lsp.ToRange(model.Tree, reference.Span)
            : null;

    /// <summary>
    /// Returns the edit that renames every occurrence of the name at <paramref name="position"/>,
    /// or the reason it cannot be renamed to <paramref name="newName"/>.
    /// </summary>
    public static (Protocol.WorkspaceEdit? Edit, string? Problem) EditAt(
        ProgramModel program, SemanticModel model, int position, string newName)
    {
        if (model.ReferenceAt(position) is not { } reference)
            return (null, "there is no name here to rename");
        if (IsStandard(reference))
            return (null, $"`{reference.Symbol.Name}` comes with nt65 and cannot be renamed; bring it in under a name of your own with `.use ... as`");
        if (CheckNewName(program.Current(reference.Symbol), newName, reference.IsAlias ? model.FileScope : null) is { } problem)
            return (null, problem);

        // An exported name appears in every file that uses it, so the edit spans the
        // program rather than the file the caret is in.
        var edits = new Dictionary<string, IReadOnlyList<Protocol.TextEdit>>(StringComparer.Ordinal);
        foreach (var byFile in Renamed(program, model, reference).GroupBy(found => found.File))
        {
            edits[Uris.ToUri(byFile.Key.Tree.Path)] =
                [.. byFile.Select(found => new Protocol.TextEdit(
                    Lsp.ToRange(byFile.Key.Tree, found.Reference.Span), newName))];
        }
        return (new Protocol.WorkspaceEdit(edits), null);
    }

    /// <summary>
    /// Checks whether a reference names something that a module that comes with nt65 declares,
    /// which no edit can rename. An alias given with <c>as</c> is the program's own, and can be
    /// renamed.
    /// </summary>
    private static bool IsStandard(SymbolReference reference) =>
        !reference.IsAlias && StandardModules.IsStandard(reference.Symbol.Tree.Path);

    /// <summary>
    /// Returns why <paramref name="newName"/> is not acceptable, or null when it is, since a
    /// rename that leaves the file not compiling is not a rename. <paramref name="alias"/> is the
    /// top level of the module whose <c>.use ... as</c> name is being renamed, where the new name
    /// must be free instead.
    /// </summary>
    private static string? CheckNewName(Symbol symbol, string newName, Scope? alias)
    {
        var name = newName;
        if (symbol.IsCheapLocal)
        {
            if (!name.StartsWith('@'))
                return $"`{symbol.DisplayName}` is a cheap local, so its new name must start with `@`";
            name = name[1..];
        }
        else if (name.StartsWith('@'))
        {
            return $"`{symbol.DisplayName}` is not a cheap local, so its new name may not start with `@`";
        }

        if (name.Length == 0 || !SyntaxFacts.IsIdentifierStart(name[0]) || !name.All(SyntaxFacts.IsIdentifierPart))
            return $"`{newName}` is not a name: names are a letter or `_` followed by letters, digits and `_`";
        // A mnemonic may be used as a name (it draws a warning, not an error), so the only
        // reserved words a rename refuses are register names.
        if (!symbol.IsCheapLocal && SyntaxFacts.IsRegister(name))
            return $"`{newName}` is a register name and cannot be used as a name";

        var taken = symbol.IsCheapLocal ? symbol.Scope.FindCheapLocal(name) : (alias ?? symbol.Scope).FindMember(name);
        return taken is null || taken == symbol ? null : $"`{newName}` is already declared in this scope";
    }

    /// <summary>
    /// Returns the references a rename of <paramref name="reference"/> replaces. An alias from
    /// <c>.use ... as</c> appears in place of the symbol's own name, so it is renamed separately.
    /// Renaming the symbol leaves aliases alone, and renaming an alias renames only the uses in
    /// this file that use that same alias.
    /// </summary>
    private static IEnumerable<(SemanticModel File, SymbolReference Reference)> Renamed(
        ProgramModel program, SemanticModel model, SymbolReference reference)
    {
        // A model kept from before an edit elsewhere still refers to the symbols the edited file
        // declared then, so symbols are compared by their current versions.
        var symbol = program.Current(reference.Symbol);
        if (symbol.Bound?.Value.Member is { } member)
            symbol = member;
        if (reference.IsAlias)
        {
            var aliasText = model.Tree.Text.Substring(reference.Span.Start, reference.Span.Length);
            return model.References
                .Where(other => other.IsAlias && program.Current(other.Symbol) == symbol
                    && model.Tree.Text.Substring(other.Span.Start, other.Span.Length) == aliasText)
                .Select(other => (model, other));
        }

        // A family instance may be named after an enum member, so renaming either one renames
        // the member and every use of every instance named after it.
        var renamed = new HashSet<Symbol> { symbol };
        if (symbol.IsEnumMember)
        {
            foreach (var file in program.Files)
                renamed.UnionWith(file.Symbols.Where(instance => instance.Bound?.Value.Member == symbol));
        }
        return program.ReferencesTo(renamed).Where(found => !found.Reference.IsAlias);
    }
}
