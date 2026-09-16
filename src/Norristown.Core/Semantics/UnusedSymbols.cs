using System.Text.RegularExpressions;

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
            yield return new Diagnostic(symbol.DeclarationSpan, Severity.Warning,
                $"`{symbol.DisplayName}` is never used: nothing names it, and it is not exported");
        }
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
    /// and a routine that nothing names is reported by flow analysis as never reached.
    /// </summary>
    private static bool IsChecked(Symbol symbol) => !symbol.IsDefine && symbol.Kind switch
    {
        SymbolKind.Label or SymbolKind.Macro or SymbolKind.Enum or SymbolKind.Struct or SymbolKind.Union => true,
        SymbolKind.Constant => symbol.Scope.Kind != ScopeKind.Type,
        _ => false,
    };
}
