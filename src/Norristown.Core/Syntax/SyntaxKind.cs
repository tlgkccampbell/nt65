namespace Norristown.Syntax;

/// <summary>The kind of a trivia, token or node.</summary>
public enum SyntaxKind : byte
{
    /// <summary>No kind.</summary>
    None,

    /// <summary>Spaces and tabs between tokens.</summary>
    WhitespaceTrivia,

    /// <summary>A <c>;</c> comment, running to the end of the line.</summary>
    CommentTrivia,

    /// <summary>A name: <c>foo</c>, <c>_bar</c>, <c>Baz9</c>.</summary>
    Identifier,

    /// <summary>A cheap local: <c>@loop</c>.</summary>
    CheapLocal,

    /// <summary>A mnemonic of any CPU, or a long branch. Case-insensitive.</summary>
    Mnemonic,

    /// <summary>A register: <c>a</c>, <c>x</c>, <c>y</c> or <c>s</c>. Case-insensitive.</summary>
    Register,

    /// <summary>A <c>.name</c> directive, including the <c>.mod</c> operator.</summary>
    Directive,

    /// <summary>A number: <c>$1F</c>, <c>%1010</c> or <c>255</c>.</summary>
    NumberLiteral,

    /// <summary>A character literal: <c>'c'</c>.</summary>
    CharacterLiteral,

    /// <summary>A string: <c>"text"</c>.</summary>
    StringLiteral,

    /// <summary>The CPU name <c>65c02</c>, which is not a number. <c>6502</c> and <c>65816</c> are.</summary>
    CpuName,

    /// <summary>A character that starts no token.</summary>
    BadToken,

    /// <summary>
    /// The line break ending a line, or empty text on the last line. Carries the trivia of a
    /// line that has no other token.
    /// </summary>
    EndOfLine,

    /// <summary><c>::</c>.</summary>
    ColonColon,

    /// <summary><c>:</c>.</summary>
    Colon,

    /// <summary><c>-&gt;</c>.</summary>
    Arrow,

    /// <summary><c>..</c>.</summary>
    DotDot,

    /// <summary><c>,</c>.</summary>
    Comma,

    /// <summary><c>(</c>.</summary>
    OpenParen,

    /// <summary><c>)</c>.</summary>
    CloseParen,

    /// <summary><c>[</c>.</summary>
    OpenBracket,

    /// <summary><c>]</c>.</summary>
    CloseBracket,

    /// <summary><c>{</c>.</summary>
    OpenBrace,

    /// <summary><c>}</c>.</summary>
    CloseBrace,

    /// <summary><c>#</c>.</summary>
    Hash,

    /// <summary><c>=</c>.</summary>
    Equals,

    /// <summary><c>==</c>.</summary>
    EqualsEquals,

    /// <summary><c>!</c>.</summary>
    Bang,

    /// <summary><c>!=</c>.</summary>
    BangEquals,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessEquals,

    /// <summary><c>&lt;&lt;</c>.</summary>
    LessLess,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterEquals,

    /// <summary><c>&gt;&gt;</c>.</summary>
    GreaterGreater,

    /// <summary><c>+</c>.</summary>
    Plus,

    /// <summary><c>-</c>.</summary>
    Minus,

    /// <summary><c>*</c>.</summary>
    Star,

    /// <summary><c>/</c>.</summary>
    Slash,

    /// <summary><c>&amp;</c>.</summary>
    Ampersand,

    /// <summary><c>&amp;&amp;</c>.</summary>
    AmpersandAmpersand,

    /// <summary><c>|</c>.</summary>
    Bar,

    /// <summary><c>||</c>.</summary>
    BarBar,

    /// <summary><c>^</c>.</summary>
    Caret,

    /// <summary><c>^^</c>.</summary>
    CaretCaret,

    /// <summary><c>~</c>.</summary>
    Tilde,

    /// <summary><c>?</c>.</summary>
    Question,

    /// <summary>One source line and its tokens.</summary>
    Line,

    /// <summary>A brace-delimited block of lines and nested blocks.</summary>
    Block,

    /// <summary>A whole file: the lines and blocks at its top level.</summary>
    File,
}
