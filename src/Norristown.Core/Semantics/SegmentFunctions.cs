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
    /// Returns the address size of what <paramref name="function"/> gives for
    /// <paramref name="segment"/>, which is the size of its placement. A run address is as wide as
    /// a label in the segment. A load address is zero page where every linked configuration loads
    /// the segment into page zero. Otherwise it is far for a far segment, which may load where it
    /// runs, and absolute for any other, as a label in the load area would be. A size is
    /// absolute. An operand that reaches an address in another bank must therefore use
    /// <c>f:</c>, as it must for a label there.
    /// </summary>
    /// <param name="function">The function, which is <c>.loadof</c>, <c>.runof</c> or <c>.spanof</c>.</param>
    /// <param name="segment">The segment the function asks about.</param>
    public static AddressSize SizeOf(BuiltinKind function, Segment segment) => function switch
    {
        BuiltinKind.Runof => segment.Size,
        BuiltinKind.Loadof when Known(segment.Loads, segment) && segment.Loads.All(area => area.First >= 0 && area.Last <= 0xff) =>
            AddressSize.ZeroPage,
        BuiltinKind.Loadof when segment.Size == AddressSize.Far => AddressSize.Far,
        _ => AddressSize.Absolute,
    };

    /// <summary>
    /// Returns the range of values what <paramref name="function"/> gives for
    /// <paramref name="segment"/> may take once linked, or null where nt65 cannot bound it. An
    /// address is inside the memory areas the linked configurations put the segment in, or where
    /// they say nothing, anywhere its address size reaches. A size is at most the room of the
    /// smallest area it is placed in, because every link holds the same bytes of the segment.
    /// </summary>
    /// <param name="function">The function, which is <c>.loadof</c>, <c>.runof</c> or <c>.spanof</c>.</param>
    /// <param name="segment">The segment the function asks about.</param>
    public static (long Low, long High)? RangeOf(BuiltinKind function, Segment segment)
    {
        if (function == BuiltinKind.Spanof)
        {
            IReadOnlyList<RunArea> areas = [.. segment.Runs, .. segment.Loads];
            return areas.Count > 0 ? (0, areas.Min(area => area.Last - area.First + 1)) : null;
        }
        if (PlacedRange(function == BuiltinKind.Runof ? segment.Runs : segment.Loads, segment) is { } placed)
            return placed;
        return SizeOf(function, segment) switch
        {
            AddressSize.ZeroPage => (0, 0xff),
            AddressSize.Absolute => (0, 0xffff),
            AddressSize.Far => (0, 0xffffff),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the addresses from the start of the lowest of <paramref name="areas"/> to the end
    /// of the highest, or null when they do not hold an area for every linked configuration that
    /// places <paramref name="segment"/>. The areas are where the segment runs or where it loads.
    /// </summary>
    public static (long Low, long High)? PlacedRange(IReadOnlyList<RunArea> areas, Segment segment) =>
        Known(areas, segment) ? (areas.Min(area => area.First), areas.Max(area => area.Last)) : null;

    /// <summary>
    /// Returns a value indicating whether <paramref name="areas"/> holds an area for every linked
    /// configuration that places <paramref name="segment"/>, so that together they bound where it
    /// is.
    /// </summary>
    private static bool Known(IReadOnlyList<RunArea> areas, Segment segment) =>
        areas.Count > 0 && areas.Count == segment.Placements.Count;
}
