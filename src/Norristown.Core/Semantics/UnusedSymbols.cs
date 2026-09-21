using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The labels, constants, macros and types a file declares and nothing names. An export
/// names what it exports, so what another file can use is never reported.
/// <para>
/// A name written in a branch this build leaves out counts as used: the other build uses it,
/// and a warning that comes and goes with a define is noise. A file with errors gets none
/// of these, because a name its broken lines were meant to use is not news.
/// </para>
/// </summary>
public static class UnusedSymbols
{
    /// <summary>
    /// What <paramref name="model"/> declares and never names. <paramref name="found"/> is what
    /// the rest of the file's analysis reported: a label it already called never reached is
    /// not reported a second time. <paramref name="entries"/> are the labels a <c>.state</c>
    /// declares entry points, which are reached from somewhere nt65 cannot see.
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

        // `actions::c`, where `c` walks an enum, names a member of `actions` on every turn,
        // so what that scope holds counts as named.
        for (var i = 1; i < model.References.Count; i++)
        {
            if (model.References[i] is { IsDeclaration: false, Symbol.Kind: SymbolKind.Binding } through
                && AfterColonColon(model.Tree.Text, through.Span.Start)
                && model.References[i - 1].Symbol.Body is { } container)
            {
                named.UnionWith(container.Symbols);
            }
        }

        // A family declares one name per member of an enum, and a member is one of a set: it
        // is worth saying only when nothing names any instance, and then only once.
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
                Catalogue.UnusedSymbol.Says(symbol.DisplayName))
            {
                Fix = new DiagnosticFix(FixKind.Unused, symbol.DisplayName),
                IsUnnecessary = true,
            };
        }

        foreach (var brought in Unused(model))
            yield return brought;
    }

    /// <summary>
    /// The <c>.use</c> items that bring in a name the file never writes. A <c>.export .use</c>
    /// re-exports rather than uses, and what it is for is another module's business; a
    /// <c>.use module::*</c> brings in whatever that module exports, and what of it this file
    /// wanted is not a question about this file.
    /// <para>
    /// A name written in a branch this build leaves out counts as used, as a declaration's does,
    /// because the lines of that branch are in the file and are what the other build writes.
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

        var written = Written(tree);
        foreach (var (name, brought) in items)
        {
            if (written.Contains(name))
                continue;
            yield return new Diagnostic(tree.GetSpan(brought.At),
                Catalogue.UnusedUseItem.Says(name))
            {
                Fix = new DiagnosticFix(FixKind.UseItem, name),
                IsUnnecessary = true,
            };
        }
    }

    /// <summary>
    /// Every name the file writes outside the <c>.use</c> items themselves, and on its own
    /// rather than as a step on a path, which is what a name brought in is written as. One pass
    /// answers for all of them, because a file with items to check has them all to check.
    /// <para>
    /// It reads each line's own tokens rather than the nodes they parse to, and it is lexical on
    /// purpose. Binding cannot answer this: it returns at a branch the build leaves out without
    /// reading a line of it, and a name written there counts as used; a name written where a bare
    /// word may stand is never looked up; a <c>.defined</c> asks about a name without naming it;
    /// and a reference records the symbol it reached rather than the spelling it was written as,
    /// so it cannot tell <c>b</c> written as <c>a::b</c> from the <c>c</c> that
    /// <c>.use a::b as c</c> brought in. The tokens are what every one of those has in common.
    /// </para>
    /// </summary>
    private static HashSet<string> Written(SyntaxTree tree)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tree.LineCount; index++)
        {
            var line = tree.GetLine(index);
            if (line.Statement.Kind == SyntaxKind.UseDirective)
                continue;

            // A name is the file's own where nothing but a `::` before it makes it a step on
            // somebody else's path.
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

    /// <summary>Whether the text before <paramref name="position"/> ends in <c>::</c>.</summary>
    private static bool AfterColonColon(string text, int position)
    {
        var i = position - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
            i--;
        return i >= 1 && text[i] == ':' && text[i - 1] == ':';
    }

    /// <summary>
    /// Whether an unused one is worth saying so. A member of a named enum is one of a set,
    /// and a routine that nothing names is reported by flow analysis as never reached. Data
    /// that holds values may be there for where it lands — a header, the vectors, a load
    /// address — so only storage that holds nothing is reported.
    /// </summary>
    private static bool IsChecked(Symbol symbol) => !symbol.IsDefine && symbol.Kind switch
    {
        SymbolKind.Label or SymbolKind.Macro or SymbolKind.Enum or SymbolKind.Struct or SymbolKind.Union => true,
        SymbolKind.Constant => symbol.Scope.Kind != ScopeKind.Type,
        SymbolKind.Data => symbol.Data is DataDirectiveSyntax element && DataSyntax.IsElementType(element)
            && element.Tail is not (InlineDataSyntax or BracedDataSyntax)
            && DataSyntax.BodyOf(element) is null,
        _ => false,
    };
}
