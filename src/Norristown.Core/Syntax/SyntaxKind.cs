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

    /// <summary>
    /// The CPU names <c>65c02</c> and <c>65sc02</c>, which are not numbers. <c>6502</c> and
    /// <c>65816</c> are, and <c>r65c02</c> is an identifier.
    /// </summary>
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

    /// <summary>A list-valued child of a node: its items, under one slot of that node.</summary>
    List,

    /// <summary>A list whose items are written with a separator between them, nearly always a comma.</summary>
    SeparatedList,

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

    /// <summary>
    /// <c>.byte</c>, <c>.res</c> and the rest of the data directives, with their operands: an
    /// element type with its count and values, or a directive that is only bytes.
    /// </summary>
    DataDirective,

    /// <summary><c>.data name: .word[16]</c>, <c>.data name: .byte 1, 2</c> or <c>.data name {</c>.</summary>
    DataDeclaration,

    /// <summary><c>[16]</c> or <c>[]</c> after an element type: how many elements there are.</summary>
    ElementCount,

    /// <summary><c>[1]</c> after a name in a path: which element of a counted declaration it is.</summary>
    ElementIndex,

    /// <summary>One line of a data body: values separated by commas, one element each.</summary>
    DataValues,

    /// <summary>The <c>{</c> of a data directive whose values are on the lines it opens.</summary>
    DataBody,

    /// <summary>The one braced value of a counted or record data directive.</summary>
    BracedData,

    /// <summary>The values of a data directive, written on its own line.</summary>
    InlineData,

    /// <summary><c>{ 1, 2, 4 }</c>: values in braces, one element each.</summary>
    ValueList,

    /// <summary><c>.cpu 6502</c>.</summary>
    CpuDirective,

    /// <summary><c>.export a, b: abs, c as "_c"</c>.</summary>
    ExportDirective,

    /// <summary>One item of an <c>.export</c> list: a name or a path, and its size or linker name.</summary>
    ExportItem,

    /// <summary><c>.module hw::vic</c>: the name of the module a file is.</summary>
    ModuleDirective,

    /// <summary><c>.use hw::init</c>, <c>.use hw::{a, b}</c>, <c>.use hw::*</c>, <c>.use hw::init as i</c>.</summary>
    UseDirective,

    /// <summary>One name in the braces of a <c>.use</c>, with the name it is brought in as.</summary>
    UseItem,

    /// <summary><c>.import a: zp, b = $10</c>.</summary>
    ImportDirective,

    /// <summary>One item of an <c>.import</c>.</summary>
    ImportItem,

    /// <summary><c>proc(a8, i16 -&gt; a8)</c> in an <c>.import</c>.</summary>
    ImportSignature,

    /// <summary><c>.segment NAME: size</c>, with its attributes.</summary>
    SegmentDeclaration,

    /// <summary><c>dp = expr</c>, <c>bank = expr</c> or <c>mirrors = [...]</c> in a segment declaration.</summary>
    SegmentAttribute,

    /// <summary>One bank, <c>$80</c>, or a range of them, <c>$00..$3f</c>, in a segment's <c>mirrors</c>.</summary>
    BankRange,

    /// <summary><c>.segment NAME {</c>: the line opening a segment block.</summary>
    SegmentBlock,

    /// <summary><c>.segment NAME</c>: places every item after it, up to the next one, in that segment.</summary>
    SegmentRegion,

    /// <summary>The line opening a <c>.proc</c>.</summary>
    ProcDeclaration,

    /// <summary><c>.proc name = expr</c>: a routine with a signature and no body.</summary>
    ExternProcDeclaration,

    /// <summary>The line opening a <c>.multiproc</c>: one routine per member of an enum.</summary>
    MultiProcDeclaration,

    /// <summary>The line opening a <c>.scope</c>.</summary>
    ScopeDeclaration,

    /// <summary>The line opening an <c>.enum</c>, named or anonymous.</summary>
    EnumDeclaration,

    /// <summary>The line opening a <c>.struct</c>, named or anonymous.</summary>
    StructDeclaration,

    /// <summary>The line opening a <c>.union</c>.</summary>
    UnionDeclaration,

    /// <summary>The line opening a <c>.charmap</c>.</summary>
    CharmapDeclaration,

    /// <summary>The line opening a <c>.list</c>.</summary>
    ListDeclaration,

    /// <summary><c>.func name(a, b) = expr</c>.</summary>
    FuncDeclaration,

    /// <summary>The parenthesized parameter names of a <c>.func</c>.</summary>
    ParameterList,

    /// <summary>One parameter of a <c>.func</c>.</summary>
    Parameter,

    /// <summary><c>.signature std = a8, i16, dp = 0</c>: a named set of signature items.</summary>
    SignatureDeclaration,

    /// <summary><c>.config NAME = value</c>: a define a module declares, which the build may set.</summary>
    ConfigDeclaration,

    /// <summary>One member of an <c>.enum</c>: <c>name</c> or <c>name = expr</c>.</summary>
    EnumMember,

    /// <summary>One entry of a <c>.charmap</c>: <c>'a' = n</c> or <c>'a'..'z' = n</c>.</summary>
    CharmapEntry,

    /// <summary>One line of a <c>.list</c>: comma-separated items.</summary>
    ListItems,

    /// <summary>The braced <c>member = value</c>s of an initialized record.</summary>
    RecordValues,

    /// <summary>One <c>member = value</c> of an initialized record.</summary>
    MemberValue,

    /// <summary>The line opening an <c>.if</c>, with the condition it tests.</summary>
    IfDirective,

    /// <summary><c>} .elseif expr {</c>: the line closing one branch and opening the next.</summary>
    ElseIfDirective,

    /// <summary><c>} .else {</c>.</summary>
    ElseDirective,

    /// <summary>The line opening a <c>.repeat</c>: a count and the name bound to each index.</summary>
    RepeatDirective,

    /// <summary>The line opening an <c>.each</c>: what to walk and the name bound to each item.</summary>
    EachDirective,

    /// <summary><c>.assert expr, level, "message"</c>.</summary>
    AssertDirective,

    /// <summary><c>.error "message"</c> or <c>.warning "message"</c>.</summary>
    ErrorDirective,

    /// <summary>The line opening a <c>.macro</c>: its name, parameters and signature.</summary>
    MacroDeclaration,

    /// <summary>The parenthesized parameters of a <c>.macro</c>.</summary>
    MacroParameterList,

    /// <summary>One parameter: a name, the kind of argument it takes, and a default.</summary>
    MacroParameter,

    /// <summary>What a parameter accepts: <c>expr</c>, <c>one(...)</c>, <c>list(...)</c> and the rest.</summary>
    ParameterKind,

    /// <summary><c>{}</c>, the default of a <c>block</c> parameter that may be left out.</summary>
    EmptyBlock,

    /// <summary><c>name!(...)</c>, with the <c>{</c> of a block argument when one follows.</summary>
    MacroCall,

    /// <summary><c>} name {</c>: the line closing one block argument and opening the next.</summary>
    BlockContinuation,

    /// <summary>A line naming a <c>block</c> parameter, which splices its argument there.</summary>
    BlockSplice,

    /// <summary><c>name = arg</c> at a call, which binds the argument to that parameter.</summary>
    NamedArgument,

    /// <summary><c>.next @a, @b</c> or <c>.next ?</c>: where flow goes after the statement above.</summary>
    NextDirective,

    /// <summary><c>.patch @op</c>: the store above writes into the instruction at that label.</summary>
    PatchDirective,

    /// <summary><c>.state a16, i8</c>: asserts, and sets, the processor state at this point.</summary>
    StateDirective,

    /// <summary><c>.ensure a16, i8</c>: makes the widths hold, emitting only the <c>rep</c> or <c>sep</c> needed.</summary>
    EnsureDirective,

    /// <summary><c>.frame locals: Locals</c>: names the top bytes of the stack as a struct.</summary>
    FrameDirective,

    /// <summary>A line the parser could not read at all.</summary>
    ErrorLine,

    /// <summary>Tokens left over at the end of a line, which the statement does not explain.</summary>
    SkippedTokens,

    // Processor-state signatures.

    /// <summary><c>: state (-&gt; state)?</c> after a proc's name.</summary>
    ProcSignature,

    /// <summary>Comma-separated state items.</summary>
    StateList,

    /// <summary>A state word and the <c>*</c> or <c>?</c> after it: <c>a16</c>, <c>i*</c>, <c>e?</c>.</summary>
    StateFlagItem,

    /// <summary>A state word and the value it is given: <c>dp = $2100</c>, <c>args 2</c>.</summary>
    StateValueItem,

    /// <summary><c>inline .strz</c>: the data after each call is a zero-terminated string.</summary>
    StateInlineItem,

    /// <summary><c>keeps a, x</c>: the registers a routine leaves as it found them.</summary>
    StateKeepsItem,

    /// <summary>The name of a signature set, which stands for the items it was declared with.</summary>
    StateSetItem,

    /// <summary><c>?</c> on its own: every tracked part of the processor state is unknown.</summary>
    StateUnknownItem,

    // Expressions.

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

    /// <summary>One part of a name, and the <c>[i]</c> after it when it has one.</summary>
    IdentifierName,

    /// <summary><c>*</c>, the current address.</summary>
    CurrentAddressExpression,

    /// <summary>A built-in function, charmap or <c>.func</c> call.</summary>
    CallExpression,

    /// <summary>The parenthesized arguments of a call.</summary>
    ArgumentList,

    /// <summary>An expression the parser could not read; empty where nothing was written at all.</summary>
    ErrorExpression,

    // Operands. Which ones an instruction and a CPU allow is decided in layout.

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

    /// <summary><c>{buf,x}</c>: a whole operand, written as the argument of a macro call.</summary>
    BracedOperand,
}
