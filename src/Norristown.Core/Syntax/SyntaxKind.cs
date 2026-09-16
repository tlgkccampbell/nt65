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

    // Statements. One line parses to exactly one of these, whose last child is the line's
    // end-of-line token, so a statement's text is its whole line.

    /// <summary>A line with no tokens of its own.</summary>
    BlankLine,

    /// <summary>A line holding only <c>}</c>.</summary>
    BlockCloseLine,

    /// <summary>A label, and the statement that may follow it on the same line.</summary>
    LabeledLine,

    /// <summary><c>name :</c> or <c>@name :</c>.</summary>
    Label,

    /// <summary><c>name = expr</c>.</summary>
    ConstantDeclaration,

    /// <summary>A mnemonic and its operand.</summary>
    InstructionStatement,

    /// <summary><c>.byte</c>, <c>.res</c> and the rest of §8, with their operands.</summary>
    DataDirective,

    /// <summary><c>.cpu 6502</c>.</summary>
    CpuDirective,

    /// <summary><c>.export a, b</c>.</summary>
    ExportDirective,

    /// <summary><c>.import a: zp, b = $10</c>.</summary>
    ImportDirective,

    /// <summary>One item of an <c>.import</c>.</summary>
    ImportItem,

    /// <summary><c>proc(a8, i16 -&gt; a8)</c> in an <c>.import</c>.</summary>
    ImportSignature,

    /// <summary><c>.segment "NAME": size</c>, with its attributes.</summary>
    SegmentDeclaration,

    /// <summary><c>dp = expr</c> or <c>bank = expr</c> in a segment declaration.</summary>
    SegmentAttribute,

    /// <summary>The line opening a segment block, named or shortcut.</summary>
    SegmentBlock,

    /// <summary>The line opening a <c>.proc</c>.</summary>
    ProcDeclaration,

    /// <summary><c>.proc name = expr</c>: a routine with a signature and no body.</summary>
    ExternProcDeclaration,

    /// <summary>The line opening a <c>.scope</c>.</summary>
    ScopeDeclaration,

    /// <summary>A construct a later stage brings online; its tokens are kept and nothing is diagnosed.</summary>
    UnsupportedLine,

    /// <summary>A line the parser could not read at all.</summary>
    ErrorLine,

    /// <summary>Tokens left over at the end of a line, which the statement does not explain.</summary>
    SkippedTokens,

    // Processor-state signatures (§7.3). Parsed and kept from Stage 2; used from Stage 11.

    /// <summary><c>: state (-&gt; state)?</c> after a proc's name.</summary>
    ProcSignature,

    /// <summary>Comma-separated state items.</summary>
    StateList,

    /// <summary>One state item, such as <c>a16</c>, <c>i*</c> or <c>dp = $2100</c>.</summary>
    StateItem,

    // Expressions (§9).

    /// <summary>Two operands and the operator between them.</summary>
    BinaryExpression,

    /// <summary>A prefix operator and its operand.</summary>
    UnaryExpression,

    /// <summary>An expression in parentheses.</summary>
    ParenthesizedExpression,

    /// <summary>A number.</summary>
    NumberExpression,

    /// <summary>A character literal.</summary>
    CharacterExpression,

    /// <summary>A string.</summary>
    StringExpression,

    /// <summary>A CPU name, which only <c>.target</c> and <c>.cpu</c> accept.</summary>
    CpuNameExpression,

    /// <summary>A name, possibly scoped: <c>gfx::init</c>, <c>::top</c>, <c>@loop</c>.</summary>
    NameExpression,

    /// <summary><c>*</c>, the current address.</summary>
    CurrentAddressExpression,

    /// <summary>A built-in function, charmap or <c>.func</c> call.</summary>
    CallExpression,

    /// <summary>The parenthesized arguments of a call.</summary>
    ArgumentList,

    /// <summary>An expression the parser could not read; empty where nothing was written at all.</summary>
    ErrorExpression,

    // Operands (§7.1). Which ones an instruction and a CPU allow is decided in Stage 5.

    /// <summary><c>#expr</c>, or the two bank bytes of <c>mvn</c> and <c>mvp</c>.</summary>
    ImmediateOperand,

    /// <summary><c>a</c>.</summary>
    AccumulatorOperand,

    /// <summary>An address operand: an optional prefix, an expression and an optional index.</summary>
    AbsoluteOperand,

    /// <summary><c>(expr)</c>, optionally indexed by <c>y</c>.</summary>
    IndirectOperand,

    /// <summary><c>(expr,x)</c> or <c>(expr,s),y</c>.</summary>
    IndexedIndirectOperand,

    /// <summary><c>[expr]</c>, optionally indexed by <c>y</c>.</summary>
    LongIndirectOperand,

    /// <summary><c>z:</c>, <c>a:</c>, <c>f:</c> or <c>d:</c> before an operand.</summary>
    AddressPrefix,
}
