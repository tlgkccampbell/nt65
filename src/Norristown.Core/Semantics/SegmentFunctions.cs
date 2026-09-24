using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Handles <c>.loadof(SEGMENT)</c>, <c>.runof(SEGMENT)</c> and <c>.spanof(SEGMENT)</c>, which give
/// where the linker loaded a segment, where it runs and how many bytes it holds. They become the
/// <c>__SEGMENT_LOAD__</c>, <c>__SEGMENT_RUN__</c> and <c>__SEGMENT_SIZE__</c> symbols that ld65
/// defines for a segment its configuration gives <c>define=yes</c>. A program therefore names them
/// through the segment table rather than importing three names that nothing checks.
/// </summary>
public static class SegmentFunctions
{
    /// <summary>
    /// Returns a value indicating whether <paramref name="call"/> is <c>.loadof</c> or
    /// <c>.runof</c>, which take a segment and nothing else.
    /// </summary>
    public static bool TakesOnlyASegment(CallExpressionSyntax call) =>
        call.BuiltinKind is BuiltinKind.Loadof or BuiltinKind.Runof;

    /// <summary>
    /// Returns the segment name given as <paramref name="call"/>'s only argument, or null when
    /// the arguments are not a single plain name.
    /// </summary>
    public static SyntaxToken? NameIn(CallExpressionSyntax call) =>
        call.Arguments.Arguments is { Count: 1 } arguments
        && arguments[0] is NameExpressionSyntax { Names.Length: 1, GlobalToken: null, SimpleName: { Kind: SyntaxKind.Identifier } name }
            ? name
            : null;

    /// <summary>
    /// Returns the segment a <c>.runof(SEGMENT)</c> names, which is the segment its address is in,
    /// or null for any other call.
    /// </summary>
    public static string? Runs(CallExpressionSyntax call) =>
        call.BuiltinKind == BuiltinKind.Runof ? NameIn(call)?.Text : null;

    /// <summary>
    /// Returns the function <paramref name="call"/> applies and the segment it asks about. Returns
    /// null when the call is not one of the three functions or names no declared segment.
    /// <c>.spanof</c> of a name that is also a symbol measures the symbol instead.
    /// </summary>
    public static (BuiltinKind Function, Segment Segment)? Of(CallExpressionSyntax call, SemanticModel model) =>
        Of(call, model.Segments, name => model.SymbolOf(name) is not null);

    /// <summary>
    /// Returns the function <paramref name="call"/> applies and the segment it asks about, finding
    /// the segment in <paramref name="segments"/> and asking <paramref name="isSymbol"/> whether a
    /// name is a symbol. Returns null when the call is not one of the three functions or names no
    /// declared segment. <c>.spanof</c> of a name that is also a symbol measures the symbol
    /// instead.
    /// </summary>
    public static (BuiltinKind Function, Segment Segment)? Of(
        CallExpressionSyntax call, SegmentTable segments, Func<NameExpressionSyntax, bool> isSymbol)
    {
        var function = call.BuiltinKind;
        if (function is not (BuiltinKind.Loadof or BuiltinKind.Runof or BuiltinKind.Spanof) || NameIn(call) is not { } name
            || segments.Find(name.Text) is not { } segment)
        {
            return null;
        }
        if (function == BuiltinKind.Spanof && isSymbol((NameExpressionSyntax)call.Arguments.Arguments[0]))
            return null;
        return (function, segment);
    }

    /// <summary>
    /// Returns the name ld65 defines for what <paramref name="function"/> asks of
    /// <paramref name="segment"/>.
    /// </summary>
    public static string LinkerName(BuiltinKind function, Segment segment) =>
        $"__{segment.Name}_{function switch { BuiltinKind.Loadof => "LOAD", BuiltinKind.Runof => "RUN", _ => "SIZE" }}__";

    /// <summary>
    /// Returns the address size of the three functions' results, which is absolute because ld65
    /// defines all three that way. An operand that reaches a load address in another bank must
    /// therefore use <c>f:</c>.
    /// </summary>
    public static AddressSize SizeOf() => AddressSize.Absolute;
}
