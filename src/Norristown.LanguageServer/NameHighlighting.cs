using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Semantic tokens: every name in a file, classified by what it refers to. The TextMate grammar
/// colours what the lexer knows, and these are drawn over it where the analysis knows more, so a
/// member spelled like a register, <c>Joy::A</c> or an enum's <c>X</c>, is coloured as the member
/// it is. A name that refers to nothing keeps the grammar's colour.
/// </summary>
internal static class NameHighlighting
{
    private const int Declaration = 1;
    private const int ReadOnly = 2;

    /// <summary>The token types and modifiers, as the client is told them; the numbers index these.</summary>
    public static readonly Protocol.SemanticTokensLegend Legend = new(
        ["namespace", "type", "enum", "struct", "enumMember", "property", "function", "macro", "parameter", "variable", "label"],
        ["declaration", "readonly"]);

    /// <summary>The names written in <paramref name="model"/>'s file, encoded as the protocol wants them.</summary>
    public static Protocol.SemanticTokens In(SemanticModel model)
    {
        var tree = model.Tree;
        var data = new List<int>();
        var (line, character, end) = (0, 0, -1);
        foreach (var reference in model.References.OrderBy(reference => reference.Span.Start))
        {
            // A macro body's names are recorded for each of its uses; each is written once.
            if (reference.Span.Start < end || reference.Span.Length == 0)
                continue;
            var at = Lsp.ToPosition(tree, reference.Span.Start);
            var (type, modifiers) = Classify(reference.Symbol);
            data.AddRange([
                at.Line - line,
                at.Line == line ? at.Character - character : at.Character,
                reference.Span.Length,
                type,
                modifiers | (reference.IsDeclaration ? Declaration : 0)]);
            (line, character, end) = (at.Line, at.Character, reference.Span.End);
        }
        return new Protocol.SemanticTokens(data);
    }

    private static (int Type, int Modifiers) Classify(Symbol symbol)
    {
        // A function's parameters are constants in its scope, which each call gives a value.
        var (type, modifiers) = symbol.IsEnumMember ? ("enumMember", ReadOnly)
            : symbol.Scope.Owner is { Kind: SymbolKind.Func } ? ("parameter", 0)
            : symbol.Kind switch
            {
                SymbolKind.Label => ("label", 0),
                SymbolKind.Proc or SymbolKind.ExternProc or SymbolKind.Func => ("function", 0),
                SymbolKind.Macro => ("macro", 0),
                SymbolKind.MacroParameter => ("parameter", 0),
                SymbolKind.Scope => ("namespace", 0),
                SymbolKind.Enum => ("enum", 0),
                SymbolKind.Struct or SymbolKind.Union => ("struct", 0),
                SymbolKind.Member => ("property", 0),
                SymbolKind.Charmap or SymbolKind.SignatureSet => ("type", 0),
                SymbolKind.Constant or SymbolKind.ImportedConstant or SymbolKind.Binding => ("variable", ReadOnly),
                _ => ("variable", 0),
            };
        return (IndexOf(type), modifiers);
    }

    private static int IndexOf(string type)
    {
        for (var i = 0; i < Legend.TokenTypes.Count; i++)
        {
            if (Legend.TokenTypes[i] == type)
                return i;
        }
        throw new ArgumentException($"`{type}` is not in the legend", nameof(type));
    }
}
