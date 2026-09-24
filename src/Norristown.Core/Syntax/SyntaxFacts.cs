using System.Collections.Frozen;

namespace Norristown.Syntax;

/// <summary>
/// Provides the reserved words of the language and the facts that determine what a line's tokens
/// mean.
/// </summary>
public static class SyntaxFacts
{
    // The contexts and nestings the directive table below combines. A declaration may appear
    // wherever items or code may. Bytes go in code or data. A condition or a repetition may
    // appear in any body that holds lines, including one of values.
    private const DirectiveContexts Declarations = DirectiveContexts.Items | DirectiveContexts.Code;
    private const DirectiveContexts Bytes = DirectiveContexts.Code | DirectiveContexts.Data;
    private const DirectiveContexts Bodies = Declarations | DirectiveContexts.Data | DirectiveContexts.Values;

    // A macro body and a repetition are expanded more than once, so neither may hold what is
    // declared once for the program. A routine may not hold another routine or a macro either.
    // What a module brings in with `.use` is part of its interface, so a `.use` goes where no
    // scope of names holds it, and a condition may test the processor, so none may hold `.cpu`.
    private const DirectiveNesting Expanded = DirectiveNesting.MacroBody | DirectiveNesting.Repetition;
    private const DirectiveNesting RoutineOrExpanded = DirectiveNesting.Routine | Expanded;

    // Static field initializers run in text order, so each field here is declared after the
    // fields its initializer reads.

    // The text of each mnemonic kind, indexed by the kind. The text of None is empty.
    private static readonly string[] mnemonicTexts =
        [.. Enum.GetValues<MnemonicKind>().Select(kind => kind == MnemonicKind.None ? "" : kind.ToString().ToLowerInvariant())];

    // The text of each directive kind, indexed by the kind. The text of None is empty.
    private static readonly string[] directiveTexts =
        [.. Enum.GetValues<DirectiveKind>().Select(kind => kind == DirectiveKind.None ? "" : "." + kind.ToString().ToLowerInvariant())];

    /// <summary>Every mnemonic, in the order <see cref="MnemonicKind"/> declares them.</summary>
    public static readonly IReadOnlyList<MnemonicKind> Mnemonics =
        [.. Enum.GetValues<MnemonicKind>().Where(kind => kind != MnemonicKind.None)];

    /// <summary>Every directive that may begin a line, in the order <see cref="DirectiveKind"/> declares them.</summary>
    public static readonly IReadOnlyList<DirectiveKind> Directives =
        [.. Enum.GetValues<DirectiveKind>().Where(kind => kind != DirectiveKind.None)];

    /// <summary>The register names, lower case.</summary>
    public static readonly IReadOnlyList<string> Registers = ["a", "x", "y", "s"];

    /// <summary>
    /// The registers a <c>keeps</c> item may name, lower case. The list includes the carry and
    /// leaves out the stack pointer, and <c>c</c> is an ordinary identifier everywhere else, so
    /// it is not the same set as the names the lexer treats as registers.
    /// </summary>
    public static readonly IReadOnlyList<string> KeptRegisters = ["a", "x", "y", "c"];

    // The text of each built-in kind, indexed by the kind. The text of None is empty.
    private static readonly string[] builtinTexts =
        [.. Enum.GetValues<BuiltinKind>().Select(kind => kind == BuiltinKind.None ? "" : "." + kind.ToString().ToLowerInvariant())];

