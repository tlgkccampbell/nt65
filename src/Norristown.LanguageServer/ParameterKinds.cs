using Norristown.Semantics;

namespace Norristown.LanguageServer;

/// <summary>
/// Describes in words what a macro parameter's kind accepts, and what each mode that an
/// <c>operand(...)</c> kind may list means. Hover shows these phrases for a parameter and for the
/// kind after its <c>:</c>, and completion shows them beside a kind or a mode it offers.
/// </summary>
internal static class ParameterKinds
{
    /// <summary>
    /// Gets the kinds a parameter may have, as completion inserts them, each with a description of
    /// what it accepts.
    /// </summary>
    public static IReadOnlyList<(string Keyword, string Takes)> Keywords { get; } =
    [
        ("expr", "an expression, constant or address"),
        ("const", "a constant, which a condition may test; const(lo..hi) takes one in a range"),
        ("ident", "a name"),
        ("operand", "an operand, braced unless it is a plain address; operand(imm, zp) takes the modes listed"),
        ("block", "the block after the call"),
        ("one(", "one of the words listed"),
        ("list(", "every remaining argument, each of the kind given"),
    ];

    /// <summary>Returns a phrase that describes what an argument for <paramref name="kind"/> may be.</summary>
    public static string Takes(ArgumentKind kind) => kind.Kind switch
    {
        ParameterKind.Const when kind is { Low: { } low, High: { } high } =>
            $"a constant from {low.GetText().Trim()} to {high.GetText().Trim()}",
        ParameterKind.Const => "a constant",
        ParameterKind.Ident => "a name",
        ParameterKind.Operand when kind.Words.Count > 0 =>
            $"an operand in {Listed(kind.Words.Select(mode => $"{mode}"), "or")} mode",
        ParameterKind.Operand => "an operand in any mode",
        ParameterKind.One => $"one of the words {Listed(kind.Words.Select(word => $"{word}"), "or")}",
        ParameterKind.List => $"every remaining argument, each {Takes(kind.Element ?? ArgumentKind.Expression)}",
        ParameterKind.Block => "the block after the call",
        ParameterKind.Enum => $"a member of {kind.Enum?.GetText().Trim()}, by its bare name or its path",
        _ => "an expression, constant or address",
    };

    /// <summary>
    /// Returns a phrase that describes how an operand in <paramref name="mode"/> is written, or
    /// null for a word that names no mode.
    /// </summary>
    public static string? Mode(string mode) => mode.ToLowerInvariant() switch
    {
        "imm" => "an immediate, {#value}",
        "acc" => "the accumulator, {a}",
        "abs" => "an address, addr, reached through the direct page or not",
        "absx" => "an address plus x, {addr,x}",
        "absy" => "an address plus y, {addr,y}",
        "zp" => "a direct-page address, addr",
        "zpx" => "a direct-page address plus x, {addr,x}",
        "zpy" => "a direct-page address plus y, {addr,y}",
        "ind" => "a pointer at an address, {(addr)}",
        "indx" => "a pointer at an address plus x, {(addr,x)}",
        "indy" => "a pointer at an address, plus y, {(addr),y}",
        "sr" => "a stack offset, {offset,s}",
        "sry" => "a pointer at a stack offset, plus y, {(offset,s),y}",
        "long" => "a long pointer on the direct page, {[addr]}",
        "longy" => "a long pointer on the direct page, plus y, {[addr],y}",
        _ => null,
    };

    /// <summary>
    /// Joins the items into a phrase such as <c>a</c>, <c>a or b</c>, or <c>a, b or c</c>, with
    /// <paramref name="last"/> before the final item.
    /// </summary>
    private static string Listed(IEnumerable<string> items, string last)
    {
        var all = items.ToList();
        return all.Count <= 1 ? string.Concat(all) : $"{string.Join(", ", all.Take(all.Count - 1))} {last} {all[^1]}";
    }
}
