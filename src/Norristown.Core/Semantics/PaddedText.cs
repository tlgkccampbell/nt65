using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// A counted <c>.byte</c> array whose only value is one text, <c>.data title: .byte[21] { "NT65" }</c>,
/// which is filled out with zero to its count, as <c>char title[21] = "..."</c> is in C. A
/// short list of values is still the error it always was: a count is there to catch a short
/// table, and a text is not a table. A pad other than zero is what a structure's
/// <c>.res n, pad</c> member is for.
/// </summary>
public static class PaddedText
{
    /// <summary>
    /// How many zero bytes the directive pads with and how many elements it declares, or null
    /// when it is not one text in a counted <c>.byte</c> array — a text as long as its count,
    /// or longer, among them.
    /// </summary>
    public static (long Zeros, long Count)? Padding(SyntaxNode directive, SemanticModel model, Expansion? on = null)
    {
        if (DataSyntax.NameOf(directive) != ".byte"
            || DataSyntax.CountExpressionOf(directive) is not { } written
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
    /// The one value the directive holds, wherever it is written, or null when it holds any
    /// other number of them.
    /// </summary>
    private static SyntaxNode? OnlyValueOf(SyntaxNode directive)
    {
        if (DataSyntax.BracedOf(directive) is { Kind: SyntaxKind.ValueList } list)
            return list.ChildNodes is [var braced] ? braced : null;
        if (DataSyntax.BodyOf(directive) is not { } body)
            return null;
        SyntaxNode? only = null;
        foreach (var line in body.ChildNodes.Skip(1))
        {
            // A conditional or a repetition in the body writes values of its own, however few
            // its lines look like, so a body holding one is never the one-text case.
            if (line.Green is GreenBlock)
                return null;
            if (line.Statement is not { Kind: SyntaxKind.DataValues } values)
                continue;
            if (only is not null || values.ChildNodes is not [var written])
                return null;
            only = written;
        }
        return only;
    }

    /// <summary>
    /// Whether a value is text: a string, a text constant, or a character mapping applied to
    /// one. A character is a number, and a number is a table of one.
    /// </summary>
    private static bool IsText(SyntaxNode value, SemanticModel model, Expansion? on)
    {
        if (value is { Kind: SyntaxKind.CallExpression, ChildNodes: [{ Kind: SyntaxKind.NameExpression } callee, var given] }
            && model.SymbolOf(callee, on) is { Kind: SymbolKind.Charmap })
        {
            return given.ChildNodes is [var text] && IsText(text, model, on);
        }
        return model.ValueOf(value, on).Kind == ValueKind.String;
    }
}
