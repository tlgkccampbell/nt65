using System.Collections.Frozen;

namespace Norristown.Syntax;

public static class SyntaxFacts
{
    // The canonical WDC mnemonics of the three CPUs. ca65's alternative spellings for the
    // 65816 (tad, tas, swa, ...) are not included, so they stay ordinary identifiers; the
    // design reserves "all mnemonics", and the canonical set is the smallest reading of that.
    private static readonly string[] Mos6502 =
    [
        "adc", "and", "asl", "bcc", "bcs", "beq", "bit", "bmi", "bne", "bpl", "brk", "bvc", "bvs",
        "clc", "cld", "cli", "clv", "cmp", "cpx", "cpy", "dec", "dex", "dey", "eor", "inc", "inx",
        "iny", "jmp", "jsr", "lda", "ldx", "ldy", "lsr", "nop", "ora", "pha", "php", "pla", "plp",
        "rol", "ror", "rti", "rts", "sbc", "sec", "sed", "sei", "sta", "stx", "sty", "tax", "tay",
        "tsx", "txa", "txs", "tya",
    ];

    // bbr0..bbr7 and friends are spelled with the bit number, as in ca65.
    private static readonly string[] Wdc65C02 =
    [
        "bra", "phx", "phy", "plx", "ply", "stz", "trb", "tsb", "stp", "wai",
        .. from bit in Enumerable.Range(0, 8)
           from op in new[] { "bbr", "bbs", "rmb", "smb" }
           select op + bit,
    ];

    private static readonly string[] Wdc65816 =
    [
        "brl", "cop", "jml", "jsl", "mvn", "mvp", "pea", "pei", "per", "phb", "phd", "phk", "plb",
        "pld", "rep", "rtl", "sep", "tcd", "tcs", "tdc", "tsc", "txy", "tyx", "wdm", "xba", "xce",
    ];

    /// <summary>Long branches (§7.6).</summary>
    public static readonly FrozenSet<string> LongBranches =
        new[] { "jeq", "jne", "jcs", "jcc", "jmi", "jpl", "jvs", "jvc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every reserved mnemonic, lower case, in a fixed order.</summary>
    public static readonly IReadOnlyList<string> Mnemonics =
        [.. Mos6502.Concat(Wdc65C02).Concat(Wdc65816).Concat(LongBranches.Order(StringComparer.Ordinal)).Distinct()];

    private static readonly FrozenSet<string> mnemonicSet = Mnemonics.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> Registers = ["a", "x", "y", "s"];

    private static readonly FrozenSet<string> registerSet = Registers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsMnemonic(ReadOnlySpan<char> text) => mnemonicSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    public static bool IsRegister(ReadOnlySpan<char> text) => registerSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    // Directives are case-insensitive, like mnemonics and registers (the design does not say;
    // ca65's are case-insensitive, and "same spelling, same meaning" favours keeping that).
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

    /// <summary>The kind of block a directive opens when its line ends in <c>{</c>.</summary>
    public static BlockKind BlockKindOfDirective(string directive) =>
        blockDirectives.GetValueOrDefault(directive, BlockKind.Unknown);

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
}
