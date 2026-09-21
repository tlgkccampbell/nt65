using System.Collections.Immutable;
using System.Text;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Lexes one line at a time, with no state carried between lines. Whitespace before
/// the first token is its leading trivia; whitespace and a comment after a token, up to the
/// line break, are its trailing trivia; the line break is the text of the final
/// <see cref="SyntaxKind.EndOfLine"/> token, which carries the trivia of a line with no
/// other tokens.
/// </summary>
internal static class Lexer
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
            var (kind, errors) = Scan(content, ref pos);
            var text = content[start..pos];

            var triviaStart = pos;
            pos = SkipWhitespace(content, pos);
            var trailing = GreenCache.Whitespace(content[triviaStart..pos]);
            if (pos < content.Length && content[pos] == ';')
            {
                trailing = trailing.Add(new GreenTrivia(SyntaxKind.CommentTrivia, content[pos..].ToString()));
                pos = content.Length;
            }
            tokens.Add(GreenCache.Token(kind, text, leading, trailing, errors));
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

    private static (SyntaxKind Kind, IReadOnlyList<DiagnosticMessage>? Errors) Scan(ReadOnlySpan<char> text, ref int pos)
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

            // A CPU name that is no number — `65c02`, `65sc02` — is one word of its own.
            // `6502` and `65816` are numbers, and the places that take a CPU name take either.
            var wrong = Digits(word, '\0', "decimal", char.IsAsciiDigit);
            if (wrong is not null && SyntaxFacts.IsCpuName(word.ToString()))
                return (SyntaxKind.CpuName, null);
            return (SyntaxKind.NumberLiteral, One(wrong));
        }
        if (c is '$' or '%')
        {
            var word = text[(pos + 1)..SkipWord(text, pos + 1)];
            pos += 1 + word.Length;
            var hex = c == '$';
            if (word.IsEmpty)
            {
                return (SyntaxKind.NumberLiteral, One(hex
                    ? Catalogue.DigitsMissing.Says("hexadecimal", "$", "")
                    : Catalogue.DigitsMissing.Says("binary", "%", " (the remainder operator is `.mod`)")));
            }
            return (SyntaxKind.NumberLiteral, One(Digits(
                word, c, hex ? "hexadecimal" : "binary", hex ? char.IsAsciiHexDigit : static digit => digit is '0' or '1')));
        }

        switch (c)
        {
            case '@':
                if (!SyntaxFacts.IsIdentifierStart(next))
                {
                    pos++;
                    return (SyntaxKind.BadToken, One(Catalogue.NameAfterAt));
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
                    return (SyntaxKind.BadToken, One(Catalogue.StrayDot));
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
        return (SyntaxKind.BadToken, One(Catalogue.UnexpectedCharacter.Says(rune)));
    }

    /// <summary>The one thing wrong with a token as the list of them it is given as, or null.</summary>
    private static IReadOnlyList<DiagnosticMessage>? One(DiagnosticMessage? error) =>
        error is { } one ? [one] : null;

    private static int SkipWord(ReadOnlySpan<char> text, int pos)
    {
        while (pos < text.Length && SyntaxFacts.IsIdentifierPart(text[pos]))
            pos++;
        return pos;
    }

    /// <summary>
    /// What is wrong with the digits of a number, or null when nothing is. <c>_</c> between two
    /// digits is a separator, in any base: <c>$7f_ff</c>, <c>%1010_1010</c> and <c>1_000</c>
    /// are the numbers their digits spell. <paramref name="prefix"/> is the <c>$</c> or <c>%</c>
    /// the digits follow, or <c>\0</c> for a decimal number, so that a message names the number
    /// as it was written; nothing is put together until there is something to say.
    /// </summary>
    private static DiagnosticMessage? Digits(ReadOnlySpan<char> digits, char prefix, string radix, Func<char, bool> isDigit)
    {
        for (var i = 0; i < digits.Length; i++)
        {
            if (digits[i] == '_')
            {
                if (i == 0 || i == digits.Length - 1 || digits[i - 1] == '_')
                    return Catalogue.NumberSeparator.Says(Written(digits, prefix));
                continue;
            }
            if (!isDigit(digits[i]))
                return Catalogue.NumberInvalid.Says(radix, Written(digits, prefix));
        }
        return null;

        static string Written(ReadOnlySpan<char> digits, char prefix) =>
            prefix == '\0' ? digits.ToString() : prefix + digits.ToString();
    }

    /// <summary>
    /// A character or string literal, up to its closing quote or the end of the line. The
    /// escapes are <c>\n \r \t \0 \\ \" \' \xHH</c> in both. Every escape it cannot read is
    /// reported, since each is a separate thing to correct; that a character literal holds
    /// more than one character is not, where an escape it could not read is why.
    /// </summary>
    private static IReadOnlyList<DiagnosticMessage>? ScanQuoted(ReadOnlySpan<char> text, ref int pos, char quote)
    {
        var isChar = quote == '\'';
        List<DiagnosticMessage>? errors = null;
        var characters = 0;
        pos++;
        while (true)
        {
            if (pos >= text.Length)
            {
                if (errors is null)
                    return One(Catalogue.TextUnterminated.Says(isChar ? "character literal" : "string"));
                return errors;
            }
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
                case 'n' or 'r' or 't' or '0' or '\\' or '"' or '\'':
                    pos += 2;
                    break;
                case 'x' when pos + 3 < text.Length && char.IsAsciiHexDigit(text[pos + 2]) && char.IsAsciiHexDigit(text[pos + 3]):
                    pos += 4;
                    break;
                case 'x':
                    (errors ??= []).Add(Catalogue.EscapeHexDigits);
                    pos += 2;
                    break;
                case '\0':
                    pos++;
                    break;
                default:
                    Rune.DecodeFromUtf16(text[(pos + 1)..], out var rune, out var consumed);
                    (errors ??= []).Add(Catalogue.EscapeUnknown.Says(rune));
                    pos += 1 + consumed;
                    break;
            }
        }

        // An escape that could not be read is why the characters do not come to one, so the
        // count is news only where every escape was read.
        if (isChar && characters != 1 && errors is null)
        {
            return One(characters == 0 ? Catalogue.CharacterEmpty : Catalogue.CharacterTooLong);
        }
        return errors;
    }
}
