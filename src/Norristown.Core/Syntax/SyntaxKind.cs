namespace Norristown.Syntax;

/// <summary>The kind of a trivia, token or node. One enum for all three, as in Roslyn.</summary>
public enum SyntaxKind : byte
{
    None,

    // Trivia.
    WhitespaceTrivia,
    CommentTrivia,

    // Tokens whose text varies.
    Identifier,
    CheapLocal,         // @name
    Mnemonic,           // any CPU's, and the long branches; case-insensitive
    Register,           // a x y s; case-insensitive
    Directive,          // .name, including the .mod operator
    NumberLiteral,      // $1F %1010 255
    CharacterLiteral,   // 'c'
    StringLiteral,      // "text"
    CpuName,            // 65c02: 6502 and 65816 are numbers
    BadToken,           // a character that starts no token
    EndOfLine,          // the line break, or empty on the last line; carries a blank line's trivia

    // Punctuation and operators.
    ColonColon,
    Colon,
    Arrow,              // ->
    DotDot,
    Comma,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    Hash,
    Equals,
    EqualsEquals,
    Bang,
    BangEquals,
    Less,
    LessEquals,
    LessLess,
    Greater,
    GreaterEquals,
    GreaterGreater,
    Plus,
    Minus,
    Star,
    Slash,
    Ampersand,
    AmpersandAmpersand,
    Bar,
    BarBar,
    Caret,
    CaretCaret,
    Tilde,
    Question,

    // Nodes.
    Line,
    Block,
    File,
}
