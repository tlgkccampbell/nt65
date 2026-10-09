using Norristown.Layout;
using Norristown.Processor;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents the byte a <c>.res</c> or <c>.align</c> in a routine is filled with, and where that
/// byte comes from. Padding with a constant fill byte of its own holds that byte. Padding with none
/// holds the segment's <see cref="SegmentFill"/>, which only a linked configuration can give.
/// </summary>
/// <param name="Value">The byte, or null when nt65 cannot work it out.</param>
/// <param name="Described">
/// How the padding is filled, as a phrase for a message, such as <c>with $ff by the memory area
/// `ROM` in `rom.cfg`</c>.
/// </param>
internal sealed record PaddingFill(long? Value, string Described)
{
    /// <summary>
    /// Returns the fill of the padding at <paramref name="step"/>, or null when the step is not a
    /// <c>.res</c> or <c>.align</c>.
    /// </summary>
    public static PaddingFill? Of(Step step, SemanticModel model)
    {
        if (step.Statement is not DataDirectiveSyntax directive || !DataSyntax.IsPadding(directive))
            return null;
        if (DataSyntax.FillOf(directive) is { } given)
        {
            return model.ValueOf(given, step.On).AsNumber() is { } value
                ? new PaddingFill(value & 0xFF, $"with {StateValue.Hex(value & 0xFF, 2)}, its fill byte")
                : new PaddingFill(null, "with a fill byte nt65 cannot work out");
        }
        var segment = step.Segment is { } name ? model.Segments.Find(name) : null;
        return segment?.Fill switch
        {
            { Value: { } value } fill => new PaddingFill(value, $"with {StateValue.Hex(value, 2)} by {fill.Source}"),
            { } fill => new PaddingFill(null, $"with a byte from {fill.Source} that nt65 cannot work out"),
            null when step.Segment is { } unplaced => new PaddingFill(
                null, $"with a byte nt65 cannot work out, since no linked config places segment `{unplaced}`"),
            null => new PaddingFill(null, "with a byte nt65 cannot work out"),
        };
    }

    /// <summary>
    /// Returns a value indicating whether execution runs through the padding on
    /// <paramref name="cpu"/>, which it does when every byte is a one-byte instruction that does
    /// nothing, such as <c>nop</c>.
    /// </summary>
    public bool RunsOn(Cpu cpu) => Value is { } value && Opcodes.DoesNothing(cpu, (byte)value);

    /// <summary>
    /// Returns why execution does not run through the padding, as a clause for a message, such as
    /// <c>it is filled with $00 by ld65's default, which does not run on</c>.
    /// </summary>
    public string WhyNot() => Value is null ? $"it is filled {Described}" : $"it is filled {Described}, which does not run on";
}
