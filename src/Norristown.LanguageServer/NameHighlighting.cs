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

    /// <summary>
    /// The names written in <paramref name="model"/>'s file, encoded as the protocol wants
    /// them, or those of the lines <paramref name="first"/> to <paramref name="last"/> where
    /// the client asked about part of a long file.
    /// </summary>
    public static Protocol.SemanticTokens In(SemanticModel model, int first = 0, int last = int.MaxValue)
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
            if (at.Line < first || at.Line > last)
                continue;
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

    /// <summary>
    /// What changed between the answer the client is holding and the one it would be given
    /// now: the one run of numbers that moved, found from the ends inwards. An edit in one
    /// place moves a handful of numbers in a file of thousands, and sending the thousands
    /// again is what the change form exists to stop.
    /// </summary>
    /// <param name="id">What to call the new answer when asking for the next change to it.</param>
    /// <param name="before">The numbers the client holds.</param>
    /// <param name="after">The numbers it would be given now.</param>
    public static Protocol.SemanticTokensDelta Changed(
        string id, IReadOnlyList<int> before, IReadOnlyList<int> after)
    {
        var head = 0;
        while (head < before.Count && head < after.Count && before[head] == after[head])
            head++;
        var tail = 0;
        while (tail < before.Count - head && tail < after.Count - head
            && before[before.Count - 1 - tail] == after[after.Count - 1 - tail])
        {
            tail++;
        }
        var removed = before.Count - head - tail;
        var written = after.Skip(head).Take(after.Count - head - tail).ToList();
        return new Protocol.SemanticTokensDelta(
            id,
            removed == 0 && written.Count == 0
                ? []
                : [new Protocol.SemanticTokensEdit(head, removed, written.Count == 0 ? null : written)]);
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
