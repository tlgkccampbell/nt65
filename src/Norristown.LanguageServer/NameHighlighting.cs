using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Computes the semantic tokens of a file, which classify every name by what it refers to. The
/// TextMate grammar colours what the lexer knows, and these tokens are drawn over it where the
/// analysis knows more. A member spelled like a register, such as <c>Joy::A</c> or an enum's
/// <c>X</c>, is therefore coloured as the member it is. A name that refers to nothing keeps the
/// grammar's colour.
/// </summary>
internal static class NameHighlighting
{
    private const int Declaration = 1;
    private const int ReadOnly = 2;

    /// <summary>
    /// The token types and modifiers as the client is told them. The numbers in the tokens index
    /// these.
    /// </summary>
    public static readonly Protocol.SemanticTokensLegend Legend = new(
        ["namespace", "type", "enum", "struct", "enumMember", "property", "function", "macro", "parameter", "variable", "label"],
        ["declaration", "readonly"]);

    /// <summary>
    /// Returns the tokens for the names in <paramref name="model"/>'s file, encoded as the protocol
    /// requires. When the client asked about part of a long file, only the names on lines
    /// <paramref name="first"/> to <paramref name="last"/> are included.
    /// </summary>
    public static Protocol.SemanticTokens In(SemanticModel model, int first = 0, int last = int.MaxValue)
    {
        var tree = model.Tree;
        var data = new List<int>();
        var (line, character, end) = (0, 0, -1);

        // A bare word a condition compares a parameter with is one of a fixed set of words the
        // parameter accepts, much as a member is one of its enum's, so it is coloured as one.
        var words = ComparedWords.In(model)
            .Select(word => (Span: word.Word.Span, Type: IndexOf("enumMember"), Modifiers: ReadOnly));
        var names = model.References.Select(reference =>
        {
            var (type, modifiers) = Classify(reference.Symbol);
            return (reference.Span, Type: type, Modifiers: modifiers | (reference.IsDeclaration ? Declaration : 0));
        });

        // A module is not a symbol, but the module path in a `.place` is still coloured as a
        // namespace.
        var modules = tree.Root.DescendantNodes().OfType<PlaceDirectiveSyntax>()
            .SelectMany(place => place.Name.Names)
            .Where(part => !part.IsMissing)
            .Select(part => (part.Span, Type: IndexOf("namespace"), Modifiers: 0));
        // Names outside the lines asked about are dropped before sorting, since a range request
        // for a screenful of a long file would otherwise sort and place every name in the file.
        var tokens = names.Concat(words).Concat(modules);
        if (first > 0 || last < int.MaxValue)
        {
            tokens = tokens.Where(token => tree.GetLineIndex(token.Span.Start) is var at
                && at >= first && at <= last);
        }
        foreach (var (span, type, modifiers) in tokens.OrderBy(token => token.Span.Start))
        {
            // A name in a macro body is recorded once per expansion; emit its token only once.
            if (span.Start < end || span.Length == 0)
                continue;
            var at = Lsp.ToPosition(tree, span.Start);
            if (at.Line < first || at.Line > last)
                continue;
            data.AddRange([
                at.Line - line,
                at.Line == line ? at.Character - character : at.Character,
                span.Length,
                type,
                modifiers]);
            (line, character, end) = (at.Line, at.Character, span.End);
        }
        return new Protocol.SemanticTokens(data);
    }

    /// <summary>
    /// Returns the difference between the tokens the client holds and the current ones, as the
    /// single run of numbers that changed, found by trimming the common prefix and suffix. An edit
    /// in one place changes a handful of numbers in a file of thousands, and the delta form exists
    /// so that the thousands are not resent.
    /// </summary>
    /// <param name="id">
    /// The result id of the new result, which the client quotes when requesting the next delta.
    /// </param>
    /// <param name="before">The numbers the client holds.</param>
    /// <param name="after">The numbers the client would be given now.</param>
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
        var inserted = after.Skip(head).Take(after.Count - head - tail).ToList();
        return new Protocol.SemanticTokensDelta(
            id,
            removed == 0 && inserted.Count == 0
                ? []
                : [new Protocol.SemanticTokensEdit(head, removed, inserted.Count == 0 ? null : inserted)]);
    }

    private static (int Type, int Modifiers) Classify(Symbol symbol)
    {
        // A function's parameters are constants in its scope, given a value by each call; they
        // are coloured as parameters.
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
