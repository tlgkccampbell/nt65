using System.Collections.Immutable;
using System.Text;

namespace Norristown.Syntax;

/// <summary>
/// Lexes one line at a time, with no state carried between lines (§4). Whitespace before
/// the first token is its leading trivia; whitespace and a comment after a token, up to the
/// line break, are its trailing trivia; the line break is the text of the final
/// <see cref="SyntaxKind.EndOfLine"/> token, which carries the trivia of a line with no
/// other tokens.
/// </summary>
public static class Lexer
{
    /// <summary>Lexes <paramref name="line"/>, which holds at most one line break, at its end.</summary>
    public static GreenLine LexLine(ReadOnlySpan<char> line)
    {
        var contentEnd = line.IndexOfAny('\r', '\n');
        if (contentEnd < 0)
            contentEnd = line.Length;
        var content = line[..contentEnd];

        var tokens = ImmutableArray.CreateBuilder<GreenToken>();
        var pos = SkipWhitespace(content, 0);
        var leading = GreenCache.Whitespace(content[..pos]);

        while (pos < content.Length && content[pos] != ';')
        {
            var start = pos;
            var (kind, error) = Scan(content, ref pos);
            var text = content[start..pos];

            var triviaStart = pos;
            pos = SkipWhitespace(content, pos);
            var trailing = GreenCache.Whitespace(content[triviaStart..pos]);
            if (pos < content.Length && content[pos] == ';')
            {
                trailing = trailing.Add(new GreenTrivia(SyntaxKind.CommentTrivia, content[pos..].ToString()));
                pos = content.Length;
            }
            tokens.Add(GreenCache.Token(kind, text, leading, trailing, error));
            leading = ImmutableArray<GreenTrivia>.Empty;
        }

        // A line with no tokens: its whitespace and comment belong to the end-of-line token.
        if (pos < content.Length)
            leading = leading.Add(new GreenTrivia(SyntaxKind.CommentTrivia, content[pos..].ToString()));
        tokens.Add(GreenCache.Token(SyntaxKind.EndOfLine, line[contentEnd..], leading, ImmutableArray<GreenTrivia>.Empty, null));
        return new GreenLine(tokens.ToImmutable());
    }

    private static int SkipWhitespace(ReadOnlySpan<char> text, int pos)
    {
        while (pos < text.Length && text[pos] is ' ' or '\t')
            pos++;
        return pos;
    }

