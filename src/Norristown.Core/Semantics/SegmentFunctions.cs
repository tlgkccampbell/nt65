using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// <c>.loadof(SEGMENT)</c>, <c>.runof(SEGMENT)</c> and <c>.spanof(SEGMENT)</c>: where the linker
/// loaded a segment, where it runs and how many bytes it holds. They stand for the
/// <c>__SEGMENT_LOAD__</c>, <c>__SEGMENT_RUN__</c> and <c>__SEGMENT_SIZE__</c> that ld65 defines
/// for a segment its configuration gives <c>define=yes</c>, so a program names them through the
/// segment table rather than importing three names nothing checks.
/// </summary>
public static class SegmentFunctions
{
    /// <summary>Whether a call is <c>.loadof</c> or <c>.runof</c>, which take a segment and nothing else.</summary>
    public static bool TakesOnlyASegment(CallExpressionSyntax call) =>
        FunctionOf(call) is ".loadof" or ".runof";

    /// <summary>The segment name a call's one argument is written as, or null when it is not one plain name.</summary>
    public static SyntaxToken? NameIn(CallExpressionSyntax call) =>
        call.Arguments.Arguments is { Count: 1 } arguments
        && arguments[0] is NameExpressionSyntax { Names.Length: 1, GlobalToken: null, SimpleName: { Kind: SyntaxKind.Identifier } name }
            ? name
            : null;

    /// <summary>The segment a <c>.runof(SEGMENT)</c> names, which is where the address it stands for is; null for any other call.</summary>
    public static string? Runs(CallExpressionSyntax call) =>
        FunctionOf(call) == ".runof" ? NameIn(call)?.Text : null;

    /// <summary>
    /// What a call asks about a segment: the function and the segment, or null when it is not
    /// one of the three or names no declared segment. <c>.spanof</c> of a name that is a symbol
    /// asks about the symbol, as it always has.
    /// </summary>
    public static (string Function, Segment Segment)? Of(CallExpressionSyntax call, SemanticModel model) =>
        Of(call, model.Segments, name => model.SymbolOf(name) is not null);

    /// <summary>The same, given the segment table and whether a name is a symbol.</summary>
    public static (string Function, Segment Segment)? Of(
        CallExpressionSyntax call, SegmentTable segments, Func<NameExpressionSyntax, bool> isSymbol)
    {
        if (FunctionOf(call) is not (".loadof" or ".runof" or ".spanof") || NameIn(call) is not { } name
            || segments.Find(name.Text) is not { } segment)
        {
            return null;
        }
        var function = FunctionOf(call)!;
        if (function == ".spanof" && isSymbol((NameExpressionSyntax)call.Arguments.Arguments[0]))
            return null;
        return (function, segment);
    }

    /// <summary>The name ld65 defines for what <paramref name="function"/> asks of <paramref name="segment"/>.</summary>
    public static string LinkerName(string function, Segment segment) =>
        $"__{segment.Name}_{function switch { ".loadof" => "LOAD", ".runof" => "RUN", _ => "SIZE" }}__";

    /// <summary>
    /// How wide an address it is: absolute, which is how ld65 defines all three, so an
    /// operand that reaches a load address in another bank says <c>f:</c>.
    /// </summary>
    public static AddressSize SizeOf() => AddressSize.Absolute;

    private static string? FunctionOf(CallExpressionSyntax call) =>
        call.Function is { Kind: SyntaxKind.Directive } function ? function.Text.ToLowerInvariant() : null;
}
