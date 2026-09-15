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

    /// <summary>Whether <paramref name="text"/> is a mnemonic, whatever its case.</summary>
    public static bool IsMnemonic(ReadOnlySpan<char> text) => mnemonicSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    /// <summary>Whether <paramref name="text"/> is a register name, whatever its case.</summary>
    public static bool IsRegister(ReadOnlySpan<char> text) => registerSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    /// <summary>Whether an identifier may start with <paramref name="c"/>.</summary>
    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>Whether an identifier may continue with <paramref name="c"/>.</summary>
    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

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
