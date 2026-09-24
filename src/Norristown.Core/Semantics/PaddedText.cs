using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Handles a counted <c>.byte</c> array whose only value is one text, such as
/// <c>.data title: .byte[21] { "NT65" }</c>. Such an array is padded with zeros to its count, as
/// <c>char title[21] = "..."</c> is in C. A list with fewer values than the count is still an
/// error, because a count exists to catch a short table, and a text is not a table. To pad with a
/// value other than zero, use a structure's <c>.res n, pad</c> member.
/// </summary>
public static class PaddedText
{
    /// <summary>
    /// Returns how many zero bytes the directive pads with and how many elements it declares.
    /// Returns null when the directive is not one text in a counted <c>.byte</c> array, or when
    /// the text is at least as long as its count.
    /// </summary>
    public static (long Zeros, long Count)? Padding(DataDirectiveSyntax directive, SemanticModel model, Expansion? on = null)
    {
        if (DataSyntax.NameOf(directive) != ".byte"
            || directive.Count?.Count is not { } written
            || model.ValueOf(written, on).AsNumber() is not { } count
            || OnlyValueOf(directive) is not { } value
            || !IsText(value, model, on)
            || model.BytesOf(value, on) is not { } bytes)
        {
            return null;
        }
        return bytes.Count < count ? (count - bytes.Count, count) : null;
    }

    /// <summary>
    /// Returns the directive's single value, whether it is in braces or in the body, or null when
    /// the directive holds any other number of values.
    /// </summary>
    private static SyntaxNode? OnlyValueOf(DataDirectiveSyntax directive)
    {
        if (directive.Tail is BracedDataSyntax { Value: ValueListSyntax list })
            return list.Values is [var braced] ? braced : null;
        if (DataSyntax.BodyOf(directive) is not { } body)
            return null;
        SyntaxNode? only = null;
        foreach (var line in body.Members.Skip(1))
        {
            // A conditional or a repetition in the body may produce any number of values, even
            // with few lines, so a body that contains one is never the one-text case.
            if (line is BlockSyntax)
                return null;
            if (line is not LineSyntax { Statement: DataValuesSyntax values })
                continue;
            if (only is not null || values.Values is not [var written])
                return null;
            only = written;
        }
        return only;
    }

    /// <summary>
    /// Returns a value indicating whether <paramref name="value"/> is text, which is a string, a
    /// text constant, or a character mapping applied to text. A character literal is a number,
    /// and a single number is a table of one, not text.
    /// </summary>
    private static bool IsText(SyntaxNode value, SemanticModel model, Expansion? on)
    {
        if (value is CallExpressionSyntax { Callee: { } callee } call
            && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap })
        {
            return call.Arguments.Arguments is [var text] && IsText(text, model, on);
        }
        return model.ValueOf(value, on).Kind == ValueKind.String;
    }
}
