namespace Norristown.LanguageServer;

/// <summary>
/// Which directives may begin a line in each kind of place, and what each of them is for. A
/// directive the binder would reject where the caret is is not offered there, so what comes
/// back is what could be written and not merely what the language spells.
/// </summary>
internal static class Directives
{
    /// <summary>The element types a declaration, a member or a line of data is written with.</summary>
    public static readonly string[] Elements =
    [
        ".byte", ".word", ".long", ".dword", ".beword", ".belong", ".bedword", ".addr", ".faraddr", ".type",
    ];

    /// <summary>How wide an address is, where a declaration says so.</summary>
    public static readonly string[] Sizes = ["zp", "abs", "far"];

    /// <summary>Everything that may stand where data goes: the element types, and the rest of the bytes.</summary>
    public static readonly string[] Data =
    [
        .. Elements, ".res", ".align", ".incbin", ".strz", ".lobytes", ".hibytes", ".bankbytes",
    ];

    /// <summary>What a file's top level holds, past the <c>.module</c> its first line may carry.</summary>
    private static readonly string[] Items =
    [
        ".cpu", ".config", ".use", ".import", ".export", ".segment", ".proc", ".multiproc", ".scope", ".macro",
        ".func", ".signature", ".data", ".enum", ".struct", ".union", ".charmap", ".list", ".if", ".repeat",
        ".each", ".assert", ".error", ".warning", ".res", ".align",
    ];

    /// <summary>What only code holds: what the processor state is, and where control goes.</summary>
    private static readonly string[] CodeOnly = [".state", ".ensure", ".frame", ".next", ".patch"];

    /// <summary>What a macro body may not declare, because a macro is expanded rather than written once.</summary>
    private static readonly string[] NotInMacro =
        [".cpu", ".export", ".import", ".segment", ".proc", ".multiproc", ".macro", ".func", ".signature"];

    /// <summary>What a repetition may not declare, because every turn would declare it again.</summary>
    private static readonly string[] NotInRepetition =
        [".cpu", ".config", ".export", ".import", ".segment", ".proc", ".multiproc", ".macro", ".func", ".signature"];

    /// <summary>What a directive is for, in the few words a completion shows beside it.</summary>
    private static readonly Dictionary<string, string> details = new(StringComparer.Ordinal)
    {
        [".addr"] = "two-byte addresses",
        [".align"] = "pad to a multiple of",
        [".assert"] = "check that something holds",
        [".bankbytes"] = "the bank byte of each address",
        [".bedword"] = "big-endian four-byte values",
        [".belong"] = "big-endian three-byte values",
        [".beword"] = "big-endian two-byte values",
        [".byte"] = "one-byte values",
        [".charmap"] = "what each character assembles to",
        [".config"] = "a setting this build reads",
        [".cpu"] = "the processor the program is built for",
        [".data"] = "a data declaration",
        [".dword"] = "four-byte values",
        [".each"] = "write the block once for each item",
        [".else"] = "what to assemble instead",
        [".elseif"] = "another condition to try",
        [".ensure"] = "the widths the code from here needs",
        [".enum"] = "a set of named constants",
        [".error"] = "fail the build with a message",
        [".export"] = "make a name visible to other modules",
        [".faraddr"] = "three-byte addresses",
        [".frame"] = "the stack frame the code from here works in",
        [".func"] = "a function of its arguments",
        [".hibytes"] = "the high byte of each value",
        [".if"] = "assemble the block when a condition holds",
        [".import"] = "a name from outside the program",
        [".incbin"] = "the bytes of a file",
        [".list"] = "a list of values to repeat over",
        [".lobytes"] = "the low byte of each value",
        [".long"] = "three-byte values",
        [".macro"] = "a macro",
        [".module"] = "the module this file is part of",
        [".multiproc"] = "one routine per member of an enum",
        [".next"] = "where control goes from here",
        [".patch"] = "the target this instruction is patched to",
        [".proc"] = "a routine",
        [".repeat"] = "write the block a number of times",
        [".res"] = "reserve bytes",
        [".scope"] = "a scope of names",
        [".segment"] = "declare a segment, or place what follows in one",
        [".signature"] = "a named set of processor-state items",
        [".state"] = "what the processor state is here",
        [".strz"] = "text with a terminating zero",
        [".struct"] = "a structure",
        [".type"] = "values of a declared type",
        [".union"] = "a union",
        [".use"] = "bring another module's names in",
        [".warning"] = "report a message",
        [".word"] = "two-byte values",
    };

    /// <summary>
    /// Every directive these lists may offer anywhere. Nothing in the language ties them to
    /// what the binder accepts, so this is what a test holds them to.
    /// </summary>
    public static IEnumerable<string> All => details.Keys;

    /// <summary>What every directive in <paramref name="names"/> is for, ready to be offered.</summary>
    public static IEnumerable<(string Name, string Detail)> Described(IEnumerable<string> names) =>
        names.Select(name => (name, details.GetValueOrDefault(name, "directive")));

    /// <summary>The directives that may begin the statement the caret is at the start of.</summary>
    public static IEnumerable<(string Name, string Detail)> At(LineContext line)
    {
        string[] names = line.Place switch
        {
            Place.Item => line.IsFirstLine ? [.. Items, ".module"] : Items,
            Place.Code or Place.Unknown => InCode(line),
            Place.Data => [.. Data, ".data", ".if", ".repeat", ".each"],
            Place.Values => [".if", ".repeat", ".each"],
            Place.TypeMembers => [".struct", ".union"],
            Place.EnumMembers => [".if"],
            _ => [],
        };

        // What a routine holds is what a file holds and what data holds, and the two lists
        // share `.res` and `.align`: one directive is offered once.
        return Described(Without(names, line).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// What a <c>.proc</c> body, a macro body or a block argument holds: what a file holds,
    /// the data a routine carries with it, and what only code says.
    /// </summary>
    private static string[] InCode(LineContext line) =>
        line.Place == Place.Unknown
            ? [.. Items, ".module", .. Data, .. CodeOnly]
            : [.. Items.Except([".cpu", ".config"], StringComparer.Ordinal), .. Data, .. CodeOnly];

    /// <summary>What the blocks around the line rule out of <paramref name="names"/>.</summary>
    private static IEnumerable<string> Without(IEnumerable<string> names, LineContext line)
    {
        if (line.Place == Place.Unknown)
            return names;
        var barred = new HashSet<string>(StringComparer.Ordinal);

        // What a module brings in is part of its interface, so a `.use` belongs where the
        // interface is: at the top level, not under a block.
        if (line.InProc || line.InMacro || line.InRepetition)
            barred.Add(".use");

        // A macro declared in a routine would see its cheap locals, and a routine inside one
        // is code the outer routine's flow analysis cannot follow.
        if (line.InProc)
        {
            barred.Add(".proc");
            barred.Add(".multiproc");
            barred.Add(".macro");
        }
        if (line.InMacro)
            barred.UnionWith(NotInMacro);
        if (line.InRepetition)
            barred.UnionWith(NotInRepetition);
        return names.Where(name => !barred.Contains(name));
    }
}
