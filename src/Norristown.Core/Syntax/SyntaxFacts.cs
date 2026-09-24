using System.Collections.Frozen;

namespace Norristown.Syntax;

/// <summary>
/// Provides the reserved words of the language and the facts that determine what a line's tokens
/// mean.
/// </summary>
public static class SyntaxFacts
{
    // Static field initializers run in text order, so each field here is declared after the
    // fields its initializer reads.

    // The text of each mnemonic kind, indexed by the kind. The text of None is empty.
    private static readonly string[] mnemonicTexts =
        [.. Enum.GetValues<MnemonicKind>().Select(kind => kind == MnemonicKind.None ? "" : kind.ToString().ToLowerInvariant())];

    /// <summary>Every mnemonic, in the order <see cref="MnemonicKind"/> declares them.</summary>
    public static readonly IReadOnlyList<MnemonicKind> Mnemonics =
        [.. Enum.GetValues<MnemonicKind>().Where(kind => kind != MnemonicKind.None)];

    /// <summary>The register names, lower case.</summary>
    public static readonly IReadOnlyList<string> Registers = ["a", "x", "y", "s"];

    /// <summary>
    /// The registers a <c>keeps</c> item may name, lower case. The list includes the carry and
    /// leaves out the stack pointer, and <c>c</c> is an ordinary identifier everywhere else, so
    /// it is not the same set as the names the lexer treats as registers.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptRegisters = ["a", "x", "y", "c"];

    /// <summary>The built-in functions any expression may call.</summary>
    public static readonly IReadOnlyList<string> BuiltinFunctions =
    [
        ".lobyte", ".hibyte", ".bankbyte", ".loword", ".hiword", ".sizeof", ".countof",
        ".endof", ".spanof", ".loadof", ".runof", ".strlen", ".strat", ".strsub", ".strcat", ".min", ".max", ".addrsize", ".target",
        ".defined", ".has", ".select", ".sqrt", ".muldiv", ".sin", ".cos", ".mincycles", ".maxcycles",
    ];

    /// <summary>
    /// The four extra built-in functions available inside a macro body, which ask about the
    /// arguments the macro was given.
    /// </summary>
    public static readonly IReadOnlyList<string> MacroBuiltinFunctions = [".mode", ".byteof", ".exprof", ".empty"];

    /// <summary>
    /// The CPU names, lower case, in the order nt65 lists them. Which processors exist is not
    /// the lexer's concern, but the text of their names is. <c>.cpu</c> and <c>.target</c> take
    /// one of these names and nothing else. The names that start with a digit but are not numbers
    /// are lexed as CPU-name tokens, which no other rule would produce.
    /// </summary>
    public static readonly IReadOnlyList<string> CpuNames = ["6502", "6502x", "65sc02", "r65c02", "65c02", "65816"];

    /// <summary>
    /// The CPU names formatted as a message lists them, such as <c>`6502`, `6502x`, … or `65816`</c>.
    /// </summary>
    public static readonly string ListedCpuNames =
        string.Join(", ", CpuNames.SkipLast(1).Select(name => $"`{name}`")) + $" or `{CpuNames[^1]}`";

