using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// How many bytes a data directive generates (§8, §7.6), and what its values must satisfy
/// for ca65 to accept them. String and character data becomes bytes in the output, so a
/// string's length is its byte count.
/// </summary>
public static class DataLengths
{
    /// <summary>
    /// The length of <paramref name="directive"/>, or null when it is a directive a later
    /// stage brings online. Anything wrong with its values goes to
    /// <paramref name="diagnostics"/>, which callers that have already reported pass as null.
    /// </summary>
    public static int? Of(SyntaxNode directive, SemanticModel model, List<Diagnostic>? diagnostics)
    {
        if (directive.ChildTokens.Length == 0)
            return null;
        var name = directive.ChildTokens[0].Text.ToLowerInvariant();
        var arguments = directive.ChildNodes;

        switch (name)
        {
            case ".byte":
                var bytes = 0;
                foreach (var argument in arguments)
                    bytes += Element(argument, model, diagnostics, limit: (-128, 255), width: 1);
                return bytes;

            case ".word":
                return Fixed(arguments, model, diagnostics, width: 2, limit: (-32768, 65535));
            case ".addr":
                return Fixed(arguments, model, diagnostics, width: 2, limit: null);
            case ".faraddr":
                return Fixed(arguments, model, diagnostics, width: 3, limit: null);
            case ".dword":
                return Fixed(arguments, model, diagnostics, width: 4, limit: (-2147483648, 4294967295));

            // `.asciiz` is one zero byte after the text, as ca65 writes it.
            case ".asciiz":
                var text = 0;
                foreach (var argument in arguments)
                    text += Element(argument, model, diagnostics, limit: (-128, 255), width: 1);
                return text + 1;

            case ".res":
                return Reserved(directive, arguments, model, diagnostics);

            // `.align` and `.incbin` arrive with Stage 7, and the rest are not data at all.
            default:
                return null;
        }
    }

    /// <summary>The bytes a string or character literal becomes (§8, §13), or null for anything else.</summary>
    public static IReadOnlyList<long>? Bytes(SyntaxNode argument, SemanticModel model)
    {
        if (argument.Kind is not (SyntaxKind.StringExpression or SyntaxKind.CharacterExpression))
            return null;
        var value = model.ValueOf(argument);
        return value.Kind switch
        {
            ValueKind.String => [.. value.Text!.Select(c => (long)c)],
            ValueKind.Number => [value.Number],
            _ => null,
        };
    }

    /// <summary>One element of a byte-wide directive: a string is its bytes, anything else is one.</summary>
    private static int Element(
        SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, (long Low, long High) limit, int width)
    {
        CheckAscii(argument, model, diagnostics);
        if (Bytes(argument, model) is { } bytes)
        {
            foreach (var value in bytes)
            {
                if (value is < 0 or > 255)
                    Report(argument, model, diagnostics, $"`{(char)value}` is not a byte; a charmap (§8) maps text to bytes");
            }
            return bytes.Count * width;
        }
        CheckRange(argument, model, diagnostics, limit);
        return width;
    }

    private static int Fixed(
        IReadOnlyList<SyntaxNode> arguments, SemanticModel model, List<Diagnostic>? diagnostics,
        int width, (long Low, long High)? limit)
    {
        var length = 0;
        foreach (var argument in arguments)
        {
            // A string in a word-wide directive is still one element per byte, each widened.
            length += limit is { } range
                ? Element(argument, model, diagnostics, range, width)
                : Element(argument, model, diagnostics, (long.MinValue, long.MaxValue), width);
        }
        return length;
    }

    /// <summary><c>.res n</c> or <c>.res n, fill</c>: the count is a constant (§7.6).</summary>
    private static int? Reserved(
        SyntaxNode directive, IReadOnlyList<SyntaxNode> arguments, SemanticModel model, List<Diagnostic>? diagnostics)
    {
        if (arguments.Count == 0)
            return null;
        if (arguments.Count > 1)
            CheckRange(arguments[1], model, diagnostics, (-128, 255));

        var count = model.ValueOf(arguments[0]).AsNumber();
        if (count is null)
        {
            Report(arguments[0], model, diagnostics, "a `.res` count must be a constant");
            return null;
        }
        if (count is < 0 or > 0xffffff)
        {
            Report(arguments[0], model, diagnostics, $"a `.res` count must be between 0 and $ffffff, not {count}");
            return null;
        }
        return (int)count;
    }

    /// <summary>
    /// Outside a charmap, text is ASCII and <c>\xHH</c> writes any byte (§4), so a character
    /// typed directly above <c>$7f</c> is an error rather than a byte of some encoding.
    /// </summary>
    private static void CheckAscii(SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics)
    {
        if (argument.Kind is not (SyntaxKind.StringExpression or SyntaxKind.CharacterExpression))
            return;
        foreach (var token in argument.ChildTokens)
        {
            if (token.Text.Any(c => c > 127))
            {
                Report(argument, model, diagnostics,
                    "text is ASCII outside a charmap; write `\\xHH` for a byte above $7f");
                return;
            }
        }
    }

    private static void CheckRange(
        SyntaxNode argument, SemanticModel model, List<Diagnostic>? diagnostics, (long Low, long High) limit)
    {
        if (model.ValueOf(argument).AsNumber() is { } value && (value < limit.Low || value > limit.High))
            Report(argument, model, diagnostics, $"{Value.Of(value)} does not fit in this directive");
    }

    private static void Report(SyntaxNode node, SemanticModel model, List<Diagnostic>? diagnostics, string message) =>
        diagnostics?.Add(new Diagnostic(model.Tree.GetSpan(node.Span), Severity.Error, message));
}
