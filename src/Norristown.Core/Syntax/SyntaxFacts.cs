using System.Collections.Frozen;

namespace Norristown.Syntax;

/// <summary>The reserved words of the language, and what a line's tokens mean.</summary>
public static class SyntaxFacts
{
    // Static field initializers run in text order, so each field here is declared after the
    // fields its initializer reads.

    // The Rockwell bit instructions, each of which is spelled with a bit number after it.
    private static readonly string[] BitOps = ["bbr", "bbs", "rmb", "smb"];

    /// <summary>The long branches, which reach any near target.</summary>
    public static readonly FrozenSet<string> LongBranches =
        new[] { "jeq", "jne", "jcs", "jcc", "jmi", "jpl", "jvs", "jvc" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every reserved mnemonic, lower case, in a fixed order.</summary>
    public static readonly IReadOnlyList<string> Mnemonics =
        [.. CpuMnemonics().Concat(LongBranches.Order(StringComparer.Ordinal)).Distinct()];

    /// <summary>The register names, lower case.</summary>
    public static readonly IReadOnlyList<string> Registers = ["a", "x", "y", "s"];

    /// <summary>
    /// The registers a <c>keeps</c> item may name, lower case. The carry is among them and the
    /// stack pointer is not, and <c>c</c> is an ordinary identifier everywhere else, so what
    /// these are is not what the lexer calls a register.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptRegisters = ["a", "x", "y", "c"];

    /// <summary>The built-in functions any expression may call.</summary>
    public static readonly IReadOnlyList<string> BuiltinFunctions =
    [
        ".lobyte", ".hibyte", ".bankbyte", ".loword", ".hiword", ".sizeof", ".countof",
        ".endof", ".spanof", ".loadof", ".runof", ".strlen", ".strat", ".min", ".max", ".addrsize", ".target",
        ".defined", ".has", ".select", ".sqrt", ".muldiv", ".sin", ".cos", ".mincycles", ".maxcycles",
    ];

    /// <summary>The four a macro body adds, which ask about the arguments it was given.</summary>
    public static readonly IReadOnlyList<string> MacroBuiltinFunctions = [".mode", ".byteof", ".exprof", ".empty"];

    /// <summary>
    /// The CPU names, lower case, in the order nt65 lists them. Which processors there are is
    /// not the lexer's business, but how one is written is: <c>.cpu</c> and <c>.target</c> take
    /// a name here and nothing else, and two of them are tokens no other rule would make.
    /// </summary>
    public static readonly IReadOnlyList<string> CpuNames = ["6502", "6502x", "65sc02", "r65c02", "65c02", "65816"];

    /// <summary>The names, as a message lists them: <c>`6502`, `6502x`, … or `65816`</c>.</summary>
    public static readonly string ListedCpuNames =
        string.Join(", ", CpuNames.SkipLast(1).Select(name => $"`{name}`")) + $" or `{CpuNames[^1]}`";

    private static readonly FrozenSet<string> mnemonicSet = Mnemonics.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> registerSet = Registers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> keptRegisterSet =
        KeptRegisters.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> cpuNameSet = CpuNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Every directive that may begin a line, one row each, so that adding a directive is one
    // line here and one case in the parser. Directives are case-insensitive, like mnemonics
    // and registers. `.segment` parses to one of three kinds and `.proc` to one of two,
    // decided by the rest of the line.
    private static readonly FrozenDictionary<string, Directive> lineDirectives = new Dictionary<string, Directive>
    {
        [".cpu"] = new(SyntaxKind.CpuDirective),
        [".segment"] = new(SyntaxKind.SegmentDeclaration, BlockKind.Segment),
        [".data"] = new(SyntaxKind.DataDeclaration, BlockKind.Data, Exportable: true),
        [".proc"] = new(SyntaxKind.ProcDeclaration, BlockKind.Proc, Exportable: true),
        [".multiproc"] = new(SyntaxKind.MultiProcDeclaration, BlockKind.MultiProc, Exportable: true),
        [".scope"] = new(SyntaxKind.ScopeDeclaration, BlockKind.Scope, Exportable: true),
        [".export"] = new(SyntaxKind.ExportDirective),
        [".import"] = new(SyntaxKind.ImportDirective, Exportable: true),
        [".module"] = new(SyntaxKind.ModuleDirective),
        [".place"] = new(SyntaxKind.PlaceDirective),
        [".use"] = new(SyntaxKind.UseDirective, Exportable: true),
        [".byte"] = new(SyntaxKind.DataDirective),
        [".word"] = new(SyntaxKind.DataDirective),
        [".long"] = new(SyntaxKind.DataDirective),
        [".dword"] = new(SyntaxKind.DataDirective),
        [".beword"] = new(SyntaxKind.DataDirective),
        [".belong"] = new(SyntaxKind.DataDirective),
        [".bedword"] = new(SyntaxKind.DataDirective),
        [".addr"] = new(SyntaxKind.DataDirective),
        [".faraddr"] = new(SyntaxKind.DataDirective),
        [".res"] = new(SyntaxKind.DataDirective),
        [".strz"] = new(SyntaxKind.DataDirective),
        [".type"] = new(SyntaxKind.DataDirective, BlockKind.RecordInitializer),
        [".align"] = new(SyntaxKind.DataDirective),
        [".incbin"] = new(SyntaxKind.DataDirective),
        [".lobytes"] = new(SyntaxKind.DataDirective),
        [".hibytes"] = new(SyntaxKind.DataDirective),
        [".bankbytes"] = new(SyntaxKind.DataDirective),
        [".enum"] = new(SyntaxKind.EnumDeclaration, BlockKind.Enum, Exportable: true),
        [".struct"] = new(SyntaxKind.StructDeclaration, BlockKind.Struct, Exportable: true),
        [".union"] = new(SyntaxKind.UnionDeclaration, BlockKind.Union, Exportable: true),
        [".charmap"] = new(SyntaxKind.CharmapDeclaration, BlockKind.Charmap, Exportable: true),
        [".list"] = new(SyntaxKind.ListDeclaration, BlockKind.List, Exportable: true),
        [".func"] = new(SyntaxKind.FuncDeclaration, Exportable: true),
        [".signature"] = new(SyntaxKind.SignatureDeclaration, Exportable: true),
        [".config"] = new(SyntaxKind.ConfigDeclaration, Exportable: true),
        [".if"] = new(SyntaxKind.IfDirective, BlockKind.If),
        [".elseif"] = new(SyntaxKind.ElseIfDirective, BlockKind.If),
        [".else"] = new(SyntaxKind.ElseDirective, BlockKind.If),
        [".repeat"] = new(SyntaxKind.RepeatDirective, BlockKind.Repeat),
        [".each"] = new(SyntaxKind.EachDirective, BlockKind.Each),
        [".assert"] = new(SyntaxKind.AssertDirective),
        [".error"] = new(SyntaxKind.ErrorDirective),
        [".warning"] = new(SyntaxKind.ErrorDirective),
        [".macro"] = new(SyntaxKind.MacroDeclaration, BlockKind.Macro, Exportable: true),
        [".next"] = new(SyntaxKind.NextDirective),
        [".patch"] = new(SyntaxKind.PatchDirective),
        [".state"] = new(SyntaxKind.StateDirective),
        [".ensure"] = new(SyntaxKind.EnsureDirective),
        [".frame"] = new(SyntaxKind.FrameDirective),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The element types a data declaration or a struct member is an array of, by the size of
    // one element. `.type T` is the other, whose size is its type's. Every width has a
    // big-endian partner, so nobody wonders which widths have one.
    private static readonly FrozenDictionary<string, int> elementTypes = new Dictionary<string, int>
    {
        [".byte"] = 1,
        [".word"] = 2,
        [".addr"] = 2,
        [".long"] = 3,
        [".faraddr"] = 3,
        [".dword"] = 4,
        [".beword"] = 2,
        [".belong"] = 3,
        [".bedword"] = 4,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> builtinFunctions =
        BuiltinFunctions.Concat(MacroBuiltinFunctions).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The processor-state items, by the suffix that follows the name: a point item stands
    // alone, `*` keeps a part of the state unchanged, `?` forgets it and `=` gives a value.
    private static readonly FrozenSet<string> pointStateItems =
        new[]
        {
            "a8", "a16", "i8", "i16", "native", "emu", "near", "far", "inline", "args", "interrupt",
            "noreturn", "keeps",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Whether <paramref name="text"/> is a register a <c>keeps</c> item may name.</summary>
    public static bool IsKeptRegister(string text) => keptRegisterSet.Contains(text);

    /// <summary>
    /// Whether <paramref name="text"/> is a word a state item is spelled with, whatever
    /// follows it, and so cannot name a signature set.
    /// </summary>
    public static bool IsStateWord(string text) => pointStateItems.Contains(text) || trackedStateParts.Contains(text);

    /// <summary>Whether <paramref name="text"/> names a kind of macro parameter.</summary>
    public static bool IsParameterKind(string text) => parameterKinds.Contains(text);

    /// <summary>Whether an identifier may start with <paramref name="c"/>.</summary>
    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>Whether an identifier may continue with <paramref name="c"/>.</summary>
    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>The kind of block a directive opens when its line ends in <c>{</c>.</summary>
    public static BlockKind BlockKindOfDirective(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) ? row.Block : BlockKind.Unknown;

    /// <summary>The kind of node a directive at the start of a line parses to, or <see cref="SyntaxKind.None"/>.</summary>
    public static SyntaxKind LineDirectiveKind(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) ? row.Kind : SyntaxKind.None;

    /// <summary>
    /// Whether <c>.export</c> may go before <paramref name="directive"/>, which is whether it
    /// declares a name for the linker to know: a routine, data, a scope, a type, a constant of
    /// the language, or what another module brings in.
    /// </summary>
    public static bool IsExportable(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) && row.Exportable;

    /// <summary>
    /// The size of one element of <paramref name="directive"/>, when it is an element type:
    /// <c>.byte</c>, <c>.word</c>, <c>.long</c>, <c>.dword</c>, their big-endian partners
    /// <c>.beword</c>, <c>.belong</c> and <c>.bedword</c>, and the addresses <c>.addr</c> and
    /// <c>.faraddr</c>.
    /// </summary>
    public static int? ElementSize(string directive) =>
        elementTypes.TryGetValue(directive, out var size) ? size : null;

    /// <summary>Whether <paramref name="directive"/> names a built-in function.</summary>
    public static bool IsBuiltinFunction(string directive) => builtinFunctions.Contains(directive);

    /// <summary>
    /// Whether <paramref name="text"/> is a level a ca65 <c>.assert</c> reports at, which an nt65
    /// one does not take: nt65 decides when a check can be made.
    /// </summary>
    public static bool IsAssertLevel(string text) =>
        text.Equals("warning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("error", StringComparison.OrdinalIgnoreCase)
        || text.Equals("ldwarning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("lderror", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="text"/> is one of the CPU names.</summary>
    public static bool IsCpuName(string text) => cpuNameSet.Contains(text);

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
    /// because <c>%</c> begins a binary number, so which directive it is has to be said as well.
    /// </summary>
    /// <param name="kind">What the token is.</param>
    /// <param name="text">The token's text, which tells one directive from another.</param>
    public static int BinaryPrecedence(SyntaxKind kind, string text) => kind switch
    {
        SyntaxKind.Star or SyntaxKind.Slash => 3,
        SyntaxKind.Directive when text.Equals(".mod", StringComparison.OrdinalIgnoreCase) => 3,
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
    /// The canonical WDC mnemonics of the CPUs, and the undocumented opcodes of the NMOS 6502
    /// in ca65's spellings, since those have no canonical name of their own. ca65's alternative
    /// 65816 spellings (<c>tad</c>, <c>swa</c> and the rest) are ordinary identifiers; ca65's
    /// <c>tas</c> is not one of those, and is an undocumented opcode below.
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

        string[] mos6502X =
        [
            "alr", "anc", "ane", "arr", "axs", "dcp", "isc", "jam", "las", "lax", "rla", "rra",
            "sax", "sha", "shx", "shy", "slo", "sre", "tas",
        ];

        // bbr0..bbr7 and friends are spelled with the bit number, as in ca65.
        string[] wdc65C02 =
        [
            "bra", "phx", "phy", "plx", "ply", "stz", "trb", "tsb", "stp", "wai",
            .. from bit in Enumerable.Range(0, 8)
               from op in BitOps
               select op + bit,
        ];

        string[] wdc65816 =
        [
            "brl", "cop", "jml", "jsl", "mvn", "mvp", "pea", "pei", "per", "phb", "phd", "phk", "plb",
            "pld", "rep", "rtl", "sep", "tcd", "tcs", "tdc", "tsc", "txy", "tyx", "wdm", "xba", "xce",
        ];

        return mos6502.Concat(mos6502X).Concat(wdc65C02).Concat(wdc65816);
    }

    /// <summary>
    /// What a directive at the start of a line is: the node it parses to, the block it opens
    /// where its line ends in <c>{</c>, and whether <c>.export</c> may go before it. One row
    /// says all three, so the three questions cannot drift apart.
    /// </summary>
    /// <param name="Kind">The node the line parses to.</param>
    /// <param name="Block">The block it opens, or <see cref="BlockKind.Unknown"/> where it opens none.</param>
    /// <param name="Exportable">Whether <c>.export</c> before it exports what it declares.</param>
    private readonly record struct Directive(
        SyntaxKind Kind, BlockKind Block = BlockKind.Unknown, bool Exportable = false);
}