    private static readonly FrozenDictionary<string, MnemonicKind> mnemonicKinds =
        Mnemonics.ToFrozenDictionary(TextOf, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> registerSet = Registers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> keptRegisterSet =
        KeptRegisters.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> cpuNameSet = CpuNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Every directive that may begin a line, one row each, so that adding a directive takes one
    // line here and one case in the parser. Directives are case-insensitive, like mnemonics
    // and registers. `.segment` parses to one of three kinds and `.proc` to one of two; the rest
    // of the line decides which.
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
        [".fallthrough"] = new(SyntaxKind.FallthroughDirective),
        [".patch"] = new(SyntaxKind.PatchDirective),
        [".state"] = new(SyntaxKind.StateDirective),
        [".ensure"] = new(SyntaxKind.EnsureDirective),
        [".frame"] = new(SyntaxKind.FrameDirective),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The element types of data declarations and struct members, mapped to the size of one
    // element. `.type T` is the only other element type, and its size is that of T. Every
    // multi-byte integer width has a big-endian partner, so no lookup of which widths have one
    // is needed.
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

    // The processor-state items, grouped by the suffix that follows the name. A point item
    // stands alone. The suffix `*` keeps a part of the state unchanged, `?` forgets it, and `=`
    // gives it a value.
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

    /// <summary>The kinds of argument a macro parameter may take, as they appear in source.</summary>
    private static readonly FrozenSet<string> parameterKinds =
        new[] { "expr", "const", "ident", "operand", "block", "one", "list" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks whether <paramref name="text"/> is a mnemonic, in any letter case.</summary>
    public static bool IsMnemonic(ReadOnlySpan<char> text) => MnemonicKindOf(text) != MnemonicKind.None;

    /// <summary>
    /// Returns the mnemonic <paramref name="text"/> names, in any letter case, or
    /// <see cref="MnemonicKind.None"/>.
    /// </summary>
    public static MnemonicKind MnemonicKindOf(ReadOnlySpan<char> text) =>
        mnemonicKinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out var kind) ? kind : MnemonicKind.None;

    /// <summary>
    /// Returns the lower-case text of <paramref name="kind"/>, or an empty string for
    /// <see cref="MnemonicKind.None"/>.
    /// </summary>
    public static string TextOf(MnemonicKind kind) => mnemonicTexts[(int)kind];

    /// <summary>
    /// Checks whether <paramref name="kind"/> is one of the long branches, which reach any near
    /// target.
    /// </summary>
    public static bool IsLongBranch(MnemonicKind kind) => kind is >= MnemonicKind.Jeq and <= MnemonicKind.Jvc;

    /// <summary>
    /// Returns the instruction group and bit number of a Rockwell bit instruction, or null for
    /// every other mnemonic. The group is named by its bit-0 member, so <c>bbr3</c> gives
    /// <see cref="MnemonicKind.Bbr0"/> and 3.
    /// </summary>
    public static (MnemonicKind Family, int Bit)? BitOf(MnemonicKind kind)
    {
        if (kind is < MnemonicKind.Bbr0 or > MnemonicKind.Smb7)
            return null;
        var offset = kind - MnemonicKind.Bbr0;
        return (MnemonicKind.Bbr0 + (offset / 8 * 8), offset % 8);
    }

    /// <summary>Checks whether <paramref name="text"/> is a register name, in any letter case.</summary>
    public static bool IsRegister(ReadOnlySpan<char> text) => registerSet.GetAlternateLookup<ReadOnlySpan<char>>().Contains(text);

    /// <summary>Checks whether <paramref name="text"/> is a register a <c>keeps</c> item may name.</summary>
    public static bool IsKeptRegister(string text) => keptRegisterSet.Contains(text);

    /// <summary>
    /// Checks whether <paramref name="text"/> is a word that begins a state item, with any
    /// suffix. Such a word cannot name a signature set.
    /// </summary>
    public static bool IsStateWord(string text) => pointStateItems.Contains(text) || trackedStateParts.Contains(text);

    /// <summary>Checks whether <paramref name="text"/> names a kind of macro parameter.</summary>
    public static bool IsParameterKind(string text) => parameterKinds.Contains(text);

    /// <summary>Checks whether an identifier may start with <paramref name="c"/>.</summary>
    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>Checks whether an identifier may continue with <paramref name="c"/>.</summary>
    public static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Returns the kind of block a directive opens when its line ends in <c>{</c>.</summary>
    public static BlockKind BlockKindOfDirective(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) ? row.Block : BlockKind.Unknown;

    /// <summary>
    /// Returns the kind of node a directive at the start of a line parses to, or
    /// <see cref="SyntaxKind.None"/>.
    /// </summary>
    public static SyntaxKind LineDirectiveKind(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) ? row.Kind : SyntaxKind.None;

    /// <summary>
    /// Checks whether <c>.export</c> may precede <paramref name="directive"/>. That is the case
    /// when the directive declares a name the linker must know. Such a name is a routine, data, a
    /// scope, a type, a constant of the language, or something another module brings in.
    /// </summary>
    public static bool IsExportable(string directive) =>
        lineDirectives.TryGetValue(directive, out var row) && row.Exportable;

    /// <summary>
    /// Returns the size of one element of <paramref name="directive"/> when it is an element
    /// type, or null otherwise. The element types are <c>.byte</c>, <c>.word</c>, <c>.long</c>
    /// and <c>.dword</c>, their big-endian partners <c>.beword</c>, <c>.belong</c> and
    /// <c>.bedword</c>, and the addresses <c>.addr</c> and <c>.faraddr</c>.
    /// </summary>
    public static int? ElementSize(string directive) =>
        elementTypes.TryGetValue(directive, out var size) ? size : null;

    /// <summary>Checks whether <paramref name="directive"/> names a built-in function.</summary>
    public static bool IsBuiltinFunction(string directive) => builtinFunctions.Contains(directive);

    /// <summary>
    /// Checks whether <paramref name="text"/> is a level a ca65 <c>.assert</c> reports at. An nt65
    /// <c>.assert</c> does not take a level, because nt65 decides when a check can be made.
    /// </summary>
    public static bool IsAssertLevel(string text) =>
        text.Equals("warning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("error", StringComparison.OrdinalIgnoreCase)
        || text.Equals("ldwarning", StringComparison.OrdinalIgnoreCase)
        || text.Equals("lderror", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks whether <paramref name="text"/> is one of the CPU names.</summary>
    public static bool IsCpuName(string text) => cpuNameSet.Contains(text);

    /// <summary>
    /// Checks whether <paramref name="text"/> is an address size, which is <c>zp</c>, <c>abs</c> or
    /// <c>far</c>.
    /// </summary>
    public static bool IsAddressSize(string text) =>
        text.Equals("zp", StringComparison.OrdinalIgnoreCase)
        || text.Equals("abs", StringComparison.OrdinalIgnoreCase)
        || text.Equals("far", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether <paramref name="text"/> names an address-size prefix, which appears before
    /// a <c>:</c> in operand position. <c>z</c>, <c>f</c> and <c>d</c> are ordinary identifiers
    /// elsewhere, but nothing else can appear in that position, so reading them as prefixes
    /// there is safe. Case is ignored.
    /// </summary>
    public static bool IsAddressPrefix(string text) =>
        text.Length == 1 && char.ToLowerInvariant(text[0]) is 'z' or 'a' or 'f' or 'd';

    /// <summary>
    /// Checks whether <paramref name="name"/> followed by <paramref name="suffix"/> is a state item.
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

    /// <summary>Checks whether a token of this kind may begin an expression as a prefix operator.</summary>
    public static bool IsUnaryOperator(SyntaxKind kind) => kind is SyntaxKind.Plus or SyntaxKind.Minus
        or SyntaxKind.Tilde or SyntaxKind.Bang or SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret;

    /// <summary>
    /// Returns the precedence level of a binary operator, from 3 to 13 with 3 binding tightest,
    /// or 0 when the token is not a binary operator. <c>.mod</c> is a directive rather than a
    /// punctuation token, because <c>%</c> begins a binary number, so the text is needed to tell
    /// it from other directives.
    /// </summary>
    /// <param name="kind">The kind of the token.</param>
    /// <param name="text">The token's text, which distinguishes one directive from another.</param>
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

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a shift or a bitwise operator. The operands of
    /// these operators must be parenthesized when they use a different binary operator.
    /// </summary>
    public static bool IsBitwiseOperator(SyntaxKind kind) => kind is SyntaxKind.LessLess
        or SyntaxKind.GreaterGreater or SyntaxKind.Ampersand or SyntaxKind.Caret or SyntaxKind.Bar;

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a logical operator. Logical operators may not be
    /// mixed without parentheses.
    /// </summary>
    public static bool IsLogicalOperator(SyntaxKind kind) =>
        kind is SyntaxKind.AmpersandAmpersand or SyntaxKind.CaretCaret or SyntaxKind.BarBar;

    /// <summary>
    /// Checks whether <paramref name="kind"/> is a unary operator that takes one byte of an
    /// address. Such an operator at the right end of a binary operator's left operand must be
    /// parenthesized, since <c>&lt;label + 1</c> looks as if the <c>&lt;</c> applied to the whole
    /// sum.
    /// </summary>
    public static bool IsByteOperator(SyntaxKind kind) =>
        kind is SyntaxKind.Less or SyntaxKind.Greater or SyntaxKind.Caret;

    /// <summary>
    /// Returns the fixed text of a punctuation or operator token kind, or null for any other kind.
    /// </summary>
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
    /// Describes a directive at the start of a line. A row gives the node it parses to, the block
    /// it opens when its line ends in <c>{</c>, and whether <c>.export</c> may precede it. One row
    /// holds all three facts, so they cannot drift apart.
    /// </summary>
    /// <param name="Kind">The kind of node the line parses to.</param>
    /// <param name="Block">The block it opens, or <see cref="BlockKind.Unknown"/> when it opens none.</param>
    /// <param name="Exportable">Whether <c>.export</c> before it exports what it declares.</param>
    private readonly record struct Directive(
        SyntaxKind Kind, BlockKind Block = BlockKind.Unknown, bool Exportable = false);
}