    /// <summary>
    /// Every built-in function, in the order <see cref="BuiltinKind"/> declares them, with how many
    /// arguments each one takes and what each one allows. Any expression may call a function here
    /// unless it is marked as one only a macro body may call.
    /// </summary>
    public static readonly IReadOnlyList<BuiltinFunction> Builtins =
    [
        new(BuiltinKind.Lobyte, 1, 1, "one value", Arithmetic: true),
        new(BuiltinKind.Hibyte, 1, 1, "one value", Arithmetic: true),
        new(BuiltinKind.Bankbyte, 1, 1, "one value", Arithmetic: true),
        new(BuiltinKind.Loword, 1, 1, "one value", Arithmetic: true),
        new(BuiltinKind.Hiword, 1, 1, "one value", Arithmetic: true),
        new(BuiltinKind.Sizeof, 1, 1, "the name of a declaration"),
        new(BuiltinKind.Countof, 1, 1, "the name of a declaration"),
        new(BuiltinKind.Endof, 1, 1, "the name of a routine or data"),
        new(BuiltinKind.Spanof, 1, 1, "the name of a routine, data or a segment"),
        new(BuiltinKind.Loadof, 1, 1, "a segment"),
        new(BuiltinKind.Runof, 1, 1, "a segment"),
        new(BuiltinKind.Strlen, 1, 1, "one text", Arithmetic: true),
        new(BuiltinKind.Strat, 2, 2, "`.strat(text, index)`: a text and a number", Arithmetic: true),
        new(BuiltinKind.Strsub, 3, 3, "`.strsub(text, start, count)`: a text and two numbers", Arithmetic: true),
        new(BuiltinKind.Strcat, 1, null, "`.strcat(part, ...)`: at least one text or number", Arithmetic: true),
        new(BuiltinKind.Min, 2, 2, "two numbers", Arithmetic: true),
        new(BuiltinKind.Max, 2, 2, "two numbers", Arithmetic: true),
        new(BuiltinKind.Addrsize, 1, 1, "one expression"),
        new(BuiltinKind.Target, 1, 1, null),
        new(BuiltinKind.Defined, 1, 1, "one name"),
        new(BuiltinKind.Has, 1, 1, null),
        new(BuiltinKind.Select, 3, 3, null),
        new(BuiltinKind.Sqrt, 1, 1, "one number", Arithmetic: true),
        new(BuiltinKind.Muldiv, 3, 3, "`.muldiv(a, b, c)`", Arithmetic: true),
        new(BuiltinKind.Sin, 3, 3, "`.sin(angle, turn, scale)`", Arithmetic: true),
        new(BuiltinKind.Cos, 3, 3, "`.cos(angle, turn, scale)`", Arithmetic: true),
        new(BuiltinKind.Mincycles, 2, 2, "`.mincycles(from, to)`"),
        new(BuiltinKind.Maxcycles, 2, 2, "`.maxcycles(from, to)`"),
        new(BuiltinKind.Mode, 1, 1, "an `operand` parameter", MacroOnly: true),
        new(BuiltinKind.Byteof, 1, 2, "`.byteof(p, n)`: an `operand` parameter and an optional byte number", MacroOnly: true),
        new(BuiltinKind.Exprof, 1, 1, "an `operand` parameter", MacroOnly: true),
        new(BuiltinKind.Empty, 1, 1, "a `block` parameter", MacroOnly: true),
    ];

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

    private static readonly FrozenDictionary<string, BuiltinKind> builtinKinds =
        Builtins.ToFrozenDictionary(builtin => builtin.Name, builtin => builtin.Kind, StringComparer.OrdinalIgnoreCase);

