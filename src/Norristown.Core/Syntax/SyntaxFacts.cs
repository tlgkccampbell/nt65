using System.Collections.Frozen;

namespace Norristown.Syntax;

/// <summary>The reserved words of the language, and what a line's tokens mean.</summary>
public static class SyntaxFacts
{
    // Static field initializers run in text order, so each field here is declared after the
    // fields its initializer reads.

    /// <summary>The long branches, which reach any near target.</summary>
    public static readonly FrozenSet<string> LongBranches =
        new[] { "jeq", "jne", "jcs", "jcc", "jmi", "jpl", "jvs", "jvc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every reserved mnemonic, lower case, in a fixed order.</summary>
    public static readonly IReadOnlyList<string> Mnemonics =
        [.. CpuMnemonics().Concat(LongBranches.Order(StringComparer.Ordinal)).Distinct()];

    /// <summary>The register names, lower case.</summary>
    public static readonly IReadOnlyList<string> Registers = ["a", "x", "y", "s"];

    private static readonly FrozenSet<string> mnemonicSet = Mnemonics.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> registerSet = Registers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Directives are case-insensitive, like mnemonics and registers.
    private static readonly FrozenDictionary<string, BlockKind> blockDirectives = new Dictionary<string, BlockKind>
    {
        [".proc"] = BlockKind.Proc,
        [".scope"] = BlockKind.Scope,
        [".macro"] = BlockKind.Macro,
        [".enum"] = BlockKind.Enum,
        [".struct"] = BlockKind.Struct,
        [".union"] = BlockKind.Union,
        [".charmap"] = BlockKind.Charmap,
        [".list"] = BlockKind.List,
        [".segment"] = BlockKind.Segment,
        [".zeropage"] = BlockKind.Segment,
        [".code"] = BlockKind.Segment,
        [".bss"] = BlockKind.Segment,
        [".data"] = BlockKind.Segment,
        [".rodata"] = BlockKind.Segment,
        [".if"] = BlockKind.If,
        [".elseif"] = BlockKind.If,
        [".else"] = BlockKind.If,
        [".repeat"] = BlockKind.Repeat,
        [".each"] = BlockKind.Each,
        [".tag"] = BlockKind.TagInitializer,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Every directive that may begin a line, and the node it parses to. `.segment` and
    // `.proc` each parse to one of two kinds, decided by the rest of the line.
    private static readonly FrozenDictionary<string, SyntaxKind> lineDirectives = new Dictionary<string, SyntaxKind>
    {
        [".cpu"] = SyntaxKind.CpuDirective,
        [".segment"] = SyntaxKind.SegmentDeclaration,
        [".zeropage"] = SyntaxKind.SegmentBlock,
        [".code"] = SyntaxKind.SegmentBlock,
        [".bss"] = SyntaxKind.SegmentBlock,
        [".data"] = SyntaxKind.SegmentBlock,
        [".rodata"] = SyntaxKind.SegmentBlock,
        [".proc"] = SyntaxKind.ProcDeclaration,
        [".scope"] = SyntaxKind.ScopeDeclaration,
        [".export"] = SyntaxKind.ExportDirective,
        [".import"] = SyntaxKind.ImportDirective,
        [".byte"] = SyntaxKind.DataDirective,
        [".word"] = SyntaxKind.DataDirective,
        [".dword"] = SyntaxKind.DataDirective,
        [".addr"] = SyntaxKind.DataDirective,
        [".faraddr"] = SyntaxKind.DataDirective,
        [".res"] = SyntaxKind.DataDirective,
        [".asciiz"] = SyntaxKind.DataDirective,
        [".tag"] = SyntaxKind.DataDirective,
        [".align"] = SyntaxKind.DataDirective,
        [".incbin"] = SyntaxKind.DataDirective,
        [".lobytes"] = SyntaxKind.DataDirective,
        [".hibytes"] = SyntaxKind.DataDirective,
        [".enum"] = SyntaxKind.EnumDeclaration,
        [".struct"] = SyntaxKind.StructDeclaration,
        [".union"] = SyntaxKind.UnionDeclaration,
        [".charmap"] = SyntaxKind.CharmapDeclaration,
        [".list"] = SyntaxKind.ListDeclaration,
        [".func"] = SyntaxKind.FuncDeclaration,
        [".if"] = SyntaxKind.IfDirective,
        [".elseif"] = SyntaxKind.ElseIfDirective,
        [".else"] = SyntaxKind.ElseDirective,
        [".repeat"] = SyntaxKind.RepeatDirective,
        [".each"] = SyntaxKind.EachDirective,
        [".assert"] = SyntaxKind.AssertDirective,
        [".error"] = SyntaxKind.ErrorDirective,
        [".macro"] = SyntaxKind.MacroDeclaration,
        [".next"] = SyntaxKind.NextDirective,
        [".patch"] = SyntaxKind.PatchDirective,
        [".state"] = SyntaxKind.StateDirective,
        [".ensure"] = SyntaxKind.EnsureDirective,
        [".frame"] = SyntaxKind.FrameDirective,

    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The built-in functions, and the three a macro body adds.</summary>
    private static readonly FrozenSet<string> builtinFunctions = new[]
    {
        ".lobyte", ".hibyte", ".bankbyte", ".loword", ".hiword", ".sizeof", ".countof",
        ".endof", ".spanof", ".strlen", ".strat", ".min", ".max", ".addrsize", ".target",
        ".defined", ".mode", ".byteof", ".empty",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The processor-state items, by the suffix that follows the name: a point item stands
    // alone, `*` keeps a part of the state unchanged, `?` forgets it and `=` gives a value.
    private static readonly FrozenSet<string> pointStateItems =
        new[] { "a8", "a16", "i8", "i16", "native", "emu", "near", "far", "inline" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> trackedStateParts =
        new[] { "a", "i", "e", "dp", "dbr" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> valuedStateParts =
        new[] { "dp", "dbr" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The kinds of argument a macro parameter may take, as they are written.</summary>
    private static readonly FrozenSet<string> parameterKinds =
        new[] { "expr", "const", "ident", "operand", "block", "one", "list" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> is a mnemonic, whatever its case.</summary>
    public static bool IsMnemonic(ReadOnlySpan<char> text) => mnemonicSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    /// <summary>Whether <paramref name="text"/> is a register name, whatever its case.</summary>
    public static bool IsRegister(ReadOnlySpan<char> text) => registerSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    /// <summary>Whether <paramref name="text"/> names a kind of macro parameter.</summary>
    public static bool IsParameterKind(string text) => parameterKinds.Contains(text);

    /// <summary>Whether an identifier may start with <paramref name="c"/>.</summary>
    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>Whether an identifier may continue with <paramref name="c"/>.</summary>
    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>The kind of block a directive opens when its line ends in <c>{</c>.</summary>
    public static BlockKind BlockKindOfDirective(string directive) =>
        blockDirectives.GetValueOrDefault(directive, BlockKind.Unknown);

    /// <summary>The kind of node a directive at the start of a line parses to, or <see cref="SyntaxKind.None"/>.</summary>
    public static SyntaxKind LineDirectiveKind(string directive) =>
        lineDirectives.GetValueOrDefault(directive, SyntaxKind.None);

    /// <summary>Whether <paramref name="directive"/> names a built-in function.</summary>
    public static bool IsBuiltinFunction(string directive) => builtinFunctions.Contains(directive);

    /// <summary>
    /// Whether <paramref name="text"/> is a level an <c>.assert</c> may report at. The two
    /// <c>ld</c> levels are the linker's, for a check nothing earlier can make.
    /// </summary>
    public static bool IsAssertLevel(string text) =>
        text.Equals("warning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("error", StringComparison.OrdinalIgnoreCase)
        || text.Equals("ldwarning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("lderror", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> is one of the three CPU names.</summary>
    public static bool IsCpuName(string text) =>
        text is "6502" or "65816" || text.Equals("65c02", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> is an address size: <c>zp</c>, <c>abs</c> or <c>far</c>.</summary>
    public static bool IsAddressSize(string text) =>
        text.Equals("zp", StringComparison.OrdinalIgnoreCase)
        || text.Equals("abs", StringComparison.OrdinalIgnoreCase)
        || text.Equals("far", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="text"/> names an address-size prefix, which is written before
    /// a <c>:</c> in operand position. <c>z</c>, <c>f</c> and <c>d</c> are also ordinary
    /// identifiers; nothing else can be written there, so the case does not matter.
    /// </summary>
    public static bool IsAddressPrefix(string text) =>
        text.Length == 1 && char.ToLowerInvariant(text[0]) is 'z' or 'a' or 'f' or 'd';

    /// <summary>
    /// Whether <paramref name="name"/> followed by <paramref name="suffix"/> is a state item
    /// <paramref name="suffix"/> is <see cref="SyntaxKind.Star"/>,
    /// <see cref="SyntaxKind.Question"/>, <see cref="SyntaxKind.Equals"/>, or
    /// <see cref="SyntaxKind.None"/> when the name stands alone.
    /// </summary>
    public static bool IsStateItem(string name, SyntaxKind suffix) => suffix switch
    {
        SyntaxKind.Star or SyntaxKind.Question => trackedStateParts.Contains(name),
        SyntaxKind.Equals => valuedStateParts.Contains(name),
        _ => pointStateItems.Contains(name),
    };

    /// <summary>Whether a token of this kind may begin an expression as a prefix operator.</summary>
    public static bool IsUnaryOperator(SyntaxKind kind) => kind is SyntaxKind.Plus or SyntaxKind.Minus
        or SyntaxKind.Tilde or SyntaxKind.Bang or SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret;

    /// <summary>
    /// The precedence level of a binary operator, 3 to 13 with 3 binding tightest, or 0
    /// when the token is not one. <c>.mod</c> is a directive rather than a punctuation token,
    /// because <c>%</c> begins a binary number.
    /// </summary>
    public static int BinaryPrecedence(GreenToken token) => token.Kind switch
    {
        SyntaxKind.Star or SyntaxKind.Slash => 3,
        SyntaxKind.Directive when token.Text.Equals(".mod", StringComparison.OrdinalIgnoreCase) => 3,
        SyntaxKind.Plus or SyntaxKind.Minus => 4,
        SyntaxKind.LessLess or SyntaxKind.GreaterGreater => 5,
        SyntaxKind.Less or SyntaxKind.LessEquals or SyntaxKind.Greater or SyntaxKind.GreaterEquals => 6,
        SyntaxKind.EqualsEquals or SyntaxKind.BangEquals => 7,
        SyntaxKind.Ampersand => 8,
        SyntaxKind.Caret => 9,
        SyntaxKind.Bar => 10,
        SyntaxKind.AmpersandAmpersand => 11,
        SyntaxKind.CaretCaret => 12,
        SyntaxKind.BarBar => 13,
        _ => 0,
    };

    /// <summary>The shifts and the bitwise operators, whose operands require parentheses around them.</summary>
    public static bool IsBitwiseOperator(SyntaxKind kind) => kind is SyntaxKind.LessLess
        or SyntaxKind.GreaterGreater or SyntaxKind.Ampersand or SyntaxKind.Caret or SyntaxKind.Bar;

    /// <summary>The logical operators, which may not be mixed without parentheses.</summary>
    public static bool IsLogicalOperator(SyntaxKind kind) =>
        kind is SyntaxKind.AmpersandAmpersand or SyntaxKind.CaretCaret or SyntaxKind.BarBar;

    /// <summary>The unary operators that take a byte out of an address, which are kept clear of binary operators.</summary>
    public static bool IsByteOperator(SyntaxKind kind) =>
        kind is SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret;

    /// <summary>Fixed token texts, for punctuation and operators.</summary>
    public static string? FixedText(SyntaxKind kind) => kind switch
    {
        SyntaxKind.ColonColon => "::",
        SyntaxKind.Colon => ":",
        SyntaxKind.Arrow => "->",
        SyntaxKind.DotDot => "..",
        SyntaxKind.Comma => ",",
        SyntaxKind.OpenParen => "(",
        SyntaxKind.CloseParen => ")",
        SyntaxKind.OpenBracket => "[",
        SyntaxKind.CloseBracket => "]",
        SyntaxKind.OpenBrace => "{",
        SyntaxKind.CloseBrace => "}",
        SyntaxKind.Hash => "#",
        SyntaxKind.Equals => "=",
        SyntaxKind.EqualsEquals => "==",
        SyntaxKind.Bang => "!",
        SyntaxKind.BangEquals => "!=",
        SyntaxKind.Less => "<",
        SyntaxKind.LessEquals => "<=",
        SyntaxKind.LessLess => "<<",
        SyntaxKind.Greater => ">",
        SyntaxKind.GreaterEquals => ">=",
        SyntaxKind.GreaterGreater => ">>",
        SyntaxKind.Plus => "+",
        SyntaxKind.Minus => "-",
        SyntaxKind.Star => "*",
        SyntaxKind.Slash => "/",
        SyntaxKind.Ampersand => "&",
        SyntaxKind.AmpersandAmpersand => "&&",
        SyntaxKind.Bar => "|",
        SyntaxKind.BarBar => "||",
        SyntaxKind.Caret => "^",
        SyntaxKind.CaretCaret => "^^",
        SyntaxKind.Tilde => "~",
        SyntaxKind.Question => "?",
        _ => null,
    };

    /// <summary>
    /// The canonical WDC mnemonics of the three CPUs. ca65's alternative 65816 spellings
    /// (<c>tad</c>, <c>tas</c>, <c>swa</c> and the rest) are ordinary identifiers.
    /// </summary>
    private static IEnumerable<string> CpuMnemonics()
    {
        string[] mos6502 =
        [
            "adc", "and", "asl", "bcc", "bcs", "beq", "bit", "bmi", "bne", "bpl", "brk", "bvc", "bvs",
            "clc", "cld", "cli", "clv", "cmp", "cpx", "cpy", "dec", "dex", "dey", "eor", "inc", "inx",
            "iny", "jmp", "jsr", "lda", "ldx", "ldy", "lsr", "nop", "ora", "pha", "php", "pla", "plp",
            "rol", "ror", "rti", "rts", "sbc", "sec", "sed", "sei", "sta", "stx", "sty", "tax", "tay",
            "tsx", "txa", "txs", "tya",
        ];

        // bbr0..bbr7 and friends are spelled with the bit number, as in ca65.
        string[] wdc65C02 =
        [
            "bra", "phx", "phy", "plx", "ply", "stz", "trb", "tsb", "stp", "wai",
            .. from bit in Enumerable.Range(0, 8)
               from op in new[] { "bbr", "bbs", "rmb", "smb" }
               select op + bit,
        ];

        string[] wdc65816 =
        [
            "brl", "cop", "jml", "jsl", "mvn", "mvp", "pea", "pei", "per", "phb", "phd", "phk", "plb",
            "pld", "rep", "rtl", "sep", "tcd", "tcs", "tdc", "tsc", "txy", "tyx", "wdm", "xba", "xce",
        ];

        return mos6502.Concat(wdc65C02).Concat(wdc65816);
    }
}
