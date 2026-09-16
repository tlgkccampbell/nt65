using System.Text;

namespace Norristown.Semantics;

/// <summary>
/// What the other files of a program can see of one file, written out so that two versions
/// of it can be compared. When an edit leaves it the same, no other file is analyzed again.
/// <para>
/// Every name a path can reach is in it, exported or not, because another file resolving a
/// name finds a private one too, to say that it is not exported. What a symbol means — its
/// value, its sizes, its signature, a macro's body — is in it for a name that is exported or
/// that another file named anyway; what a private name nobody else names means is the file's
/// own business. Nothing in it comes from a routine's body, and nothing is a position: a line
/// added above a declaration moves it without changing what it is.
/// </para>
/// </summary>
internal static class FileInterface
{
    /// <summary>
    /// The interface of a file that declares <paramref name="symbols"/>. <paramref name="isExported"/> says what its
    /// <c>.export</c> items make visible; <paramref name="namedElsewhere"/> holds the qualified
    /// names of its symbols that another file names.
    /// </summary>
    public static string Of(
        IEnumerable<Symbol> symbols, Func<Symbol, bool> isExported, IReadOnlySet<string> namedElsewhere)
    {
        var text = new StringBuilder();
        foreach (var symbol in symbols.Where(symbol => symbol.IsReachableByPath))
        {
            var exported = isExported(symbol);
            text.Append($"{symbol.QualifiedName} {symbol.Kind}{(exported ? " exported" : "")}\n");
            if (exported || namedElsewhere.Contains(symbol.QualifiedName))
                Describe(symbol, text);
        }
        return text.ToString();
    }

    private static void Describe(Symbol symbol, StringBuilder text)
    {
        text.Append($"  = {symbol.Value} {symbol.AddressSize} {symbol.Segment} {symbol.Size} {symbol.Count} {symbol.IsDefine}\n");
        if (symbol.Type is { } type)
            text.Append($"  type {type.Tree.Path} {type.QualifiedName}\n");
        if (symbol.Signature is { } signature)
            text.Append($"  signature {Spell(signature)}\n");
        if (symbol.MacroSignature is { } macroSignature)
            text.Append($"  macro signature {Spell(macroSignature)}\n");
        foreach (var node in new[] { symbol.ValueExpression, symbol.Data, symbol.StateDeclaration, symbol.Definition })
        {
            if (node is not null)
                text.Append($"  {node.Kind} {node.GetText()}\n");
        }
        foreach (var parameter in symbol.Parameters)
            text.Append($"  parameter {parameter.Symbol.Name} {parameter.Accepts} {parameter.Default?.GetText()} {parameter.Empty}\n");
        foreach (var parameter in symbol.ParameterSymbols)
            text.Append($"  parameter {parameter.Name}\n");
        foreach (var node in symbol.Items.Concat(symbol.Entries))
            text.Append($"  item {node.GetText()}\n");
        foreach (var (callee, _) in symbol.Calls)
            text.Append($"  calls {callee.Tree.Path} {callee.QualifiedName}\n");
        foreach (var (used, _) in symbol.Uses)
            text.Append($"  uses {used.Tree.Path} {used.QualifiedName}\n");
    }

    // A signature is compared by what it declares, not by the syntax it was read from.
    private static string Spell(Signature signature) =>
        $"{signature.Entry} -> {signature.Exit} {signature.Distance} {signature.Inline?.Text}";
}
