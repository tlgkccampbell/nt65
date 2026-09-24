using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Finds the labels, constants, macros and types a file declares that nothing names. An export
/// names what it exports, so a symbol another file can use is never reported. Neither is a
/// symbol another file names without the export, because that is already reported there.
/// <para>
/// A name that appears in a branch this build leaves out counts as used, because the other
/// build uses it, and a warning that comes and goes with a define is noise. A file with errors
/// gets none of these warnings, because a name may look unused only because the broken lines
/// meant to use it did not parse or resolve.
/// </para>
/// </summary>
public static class UnusedSymbols
{
    /// <summary>
    /// Reports a diagnostic for each symbol <paramref name="model"/> declares and never names.
    /// <paramref name="found"/> holds what the rest of the file's analysis reported, so a label
    /// already reported as never reached is not reported a second time.
    /// <paramref name="entries"/> are the labels a <c>.state</c> declares as entry points, which
    /// are reached from somewhere nt65 cannot see.
    /// </summary>
    public static IEnumerable<Diagnostic> Of(
        SemanticModel model, IReadOnlyList<Diagnostic> found, IEnumerable<Symbol> entries)
    {
        if (model.Tree.Diagnostics.Concat(model.Diagnostics).Concat(found).Any(d => d.Severity == Severity.Error))
            yield break;

        var named = model.References.Where(reference => !reference.IsDeclaration)
            .Select(reference => reference.Symbol)
            .ToHashSet();
        named.UnionWith(entries);
        named.UnionWith(model.Symbols.Where(symbol => symbol.IsExported));

        // A name another file uses without this one exporting it is used, though wrongly. That
        // file reports that the name is not exported. Reporting here that nothing uses it would
        // report the same mistake twice, with the fix for one the opposite of the fix for the
        // other.
        named.UnionWith(model.Symbols.Where(symbol => model.NamedUnexported.Contains(symbol.QualifiedName)));

        // `actions::c`, where `c` iterates over an enum, names a different member of `actions`
        // in every iteration, so everything that scope holds counts as named.
        for (var i = 1; i < model.References.Count; i++)
        {
            if (model.References[i] is { IsDeclaration: false, Symbol.Kind: SymbolKind.Binding } through
                && AfterColonColon(model.Tree.Text, through.Span.Start)
                && model.References[i - 1].Symbol.Body is { } container)
            {
                named.UnionWith(container.Symbols);
            }
        }

        // A family declares one name per member of an enum, and each instance is one of a set.
        // It is worth reporting only when nothing names any instance, and then only once.
        foreach (var family in model.Families)
            named.UnionWith(family.Instances.Any(named.Contains) ? family.Instances : family.Instances.Skip(1));

        var reported = found.Where(d => d.Severity == Severity.Warning).Select(d => d.Span).ToHashSet();
        var omitted = model.Configuration.Omitted(model.Tree)
            .Select(span => model.Tree.Text.Substring(span.Start, span.Length))
            .ToList();

        foreach (var symbol in model.Symbols)
        {
            if (!IsChecked(symbol) || named.Contains(symbol) || reported.Contains(symbol.DeclarationSpan))
                continue;
            if (omitted.Count > 0 && omitted.Any(new Regex($@"(?<![\w@.]){Regex.Escape(symbol.DisplayName)}(?!\w)").IsMatch))
                continue;
            yield return new Diagnostic(symbol.DeclarationSpan,
                Catalogue.UnusedSymbol.Message(symbol.DisplayName))
            {
                Fix = new DiagnosticFix(FixKind.Unused, symbol.DisplayName),
                IsUnnecessary = true,
            };
        }

        foreach (var brought in Unused(model))
            yield return brought;
    }

