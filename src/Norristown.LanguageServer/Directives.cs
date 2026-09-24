using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Lists the directives that may begin a line in each kind of context, and describes what each
/// is for. Where a directive may appear comes from its <see cref="DirectivePlacement"/>, which the
/// binder reads too, so a directive the binder would reject at the caret is not offered there.
/// </summary>
internal static class Directives
{
    /// <summary>The element types that a declaration, a member or a line of data may use.</summary>
    public static readonly string[] Elements =
    [
        .. SyntaxFacts.Directives
            .Where(kind => SyntaxFacts.ElementSize(kind) is not null || kind == DirectiveKind.Type)
            .Select(SyntaxFacts.TextOf),
    ];

    /// <summary>The address widths a declaration may specify.</summary>
    public static readonly string[] Sizes = ["zp", "abs", "far"];

    /// <summary>
    /// The directives that may appear where data goes, which are the element types and the other
    /// directives that produce bytes.
    /// </summary>
    public static readonly string[] Data =
    [
        .. SyntaxFacts.Directives
            .Where(kind => SyntaxFacts.LineDirectiveKind(kind) == SyntaxKind.DataDirective)
            .Select(SyntaxFacts.TextOf),
    ];

    /// <summary>The short description of each directive that a completion shows beside it.</summary>
    private static readonly Dictionary<DirectiveKind, string> details = new()
    {
        [DirectiveKind.Addr] = "two-byte addresses",
        [DirectiveKind.Align] = "pad to a multiple of",
        [DirectiveKind.Assert] = "check that something holds",
        [DirectiveKind.BankBytes] = "the bank byte of each address",
        [DirectiveKind.BeDword] = "big-endian four-byte values",
        [DirectiveKind.BeLong] = "big-endian three-byte values",
        [DirectiveKind.BeWord] = "big-endian two-byte values",
        [DirectiveKind.Byte] = "one-byte values",
        [DirectiveKind.Charmap] = "what each character assembles to",
        [DirectiveKind.Config] = "a setting this build reads",
        [DirectiveKind.Cpu] = "the processor the program is built for",
        [DirectiveKind.Data] = "a data declaration",
        [DirectiveKind.Dword] = "four-byte values",
        [DirectiveKind.Each] = "assemble the block once for each item",
        [DirectiveKind.Else] = "what to assemble instead",
        [DirectiveKind.ElseIf] = "another condition to try",
        [DirectiveKind.Ensure] = "the widths the code from here needs",
        [DirectiveKind.Enum] = "a set of named constants",
        [DirectiveKind.Error] = "fail the build with a message",
        [DirectiveKind.Export] = "make a name visible to other modules",
        [DirectiveKind.Fallthrough] = "the routine this one runs into",
        [DirectiveKind.FarAddr] = "three-byte addresses",
        [DirectiveKind.Frame] = "the stack frame the code from here works in",
        [DirectiveKind.Func] = "a function of its arguments",
        [DirectiveKind.HiBytes] = "the high byte of each value",
        [DirectiveKind.If] = "assemble the block when a condition holds",
        [DirectiveKind.Import] = "a name from outside the program",
        [DirectiveKind.IncBin] = "the bytes of a file",
        [DirectiveKind.List] = "a list of values to repeat over",
        [DirectiveKind.LoBytes] = "the low byte of each value",
        [DirectiveKind.Long] = "three-byte values",
        [DirectiveKind.Macro] = "a macro",
        [DirectiveKind.Module] = "the module this file is part of",
        [DirectiveKind.MultiProc] = "one routine per member of an enum",
        [DirectiveKind.Next] = "where control goes from here",
        [DirectiveKind.Patch] = "the target this instruction is patched to",
        [DirectiveKind.Place] = "emit another module's code here",
        [DirectiveKind.Proc] = "a routine",
        [DirectiveKind.Repeat] = "assemble the block a number of times",
        [DirectiveKind.Res] = "reserve bytes",
        [DirectiveKind.Scope] = "a scope of names",
        [DirectiveKind.Segment] = "declare a segment, or place what follows in one",
        [DirectiveKind.Signature] = "a named set of processor-state items",
        [DirectiveKind.State] = "what the processor state is here",
        [DirectiveKind.Strz] = "text with a terminating zero",
        [DirectiveKind.Struct] = "a structure",
        [DirectiveKind.Type] = "values of a declared type",
        [DirectiveKind.Union] = "a union",
        [DirectiveKind.Use] = "bring another module's names in",
        [DirectiveKind.Warning] = "report a message",
        [DirectiveKind.Word] = "two-byte values",
    };

    /// <summary>Gets every directive these lists may offer anywhere, each with a description.</summary>
    public static IEnumerable<DirectiveKind> All => details.Keys;

    /// <summary>
    /// Pairs every directive in <paramref name="kinds"/> with a description of what it is for,
    /// ready to be offered.
    /// </summary>
    public static IEnumerable<(string Name, string Detail)> Described(IEnumerable<DirectiveKind> kinds) =>
        kinds.Select(kind => (SyntaxFacts.TextOf(kind), details.GetValueOrDefault(kind, "directive")));

    /// <summary>
    /// Returns the directives that may begin the statement the caret is at the start of. In a block
    /// of unknown kind, anything may be meant, so every directive that may begin a line anywhere is
    /// offered.
    /// </summary>
    public static IEnumerable<(string Name, string Detail)> At(LineContext line)
    {
        if (line.Context == ContextKind.Unknown)
            return Described(SyntaxFacts.Directives.Where(kind => SyntaxFacts.PlacementOf(kind).Contexts != DirectiveContexts.None));
        var context = line.Context switch
        {
            ContextKind.Item => DirectiveContexts.Items,
            ContextKind.Code => DirectiveContexts.Code,
            ContextKind.Data => DirectiveContexts.Data,
            ContextKind.Values => DirectiveContexts.Values,
            ContextKind.TypeMembers => DirectiveContexts.TypeMembers,
            ContextKind.EnumMembers => DirectiveContexts.EnumMembers,
            _ => DirectiveContexts.None,
        };
        return Described(SyntaxFacts.Directives.Where(kind => SyntaxFacts.PlacementOf(kind).Allows(context, line.Nesting)));
    }
}