    // Directives are case-insensitive, like mnemonics and registers.
    private static readonly FrozenDictionary<string, DirectiveKind> directiveKinds =
        Directives.ToFrozenDictionary(TextOf, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> registerSet = Registers.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> keptRegisterSet =
        KeptRegisters.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> cpuNameSet = CpuNames.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Every directive that may begin a line, one row each, so that adding a directive takes one
    // row here and one case in the parser. `.segment` parses to one of three kinds and `.proc` to
    // one of two; the rest of the line decides which.
    private static readonly FrozenDictionary<DirectiveKind, Directive> directiveRows = new Dictionary<DirectiveKind, Directive>
    {
        [DirectiveKind.Cpu] = new(
            SyntaxKind.CpuDirective, new(DirectiveContexts.Items, Expanded | DirectiveNesting.Condition)),
        [DirectiveKind.Segment] = new(SyntaxKind.SegmentDeclaration, new(Declarations, Expanded), BlockKind.Segment),
        [DirectiveKind.Data] = new(
            SyntaxKind.DataDeclaration, new(Declarations | DirectiveContexts.Data), BlockKind.Data, Exportable: true),
        [DirectiveKind.Proc] = new(SyntaxKind.ProcDeclaration, new(Declarations, RoutineOrExpanded), BlockKind.Proc, Exportable: true),
        [DirectiveKind.MultiProc] = new(
            SyntaxKind.MultiProcDeclaration, new(Declarations, RoutineOrExpanded), BlockKind.MultiProc, Exportable: true),
        [DirectiveKind.Scope] = new(SyntaxKind.ScopeDeclaration, new(Declarations), BlockKind.Scope, Exportable: true),
        [DirectiveKind.Export] = new(SyntaxKind.ExportDirective, new(Declarations, Expanded)),
        [DirectiveKind.Import] = new(SyntaxKind.ImportDirective, new(Declarations, Expanded), Exportable: true),
        [DirectiveKind.Module] = new(
            SyntaxKind.ModuleDirective, new(DirectiveContexts.Items, Required: DirectiveNesting.FirstLine)),
        [DirectiveKind.Place] = new(SyntaxKind.PlaceDirective, new(DirectiveContexts.Items, DirectiveNesting.PastFileLevel)),
        [DirectiveKind.Use] = new(SyntaxKind.UseDirective, new(Declarations, DirectiveNesting.NameScope), Exportable: true),

        // The element types give the size of one element. `.type T` is the only other element
        // type, and its size is that of T. Every multi-byte integer width has a big-endian
        // partner, so no lookup of which widths have one is needed.
        [DirectiveKind.Byte] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 1),
        [DirectiveKind.Word] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 2),
        [DirectiveKind.Long] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 3),
        [DirectiveKind.Dword] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 4),
        [DirectiveKind.BeWord] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 2),
        [DirectiveKind.BeLong] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 3),
        [DirectiveKind.BeDword] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 4),
        [DirectiveKind.Addr] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 2),
        [DirectiveKind.FarAddr] = new(SyntaxKind.DataDirective, new(Bytes), ElementSize: 3),
        [DirectiveKind.Res] = new(SyntaxKind.DataDirective, new(Bytes | DirectiveContexts.Items)),
        [DirectiveKind.Strz] = new(SyntaxKind.DataDirective, new(Bytes)),
        [DirectiveKind.Type] = new(SyntaxKind.DataDirective, new(Bytes), BlockKind.RecordInitializer),
        [DirectiveKind.Align] = new(SyntaxKind.DataDirective, new(Bytes | DirectiveContexts.Items)),
        [DirectiveKind.IncBin] = new(SyntaxKind.DataDirective, new(Bytes)),
        [DirectiveKind.LoBytes] = new(SyntaxKind.DataDirective, new(Bytes)),
        [DirectiveKind.HiBytes] = new(SyntaxKind.DataDirective, new(Bytes)),
        [DirectiveKind.BankBytes] = new(SyntaxKind.DataDirective, new(Bytes)),
        [DirectiveKind.Enum] = new(SyntaxKind.EnumDeclaration, new(Declarations), BlockKind.Enum, Exportable: true),
        [DirectiveKind.Struct] = new(
            SyntaxKind.StructDeclaration, new(Declarations | DirectiveContexts.TypeMembers), BlockKind.Struct, Exportable: true),
        [DirectiveKind.Union] = new(
            SyntaxKind.UnionDeclaration, new(Declarations | DirectiveContexts.TypeMembers), BlockKind.Union, Exportable: true),
        [DirectiveKind.Charmap] = new(SyntaxKind.CharmapDeclaration, new(Declarations), BlockKind.Charmap, Exportable: true),
        [DirectiveKind.List] = new(SyntaxKind.ListDeclaration, new(Declarations), BlockKind.List, Exportable: true),
        [DirectiveKind.Func] = new(SyntaxKind.FuncDeclaration, new(Declarations, Expanded), Exportable: true),
        [DirectiveKind.Signature] = new(SyntaxKind.SignatureDeclaration, new(Declarations, Expanded), Exportable: true),
        [DirectiveKind.Config] = new(
            SyntaxKind.ConfigDeclaration, new(DirectiveContexts.Items, DirectiveNesting.Block), Exportable: true),
        [DirectiveKind.If] = new(SyntaxKind.IfDirective, new(Bodies | DirectiveContexts.EnumMembers), BlockKind.If),
        [DirectiveKind.ElseIf] = new(SyntaxKind.ElseIfDirective, new(DirectiveContexts.None), BlockKind.If),
        [DirectiveKind.Else] = new(SyntaxKind.ElseDirective, new(DirectiveContexts.None), BlockKind.If),
        [DirectiveKind.Repeat] = new(SyntaxKind.RepeatDirective, new(Bodies), BlockKind.Repeat),
        [DirectiveKind.Each] = new(SyntaxKind.EachDirective, new(Bodies), BlockKind.Each),
        [DirectiveKind.Assert] = new(SyntaxKind.AssertDirective, new(Declarations)),
        [DirectiveKind.Error] = new(SyntaxKind.ErrorDirective, new(Declarations)),
        [DirectiveKind.Warning] = new(SyntaxKind.ErrorDirective, new(Declarations)),
        [DirectiveKind.Macro] = new(SyntaxKind.MacroDeclaration, new(Declarations, RoutineOrExpanded), BlockKind.Macro, Exportable: true),
        [DirectiveKind.Next] = new(SyntaxKind.NextDirective, new(DirectiveContexts.Code)),
        [DirectiveKind.Fallthrough] = new(
            SyntaxKind.FallthroughDirective, new(DirectiveContexts.Code, Expanded, DirectiveNesting.Routine)),
        [DirectiveKind.Patch] = new(SyntaxKind.PatchDirective, new(DirectiveContexts.Code)),
        [DirectiveKind.State] = new(SyntaxKind.StateDirective, new(DirectiveContexts.Code)),
        [DirectiveKind.Ensure] = new(SyntaxKind.EnsureDirective, new(DirectiveContexts.Code)),
        [DirectiveKind.Frame] = new(SyntaxKind.FrameDirective, new(DirectiveContexts.Code)),
    }.ToFrozenDictionary();

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
    public static (MnemonicKind Group, int Bit)? BitOf(MnemonicKind kind)
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

    /// <summary>
    /// Returns the directive <paramref name="text"/> names, in any letter case, or
    /// <see cref="DirectiveKind.None"/>.
    /// </summary>
    public static DirectiveKind DirectiveKindOf(ReadOnlySpan<char> text) =>
        directiveKinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out var kind) ? kind : DirectiveKind.None;

    /// <summary>
    /// Returns the lower-case text of <paramref name="kind"/>, such as <c>.byte</c>, or an empty
    /// string for <see cref="DirectiveKind.None"/>.
    /// </summary>
    public static string TextOf(DirectiveKind kind) => directiveTexts[(int)kind];

    /// <summary>Returns the kind of block a directive opens when its line ends in <c>{</c>.</summary>
    public static BlockKind BlockKindOf(DirectiveKind directive) =>
        directiveRows.TryGetValue(directive, out var row) ? row.Block : BlockKind.Unknown;

    /// <summary>
    /// Returns the kind of node a directive at the start of a line parses to, or
    /// <see cref="SyntaxKind.None"/>.
    /// </summary>
    public static SyntaxKind LineDirectiveKind(DirectiveKind directive) =>
        directiveRows.TryGetValue(directive, out var row) ? row.Kind : SyntaxKind.None;

    /// <summary>
    /// Checks whether <c>.export</c> may precede <paramref name="directive"/>. That is the case
    /// when the directive declares a name the linker must know. Such a name is a routine, data, a
    /// scope, a type, a constant of the language, or something another module brings in.
    /// </summary>
    public static bool IsExportable(DirectiveKind directive) =>
        directiveRows.TryGetValue(directive, out var row) && row.Exportable;

    /// <summary>
    /// Returns the size of one element of <paramref name="directive"/> when it is an element
    /// type, or null otherwise. The element types are <c>.byte</c>, <c>.word</c>, <c>.long</c>
    /// and <c>.dword</c>, their big-endian partners <c>.beword</c>, <c>.belong</c> and
    /// <c>.bedword</c>, and the addresses <c>.addr</c> and <c>.faraddr</c>.
    /// </summary>
    public static int? ElementSize(DirectiveKind directive) =>
        directiveRows.TryGetValue(directive, out var row) ? row.ElementSize : null;

    /// <summary>
    /// Returns where <paramref name="directive"/> may begin a line. A directive with no row, and
    /// <see cref="DirectiveKind.None"/>, may begin a line nowhere.
    /// </summary>
    public static DirectivePlacement PlacementOf(DirectiveKind directive) =>
        directiveRows.TryGetValue(directive, out var row) ? row.Placement : default;

    /// <summary>
    /// Returns what surrounds a line inside <paramref name="block"/>, combining that block and
    /// every block around it. A null block is a file's top level, which nothing surrounds. The
    /// body of a <c>.multiproc</c> is a routine that is repeated once for each member of its enum,
    /// so it counts as both. The result never includes <see cref="DirectiveNesting.FirstLine"/>,
    /// which depends on the line rather than on the blocks.
    /// </summary>
    public static DirectiveNesting NestingWithin(BlockSyntax? block)
    {
        var nesting = DirectiveNesting.None;
        for (SyntaxNode? at = block; at is not null; at = at.Parent)
        {
            if (at is not BlockSyntax around)
                continue;
            nesting |= DirectiveNesting.Block | around.BlockKind switch
            {
                BlockKind.Proc => DirectiveNesting.Routine | DirectiveNesting.NameScope,
                BlockKind.Macro => DirectiveNesting.MacroBody | DirectiveNesting.NameScope,
                BlockKind.Repeat or BlockKind.Each => DirectiveNesting.Repetition | DirectiveNesting.NameScope,
                BlockKind.MultiProc => DirectiveNesting.Routine | DirectiveNesting.Repetition | DirectiveNesting.NameScope,
                BlockKind.If => DirectiveNesting.Condition,
                BlockKind.MacroBlock or BlockKind.Scope or BlockKind.Data
                    or BlockKind.Enum or BlockKind.Struct or BlockKind.Union => DirectiveNesting.NameScope,
                _ => DirectiveNesting.None,
            };
            if (around.BlockKind != BlockKind.Region)
                nesting |= DirectiveNesting.PastFileLevel;
        }
        return nesting;
    }

    /// <summary>
    /// Returns what surrounds the line that holds <paramref name="statement"/>, combining every
    /// block the line is in. A line that opens a block is the first line in it, so that block
    /// counts too. As with <see cref="NestingWithin"/>, the result never includes
    /// <see cref="DirectiveNesting.FirstLine"/>.
    /// </summary>
    public static DirectiveNesting NestingOf(StatementSyntax statement) =>
        NestingWithin(statement.FirstAncestorOrSelf<LineSyntax>()?.Parent as BlockSyntax);

    /// <summary>Checks whether <paramref name="directive"/> names a built-in function, in any letter case.</summary>
    public static bool IsBuiltinFunction(ReadOnlySpan<char> directive) => BuiltinKindOf(directive) != BuiltinKind.None;

    /// <summary>
    /// Returns the built-in function <paramref name="text"/> names, in any letter case, or
    /// <see cref="BuiltinKind.None"/>.
    /// </summary>
    public static BuiltinKind BuiltinKindOf(ReadOnlySpan<char> text) =>
        builtinKinds.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out var kind) ? kind : BuiltinKind.None;

    /// <summary>
    /// Returns the name of <paramref name="kind"/> as it is spelled in source, such as
    /// <c>.sizeof</c>, or an empty string for <see cref="BuiltinKind.None"/>.
    /// </summary>
    public static string TextOf(BuiltinKind kind) => builtinTexts[(int)kind];

    /// <summary>Returns the row of <see cref="Builtins"/> that describes <paramref name="kind"/>.</summary>
    public static BuiltinFunction Builtin(BuiltinKind kind) =>
        kind == BuiltinKind.None ? throw new ArgumentOutOfRangeException(nameof(kind)) : Builtins[(int)kind - 1];

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
    /// Describes a directive at the start of a line. A row gives the node it parses to, where it may
    /// appear, the block it opens when its line ends in <c>{</c>, whether <c>.export</c> may precede
    /// it, and the size of one element when it is an element type. One row holds all of these
    /// facts, so they cannot drift apart.
    /// </summary>
    /// <param name="Kind">The kind of node the line parses to.</param>
    /// <param name="Placement">Where the directive may begin a line.</param>
    /// <param name="Block">The block it opens, or <see cref="BlockKind.Unknown"/> when it opens none.</param>
    /// <param name="Exportable">Whether <c>.export</c> before it exports what it declares.</param>
    /// <param name="ElementSize">The size of one element, or null when it is not an element type.</param>
    private readonly record struct Directive(
        SyntaxKind Kind, DirectivePlacement Placement, BlockKind Block = BlockKind.Unknown, bool Exportable = false,
        int? ElementSize = null);
}
