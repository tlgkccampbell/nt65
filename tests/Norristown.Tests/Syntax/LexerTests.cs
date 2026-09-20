using Norristown.Syntax.InternalSyntax;
using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

public sealed class LexerTests
{
    [Theory]
    // Identifiers and scoped names. `::` is one token, so `z::foo` walks into scope z.
    [InlineData("foo _bar Baz9", "Identifier:foo Identifier:_bar Identifier:Baz9")]
    [InlineData("gfx::init", "Identifier:gfx ColonColon::: Identifier:init")]
    [InlineData("::top_level", "ColonColon::: Identifier:top_level")]
    [InlineData("z::foo", "Identifier:z ColonColon::: Identifier:foo")]
    [InlineData("z: ::foo", "Identifier:z Colon:: ColonColon::: Identifier:foo")]
    [InlineData("z:ptr+1", "Identifier:z Colon:: Identifier:ptr Plus:+ NumberLiteral:1")]
    [InlineData("@loop: @a_1", "CheapLocal:@loop Colon:: CheapLocal:@a_1")]
    // Mnemonics of every CPU and the long branches, case-insensitive; ca65's aliases are not reserved.
    [InlineData("LDA stz bbr7 Xce jeq JVC", "Mnemonic:LDA Mnemonic:stz Mnemonic:bbr7 Mnemonic:Xce Mnemonic:jeq Mnemonic:JVC")]
    [InlineData("ldax bbr8 tad", "Identifier:ldax Identifier:bbr8 Identifier:tad")]
    [InlineData("a X y S", "Register:a Register:X Register:y Register:S")]
    [InlineData("a8 i16 xs", "Identifier:a8 Identifier:i16 Identifier:xs")]
    [InlineData("a? a*, e?", "Register:a Question:? Register:a Star:* Comma:, Identifier:e Question:?")]
    [InlineData(".byte .PROC .mod", "Directive:.byte Directive:.PROC Directive:.mod")]
    // Numbers; 6502 and 65816 are numbers, 65c02 is a CPU name.
    [InlineData("$1F $ffff %1010 255 0", "NumberLiteral:$1F NumberLiteral:$ffff NumberLiteral:%1010 NumberLiteral:255 NumberLiteral:0")]
    [InlineData("6502 65c02 65C02 65816", "NumberLiteral:6502 CpuName:65c02 CpuName:65C02 NumberLiteral:65816")]
    [InlineData(@"'c' '\n' '\x41' '\'' '""' ';'", @"CharacterLiteral:'c' CharacterLiteral:'\n' CharacterLiteral:'\x41' CharacterLiteral:'\'' CharacterLiteral:'""' CharacterLiteral:';'")]
    [InlineData(@"""text"" ""a\""b"" ""\\"" """" ""it's; not a comment""", @"StringLiteral:""text"" StringLiteral:""a\""b"" StringLiteral:""\\"" StringLiteral:"""" StringLiteral:""it's; not a comment""")]
    [InlineData("'é'", "CharacterLiteral:'é'")]
    // Multi-character tokens: `a->b` is not `a - >b`, written with spaces it is.
    [InlineData("a->b", "Register:a Arrow:-> Identifier:b")]
    [InlineData("a - >b", "Register:a Minus:- Greater:> Identifier:b")]
    [InlineData("'A'..'Z'", "CharacterLiteral:'A' DotDot:.. CharacterLiteral:'Z'")]
    [InlineData("== != <= << >= >> && || ^^", "EqualsEquals:== BangEquals:!= LessEquals:<= LessLess:<< GreaterEquals:>= GreaterGreater:>> AmpersandAmpersand:&& BarBar:|| CaretCaret:^^")]
    [InlineData("< > + - * / & | ^ ~ ! # = ?", "Less:< Greater:> Plus:+ Minus:- Star:* Slash:/ Ampersand:& Bar:| Caret:^ Tilde:~ Bang:! Hash:# Equals:= Question:?")]
    [InlineData(", ( ) [ ] { } : ::", "Comma:, OpenParen:( CloseParen:) OpenBracket:[ CloseBracket:] OpenBrace:{ CloseBrace:} Colon:: ColonColon:::")]
    [InlineData("set16!({buf,x}, $1234)", "Identifier:set16 Bang:! OpenParen:( OpenBrace:{ Identifier:buf Comma:, Register:x CloseBrace:} Comma:, NumberLiteral:$1234 CloseParen:)")]
    [InlineData("#<(label+1)", "Hash:# Less:< OpenParen:( Identifier:label Plus:+ NumberLiteral:1 CloseParen:)")]
    [InlineData("lda [dp],y", "Mnemonic:lda OpenBracket:[ Identifier:dp CloseBracket:] Comma:, Register:y")]
    public void TokenClasses(string line, string expected) => Assert.Equal(expected, Lex(line));

