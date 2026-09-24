using System.Text;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Builds a text description, symbol by symbol, of what the other files of a program can see of
/// one file, so that two versions of it can be compared. A change to a symbol's entry matters
/// only to the files that looked its name up.
/// <para>
/// The interface includes every name a path can reach, exported or not, because another file
/// resolving a name also finds a private one, in order to report that it is not exported. Each
/// entry describes what the symbol means, such as its value, its sizes, its signature or a
/// macro's body. Almost nothing in an entry is a position, because a line added above a
/// declaration moves it without changing what it is.
/// </para>
/// <para>
/// The one exception is the location of a macro's body. An expansion names each call it emits by
/// file and line, and a problem with a body line is reported at the call with the body line
/// beside it. The body's location is therefore part of every caller's output and diagnostics.
/// </para>
/// <para>
/// A body is interpreted where it is used, not where it is declared: a macro expands, and a
/// function or a list is evaluated, in the file that names it. An entry with a body therefore
/// also describes what every name in the body means. A change to one of those names changes what
/// the body does wherever it lands, even when the file using the body never names it. A type is
/// likewise laid out where it is used, and described member by member, so its entry describes
/// each of its members.
/// </para>
/// </summary>
internal static class FileInterface
{
    /// <summary>
    /// Returns the interface of <paramref name="module"/>, a file that declares
    /// <paramref name="symbols"/>, keyed by qualified name. <paramref name="resolved"/> maps each
    /// name in the file to the symbol it refers to. The module's name and its re-exports are
    /// entries too. Their keys start with a space so that no symbol's qualified name can match
    /// them.
    /// </summary>
    public static Dictionary<string, string> Of(
        ProgramSymbols.Module module,
        IEnumerable<Symbol> symbols,
        IReadOnlyDictionary<(SyntaxTree Tree, int Position), Symbol> resolved)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal) { [" module"] = module.Name ?? "" };
        foreach (var reexport in module.Reexports)
            entries.TryAdd(" reexport " + reexport.Name, string.Join("::", reexport.Path));
        foreach (var symbol in symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var text = new StringBuilder($"{symbol.Kind}{(symbol.IsExported ? $" exported {symbol.LinkerName} {symbol.ExportSize}" : "")}\n");
            Describe(symbol, resolved, text, [], "  ");
            entries.TryAdd(symbol.QualifiedName, text.ToString());
        }
        return entries;
    }

    /// <summary>
    /// Returns a value indicating whether a change to <paramref name="entry"/> can change what
    /// every path in the program means, not just what one name means. Such entries are the
    /// module's name and the names it re-exports.
    /// </summary>
    public static bool IsModuleWide(string entry) => entry.StartsWith(' ');

    /// <summary>
    /// Returns the names whose entries differ between <paramref name="before"/> and
    /// <paramref name="after"/>, including names that only one of them has.
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
        // Each symbol is described only once in an entry, even if it is named again. Two bodies
        // that name each other still produce one finite entry, and so does a long chain of
        // constants.
        if (!described.Add(symbol))
            return;

        text.Append($"{indent}= {symbol.Value} {symbol.AddressSize} {symbol.Segment} {symbol.Size} {symbol.Count}\n");
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
            text.Append($"{indent}signature {Format(signature)}\n");
        if (symbol.MacroSignature is { } macroSignature)
            text.Append($"{indent}macro signature {Format(macroSignature)}\n");
        foreach (var parameter in symbol.Parameters)
            text.Append($"{indent}parameter {parameter.Symbol.Name} {parameter.Accepts} {parameter.Default?.GetText()} {parameter.Empty}\n");
        foreach (var parameter in symbol.ParameterSymbols)
            text.Append($"{indent}parameter {parameter.Name}\n");
        foreach (var (callee, _) in symbol.Calls)
            text.Append($"{indent}calls {callee.Tree.Path} {callee.QualifiedName}\n");
        foreach (var (used, _) in symbol.Uses)
            text.Append($"{indent}uses {used.Tree.Path} {used.QualifiedName}\n");

        var declaredNodes = new[] { symbol.ValueExpression, symbol.Data, symbol.StateDeclaration, symbol.TypeExpression, symbol.Definition }
            .Concat(symbol.Items)
            .Concat(symbol.Entries)
            .Concat(symbol.Parameters.Select(parameter => parameter.Default))
            .OfType<SyntaxNode>()
            .ToList();
        foreach (var node in declaredNodes)
            text.Append($"{indent}{node.Kind} {node.GetText()}\n");
        foreach (var node in declaredNodes.SelectMany(node => node.DescendantNodes().Prepend(node)))
        {
            foreach (var token in node.ChildTokens)
            {
                // A missing token starts where the token after it does, so looking up a missing
                // token would find the name at that position.
                if (token.IsMissing
                    || !resolved.TryGetValue((node.Tree, token.Span.Start), out var named) || named == symbol)
                {
                    continue;
                }
                text.Append($"{indent}names {named.Tree.Path} {named.QualifiedName}\n");
                Describe(named, resolved, text, described, indent + "  ");
            }
        }
    }

    // A signature is compared by what it declares, not by the syntax it was read from.
    private static string Format(Signature signature) =>
        $"{signature.Entry} -> {signature.Exit} {signature.Distance} {signature.Inline?.Text} {signature.Arguments} {signature.NeverReturns} {signature.Keeps}";
}
