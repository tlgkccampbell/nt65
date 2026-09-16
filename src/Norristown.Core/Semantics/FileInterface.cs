using System.Text;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What the other files of a program can see of one file, symbol by symbol, written out so that
/// two versions of it can be compared. A symbol whose entry changed is news only to the files
/// that looked its name up.
/// <para>
/// Every name a path can reach is in it, exported or not, because another file resolving a
/// name finds a private one too, to say that it is not exported. Each entry says what the
/// symbol means — its value, its sizes, its signature, a macro's body — and almost nothing is
/// a position: a line added above a declaration moves it without changing what it is. The one
/// exception is where a macro's body is: an expansion names each call it writes out by file
/// and line, and a problem with a body line is reported at the call with the line beside it,
/// so where the body is is part of what every caller writes and says.
/// </para>
/// <para>
/// A body is read where it is used rather than where it is written: a macro expands, and a
/// function or a list is evaluated, in the file that names it. So an entry with a body also
/// says what every name in the body means, since a change to one of those is a change to what
/// the body does wherever it lands, even when the file using it never wrote the name. A type
/// is laid out where it is used in the same way, and written out member by member, so its
/// entry says what each of its members is.
/// </para>
/// </summary>
internal static class FileInterface
{
    /// <summary>
    /// The interface of a file that declares <paramref name="symbols"/>, by qualified name.
    /// <paramref name="isExported"/> says what its <c>.export</c> items make visible, and
    /// <paramref name="resolved"/> what the names written in it mean.
    /// </summary>
    public static Dictionary<string, string> Of(
        IEnumerable<Symbol> symbols,
        Func<Symbol, bool> isExported,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var symbol in symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var text = new StringBuilder($"{symbol.Kind}{(isExported(symbol) ? " exported" : "")}\n");
            Describe(symbol, resolved, text, [], "  ");
            entries.TryAdd(symbol.QualifiedName, text.ToString());
        }
        return entries;
    }

    /// <summary>
    /// The names whose entries differ between <paramref name="before"/> and
    /// <paramref name="after"/>, including those only one of them has.
    /// </summary>
    public static IEnumerable<string> Changed(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .Where(name => before.GetValueOrDefault(name) != after.GetValueOrDefault(name));

    private static void Describe(
        Symbol symbol,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved,
        StringBuilder text,
        HashSet<Symbol> described,
        string indent)
    {
        // Each symbol is described once in an entry, wherever else it is named again: two bodies
        // that name each other are still one finite entry, and so is a long chain of constants.
        if (!described.Add(symbol))
            return;

        text.Append($"{indent}= {symbol.Value} {symbol.AddressSize} {symbol.Segment} {symbol.Size} {symbol.Count} {symbol.IsDefine}\n");
        if (symbol.Type is { } type)
            text.Append($"{indent}type {type.Tree.Path} {type.QualifiedName}\n");
        if (symbol.IsLayout || symbol.Kind == SymbolKind.Enum)
        {
            foreach (var member in symbol.Body?.Symbols ?? [])
            {
                text.Append($"{indent}member {member.Name}\n");
                Describe(member, resolved, text, described, indent + "  ");
            }
        }
        if (symbol is { Kind: SymbolKind.Macro, Definition: { } body })
            text.Append($"{indent}at line {body.LineIndex + 1}\n");
        if (symbol.Signature is { } signature)
            text.Append($"{indent}signature {Spell(signature)}\n");
        if (symbol.MacroSignature is { } macroSignature)
            text.Append($"{indent}macro signature {Spell(macroSignature)}\n");
        foreach (var parameter in symbol.Parameters)
            text.Append($"{indent}parameter {parameter.Symbol.Name} {parameter.Accepts} {parameter.Default?.GetText()} {parameter.Empty}\n");
        foreach (var parameter in symbol.ParameterSymbols)
            text.Append($"{indent}parameter {parameter.Name}\n");
        foreach (var (callee, _) in symbol.Calls)
            text.Append($"{indent}calls {callee.Tree.Path} {callee.QualifiedName}\n");
        foreach (var (used, _) in symbol.Uses)
            text.Append($"{indent}uses {used.Tree.Path} {used.QualifiedName}\n");

        var written = new[] { symbol.ValueExpression, symbol.Data, symbol.StateDeclaration, symbol.TypeExpression, symbol.Definition }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .Concat(symbol.Parameters.Select(parameter => parameter.Default))
            .OfType<SyntaxNode>()
            .ToList();
        foreach (var node in written)
            text.Append($"{indent}{node.Kind} {node.GetText()}\n");
        foreach (var node in written.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                if (!resolved.TryGetValue((node.Tree, token.Span.Start), out var named) || named == symbol)
                    continue;
                text.Append($"{indent}names {named.Tree.Path} {named.QualifiedName}\n");
                Describe(named, resolved, text, described, indent + "  ");
            }
        }
    }

    // A signature is compared by what it declares, not by the syntax it was read from.
    private static string Spell(Signature signature) =>
        $"{signature.Entry} -> {signature.Exit} {signature.Distance} {signature.Inline?.Text}";
}