    [Theory]
    [InlineData("12ab", "NumberLiteral", "invalid decimal number `12ab`")]
    [InlineData("$", "NumberLiteral", "expected hexadecimal digits after `$`")]
    [InlineData("$1G", "NumberLiteral", "invalid hexadecimal number `$1G`")]
    [InlineData("%102", "NumberLiteral", "invalid binary number `%102`")]
    [InlineData("%", "NumberLiteral", "expected binary digits after `%` (the remainder operator is `.mod`)")]
    [InlineData("@", "BadToken", "expected a name after `@`")]
    [InlineData(".", "BadToken", "unexpected `.`")]
    [InlineData("'ab'", "CharacterLiteral", "a character literal holds exactly one character")]
    [InlineData("''", "CharacterLiteral", "empty character literal")]
    [InlineData("'a", "CharacterLiteral", "unterminated character literal")]
    [InlineData("\"abc ; no comment", "StringLiteral", "unterminated string")]
    [InlineData(@"""\q""", "StringLiteral", @"unknown escape `\q`")]
    [InlineData(@"""\x4""", "StringLiteral", @"`\x` must be followed by two hexadecimal digits")]
    [InlineData("\\", "BadToken", "unexpected character `\\`")]
    [InlineData("é", "BadToken", "unexpected character `é`")]
    [InlineData("😀", "BadToken", "unexpected character `😀`")]
    public void LexicalErrorsCoverOneToken(string text, string kind, string error)
    {
        var line = Lexer.LexLine(text);
        Assert.Equal(2, line.Tokens.Length);
        Assert.Equal(kind, line.Tokens[0].Kind.ToString());
        Assert.Equal(text, line.Tokens[0].Text);

        // A lexical error covers the token's text, and the line says it holds one.
        Assert.True(line.ContainsDiagnostics);
        var reported = Assert.Single(line.Tokens[0].Diagnostics);
        Assert.Equal(error, reported.Message);
        Assert.Equal(line.Tokens[0].LeadingWidth, reported.Offset);
        Assert.Equal(text.Length, reported.Width);
    }

    [Fact]
    public void TriviaAttachesToTheTokensOnItsLine()
    {
        var line = Lexer.LexLine("    lda #1   ; load\r\n");
        var (lda, hash, one, eol) = (line.Tokens[0], line.Tokens[1], line.Tokens[2], line.Tokens[3]);
        Assert.Equal("    ", Assert.Single(lda.LeadingTrivia).Text);
        Assert.Equal(" ", Assert.Single(lda.TrailingTrivia).Text);
        Assert.Empty(hash.LeadingTrivia);
        Assert.Empty(hash.TrailingTrivia);
        Assert.Equal(["   ", "; load"], one.TrailingTrivia.Select(t => t.Text));
        Assert.Equal(SyntaxKind.CommentTrivia, one.TrailingTrivia[1].Kind);
        Assert.Equal(SyntaxKind.EndOfLine, eol.Kind);
        Assert.Equal("\r\n", eol.Text);
        Assert.Equal("    lda #1   ; load\r\n", line.ToFullString());
        Assert.Equal(4, line.TextOffset(0));
        Assert.Equal(9, line.TextOffset(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \t")]
    [InlineData("  ; only a comment ;! and more\r")]
    public void ALineWithNoTokensKeepsItsTriviaOnTheEndOfLine(string text)
    {
        var line = Lexer.LexLine(text);
        var eol = Assert.Single(line.Tokens);
        Assert.Equal(SyntaxKind.EndOfLine, eol.Kind);
        Assert.Equal(text, line.ToFullString());
    }

    /// <summary>
    /// The cache's tables are per-thread, so what this test sees is decided by its own calls and
    /// not by whatever else the suite is lexing beside it. They are also small and direct-mapped,
    /// and where two tokens of one line want the same slot neither of them is shared — a property
    /// of the table rather than of the sharing, and one that moves with the run, since the hash a
    /// slot comes from is seeded afresh per process. So the sharing is asked of several lines and
    /// wanted of one: a lexer that shares nothing fails every one of them.
    /// </summary>
    [Fact]
    public void CommonTokensAreShared()
    {
        string[] lines = ["    lda #1\n", "    sta $20,x\n", "    ldy #0\n", "  rts\n", "\tinx\n"];
        Assert.Contains(lines, text =>
        {
            var first = Lexer.LexLine(text);
            var second = Lexer.LexLine(text);
            return !ReferenceEquals(first, second)
                && first.Tokens.Select((token, i) => ReferenceEquals(token, second.Tokens[i])).All(same => same);
        });

        // Comments are not shared, and neither is anything with an error.
        Assert.NotSame(Lexer.LexLine("rts ; x").Tokens[0], Lexer.LexLine("rts ; x").Tokens[0]);
        Assert.NotSame(Lexer.LexLine("$").Tokens[0], Lexer.LexLine("$").Tokens[0]);
    }

    private static string Lex(string line) => SyntaxDump.Tokens(Lexer.LexLine(line));
}