    /// <summary>
    /// Reports a diagnostic for each <c>.use</c> item that brings in a name the file never uses.
    /// A <c>.export .use</c> is not reported, because it re-exports rather than uses, and whether
    /// anything uses the name is up to other modules. A <c>.use module::*</c> is not reported
    /// either, because it brings in everything that module exports, and which of those names
    /// this file wanted cannot be told from the file.
    /// <para>
    /// A name that appears in a branch this build leaves out counts as used, as for a
    /// declaration, because the lines of that branch are in the file and the other build emits
    /// them.
    /// </para>
    /// </summary>
    private static IEnumerable<Diagnostic> Unused(SemanticModel model)
    {
        var tree = model.Tree;
        var items = model.Brought
            .Where(pair => pair.Value is { IsExported: false, At.Length: > 0 })
            .OrderBy(pair => pair.Value.At.Start)
            .ToList();
        if (items.Count == 0)
            yield break;

        var used = NamesIn(tree);
        foreach (var (name, brought) in items)
        {
            if (used.Contains(name))
                continue;
            yield return new Diagnostic(tree.GetSpan(brought.At),
                Catalogue.UnusedUseItem.Message(name))
            {
                Fix = new DiagnosticFix(FixKind.UseItem, name),
                IsUnnecessary = true,
            };
        }
    }

    /// <summary>
    /// Returns every name the file contains outside the <c>.use</c> items themselves, standing
    /// on its own rather than as a later part of a path, which is how a name brought in is used.
    /// One pass over the file serves every item, because all of a file's items are checked
    /// together.
    /// <para>
    /// This method reads each line's own tokens rather than the nodes they parse to, and is
    /// lexical on purpose, because binding cannot answer the question. Binding stops at a branch
    /// the build leaves out without reading any of its lines, yet a name there counts as used. A
    /// name in a position where a bare word may stand is never looked up. A <c>.defined</c> asks
    /// about a name without naming it. A reference records the symbol it reached rather than the
    /// spelling used, so it cannot tell <c>b</c> written as <c>a::b</c> from the <c>c</c> that
    /// <c>.use a::b as c</c> brought in. The tokens are what all of these cases have in common.
    /// </para>
    /// </summary>
    private static HashSet<string> NamesIn(SyntaxTree tree)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tree.LineCount; index++)
        {
            var line = tree.GetLine(index);
            if (line.Statement.Kind == SyntaxKind.UseDirective)
                continue;

            // A token counts as a name standing on its own unless the token before it is `::`,
            // which makes it a later part of a path.
            var reached = false;
            foreach (var token in line.Tokens)
            {
                if (!reached)
                    names.Add(token.Text);
                reached = token.Kind == SyntaxKind.ColonColon;
            }
        }
        return names;
    }

    /// <summary>
    /// Returns a value indicating whether the text before <paramref name="position"/>, ignoring
    /// whitespace, ends in <c>::</c>.
    /// </summary>
    private static bool AfterColonColon(string text, int position)
    {
        var i = position - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;
        return i >= 1 && text[i] == ':' && text[i - 1] == ':';
    }

    /// <summary>
    /// Returns a value indicating whether an unused symbol like <paramref name="symbol"/> is worth
    /// reporting. A member of a named enum is one of a set, so it is not, and a setting is there
    /// for the build to set, so it is not either. Data that holds values may exist for where it
    /// lands, such as a header, the vectors or a load address, so only storage that holds nothing
    /// is reported.
    /// <para>
    /// A routine is reported like anything else a file declares. An unexported <c>.proc</c> that
    /// nothing calls, jumps to, or names in data, a <c>.next</c> or a <c>.fallthrough</c> is a
    /// routine the program has left behind, which is what someone finishing a port most wants to
    /// find. An interrupt handler is the exception the language already knows about. The
    /// processor reaches it through a vector this program may not even contain, so an
    /// <c>interrupt</c> signature counts as naming it.
    /// </para>
    /// </summary>
    private static bool IsChecked(Symbol symbol) => symbol.Kind switch
    {
        SymbolKind.Label or SymbolKind.Macro or SymbolKind.Enum or SymbolKind.Struct or SymbolKind.Union => true,
        SymbolKind.Proc => symbol.Signature is not { IsInterrupt: true },
        SymbolKind.Constant => symbol.Scope.Kind != ScopeKind.Type && !symbol.IsSetting,
        SymbolKind.Data => symbol.Data is DataDirectiveSyntax element && DataSyntax.IsElementType(element)
            && element.Tail is not (InlineDataSyntax or BracedDataSyntax)
            && DataSyntax.BodyOf(element) is null,
        _ => false,
    };
}
