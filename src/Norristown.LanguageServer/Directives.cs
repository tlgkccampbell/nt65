namespace Norristown.LanguageServer;

/// <summary>
/// Lists the directives that may begin a line in each kind of context, and describes what each
/// is for. A directive the binder would reject at the caret is not offered there, so the result
/// is what could actually be used there, not merely every directive the language has.
/// </summary>
internal static class Directives
{
    /// <summary>The element types that a declaration, a member or a line of data may use.</summary>
    public static readonly string[] Elements =
    [
        ".byte", ".word", ".long", ".dword", ".beword", ".belong", ".bedword", ".addr", ".faraddr", ".type",
    ];

    /// <summary>The address widths a declaration may specify.</summary>
    public static readonly string[] Sizes = ["zp", "abs", "far"];

    /// <summary>
    /// The directives that may appear where data goes, which are the element types and the other
    /// directives that produce bytes.
    /// </summary>
    public static readonly string[] Data =
    [
        .. Elements, ".res", ".align", ".incbin", ".strz", ".lobytes", ".hibytes", ".bankbytes",
    ];

    /// <summary>The directives a file's top level may hold, besides the <c>.module</c> its first line may carry.</summary>
    private static readonly string[] Items =
    [
        ".cpu", ".config", ".use", ".import", ".export", ".segment", ".proc", ".multiproc", ".scope", ".macro",
        ".func", ".signature", ".data", ".enum", ".struct", ".union", ".charmap", ".list", ".if", ".repeat",
        ".each", ".assert", ".error", ".warning", ".res", ".align", ".place",
    ];

    /// <summary>
    /// The directives that only code may hold, which declare the processor state or where control
    /// goes.
    /// </summary>
    private static readonly string[] CodeOnly = [".state", ".ensure", ".frame", ".next", ".fallthrough", ".patch"];

    /// <summary>
    /// The directives a macro body may not contain, because a macro body is expanded at every call
    /// rather than assembled once.
    /// </summary>
    private static readonly string[] NotInMacro =
        [".cpu", ".export", ".import", ".segment", ".proc", ".multiproc", ".macro", ".func", ".signature"];

    /// <summary>
    /// The directives a repetition may not contain, because every iteration would declare the same
    /// thing again.
    /// </summary>
    private static readonly string[] NotInRepetition =
        [".cpu", ".config", ".export", ".import", ".segment", ".proc", ".multiproc", ".macro", ".func", ".signature"];

    /// <summary>The short description of each directive that a completion shows beside it.</summary>
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
        [".fallthrough"] = "the routine this one runs into",
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
        [".place"] = "another module's bytes, written here",
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
    /// Gets every directive these lists may offer anywhere. Nothing in the code ties these lists
    /// to what the binder accepts, so a test uses this property to check them against the binder.
    /// </summary>
    public static IEnumerable<string> All => details.Keys;

    /// <summary>
    /// Pairs every directive in <paramref name="names"/> with a description of what it is for,
    /// ready to be offered.
    /// </summary>
    public static IEnumerable<(string Name, string Detail)> Described(IEnumerable<string> names) =>
        names.Select(name => (name, details.GetValueOrDefault(name, "directive")));

    /// <summary>Returns the directives that may begin the statement the caret is at the start of.</summary>
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

        // Code may hold both the top-level and the data directives, and those two lists share
        // `.res` and `.align`, so duplicates are removed.
        return Described(Without(names, line).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns the directives a <c>.proc</c> body, a macro body or a block argument may hold.
    /// These are the top-level directives except <c>.cpu</c>, <c>.config</c> and <c>.place</c>,
    /// the data directives, and the code-only directives. In a block of unknown kind every
    /// directive is offered, including <c>.module</c>.
    /// </summary>
    private static string[] InCode(LineContext line) =>
        line.Place == Place.Unknown
            ? [.. Items, ".module", .. Data, .. CodeOnly]
            : [.. Items.Except([".cpu", ".config", ".place"], StringComparer.Ordinal), .. Data, .. CodeOnly];

    /// <summary>
    /// Removes from <paramref name="names"/> the directives that the blocks around the line rule
    /// out.
    /// </summary>
    private static IEnumerable<string> Without(IEnumerable<string> names, LineContext line)
    {
        if (line.Place == Place.Unknown)
            return names;
        var barred = new HashSet<string>(StringComparer.Ordinal);

        // What a module brings in is part of its interface, so a `.use` belongs where the
        // interface is: at the top level, not under a block.
        if (line.InProc || line.InMacro || line.InRepetition)
            barred.Add(".use");

        // A `.config` declares a setting the build supplies, so it must appear where nothing
        // decides whether it is read at all. That means outside every block, including an open
        // `.segment` region, which counts as a block for any line under it.
        if (line.InBlock)
            barred.Add(".config");

        // Which modules share a translation unit is structural and read from the files alone,
        // so a `.place` must be at file level. Unlike for `.config`, an open `.segment` region
        // does not count as a block here.
        if (!line.AtFileLevel)
            barred.Add(".place");

        // A `.fallthrough` is the last line of a routine's own body, or of a branch of an `.if`
        // chain that ends it. Neither a macro body nor a repetition is a routine's body.
        if (!line.InProc || line.InMacro || line.InRepetition)
            barred.Add(".fallthrough");

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