    private static (SyntaxKind Kind, string? Error) Scan(ReadOnlySpan<char> text, ref int pos)
    {
        var c = text[pos];
        var next = pos + 1 < text.Length ? text[pos + 1] : '\0';

        if (SyntaxFacts.IsIdentifierStart(c))
        {
            var word = text[pos..SkipWord(text, pos)];
            pos += word.Length;
            if (SyntaxFacts.IsRegister(word))
                return (SyntaxKind.Register, null);
            if (SyntaxFacts.IsMnemonic(word))
                return (SyntaxKind.Mnemonic, null);
            return (SyntaxKind.Identifier, null);
        }

        // A number runs to the end of the word, so `12ab` and `$1G` are one bad number rather
        // than a number followed by an identifier.
        if (char.IsAsciiDigit(c))
        {
            var word = text[pos..SkipWord(text, pos)];
            pos += word.Length;
            if (word.Equals("65c02", StringComparison.OrdinalIgnoreCase))
                return (SyntaxKind.CpuName, null);
            return (SyntaxKind.NumberLiteral, word.ContainsAnyExceptInRange('0', '9') ? $"invalid decimal number `{word}`" : null);
        }
        if (c is '$' or '%')
        {
            var word = text[(pos + 1)..SkipWord(text, pos + 1)];
            pos += 1 + word.Length;
            var hex = c == '$';
            string? error = null;
            if (word.IsEmpty)
                error = hex ? "expected hexadecimal digits after `$`" : "expected binary digits after `%` (the remainder operator is `.mod`)";
            else if (hex ? !IsAll(word, char.IsAsciiHexDigit) : word.ContainsAnyExcept('0', '1'))
                error = $"invalid {(hex ? "hexadecimal" : "binary")} number `{c}{word}`";
            return (SyntaxKind.NumberLiteral, error);
        }

        switch (c)
        {
            case '@':
                if (!SyntaxFacts.IsIdentifierStart(next))
                {
                    pos++;
                    return (SyntaxKind.BadToken, "expected a name after `@`");
                }
                pos = SkipWord(text, pos + 1);
                return (SyntaxKind.CheapLocal, null);
            case '.':
                if (next == '.')
                {
                    pos += 2;
                    return (SyntaxKind.DotDot, null);
                }
                if (!SyntaxFacts.IsIdentifierStart(next))
                {
                    pos++;
                    return (SyntaxKind.BadToken, "unexpected `.`");
                }
                pos = SkipWord(text, pos + 1);
                return (SyntaxKind.Directive, null);
            case '\'':
                return (SyntaxKind.CharacterLiteral, ScanQuoted(text, ref pos, '\''));
            case '"':
                return (SyntaxKind.StringLiteral, ScanQuoted(text, ref pos, '"'));
        }

        var (kind, length) = (c, next) switch
        {
            (':', ':') => (SyntaxKind.ColonColon, 2),
            ('-', '>') => (SyntaxKind.Arrow, 2),
            ('=', '=') => (SyntaxKind.EqualsEquals, 2),
            ('!', '=') => (SyntaxKind.BangEquals, 2),
            ('<', '=') => (SyntaxKind.LessEquals, 2),
            ('<', '<') => (SyntaxKind.LessLess, 2),
            ('>', '=') => (SyntaxKind.GreaterEquals, 2),
            ('>', '>') => (SyntaxKind.GreaterGreater, 2),
            ('&', '&') => (SyntaxKind.AmpersandAmpersand, 2),
            ('|', '|') => (SyntaxKind.BarBar, 2),
            ('^', '^') => (SyntaxKind.CaretCaret, 2),
            (':', _) => (SyntaxKind.Colon, 1),
            (',', _) => (SyntaxKind.Comma, 1),
            ('(', _) => (SyntaxKind.OpenParen, 1),
            (')', _) => (SyntaxKind.CloseParen, 1),
            ('[', _) => (SyntaxKind.OpenBracket, 1),
            (']', _) => (SyntaxKind.CloseBracket, 1),
            ('{', _) => (SyntaxKind.OpenBrace, 1),
            ('}', _) => (SyntaxKind.CloseBrace, 1),
            ('#', _) => (SyntaxKind.Hash, 1),
            ('=', _) => (SyntaxKind.Equals, 1),
            ('!', _) => (SyntaxKind.Bang, 1),
            ('<', _) => (SyntaxKind.Less, 1),
            ('>', _) => (SyntaxKind.Greater, 1),
            ('+', _) => (SyntaxKind.Plus, 1),
            ('-', _) => (SyntaxKind.Minus, 1),
            ('*', _) => (SyntaxKind.Star, 1),
            ('/', _) => (SyntaxKind.Slash, 1),
            ('&', _) => (SyntaxKind.Ampersand, 1),
            ('|', _) => (SyntaxKind.Bar, 1),
            ('^', _) => (SyntaxKind.Caret, 1),
            ('~', _) => (SyntaxKind.Tilde, 1),
            ('?', _) => (SyntaxKind.Question, 1),
            _ => (SyntaxKind.BadToken, 0),
        };
        if (kind != SyntaxKind.BadToken)
        {
            pos += length;
            return (kind, null);
        }

        // One whole character, so a surrogate pair is not split.
        Rune.DecodeFromUtf16(text[pos..], out var rune, out var consumed);
        pos += consumed;
        return (SyntaxKind.BadToken, $"unexpected character `{rune}`");
    }

    private static int SkipWord(ReadOnlySpan<char> text, int pos)
    {
        while (pos < text.Length && SyntaxFacts.IsIdentifierPart(text[pos]))
            pos++;
        return pos;
    }

    private static bool IsAll(ReadOnlySpan<char> text, Func<char, bool> predicate)
    {
        foreach (var c in text)
        {
            if (!predicate(c))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A character or string literal, up to its closing quote or the end of the line. The
    /// escapes are <c>\n \r \t \\ \" \' \xHH</c> (§4) in both. Returns the first error.
    /// </summary>
    private static string? ScanQuoted(ReadOnlySpan<char> text, ref int pos, char quote)
    {
        var isChar = quote == '\'';
        string? error = null;
        var characters = 0;
        pos++;
        while (true)
        {
            if (pos >= text.Length)
                return error ?? (isChar ? "unterminated character literal" : "unterminated string");
            var c = text[pos];
            if (c == quote)
            {
                pos++;
                break;
            }
            characters++;
            if (c != '\\')
            {
                Rune.DecodeFromUtf16(text[pos..], out _, out var consumed);
                pos += consumed;
                continue;
            }
            var escape = pos + 1 < text.Length ? text[pos + 1] : '\0';
            switch (escape)
            {
                case 'n' or 'r' or 't' or '\\' or '"' or '\'':
                    pos += 2;
                    break;
                case 'x' when pos + 3 < text.Length && char.IsAsciiHexDigit(text[pos + 2]) && char.IsAsciiHexDigit(text[pos + 3]):
                    pos += 4;
                    break;
                case 'x':
                    error ??= "`\\x` must be followed by two hexadecimal digits";
                    pos += 2;
                    break;
                case '\0':
                    pos++;
                    break;
                default:
                    Rune.DecodeFromUtf16(text[(pos + 1)..], out var rune, out var consumed);
                    error ??= $"unknown escape `\\{rune}`";
                    pos += 1 + consumed;
                    break;
            }
        }
        if (isChar && characters != 1)
            error ??= characters == 0 ? "empty character literal" : "a character literal holds exactly one character";
        return error;
    }
}
