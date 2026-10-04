using Norristown.Layout;

namespace Norristown.Flow;

/// <summary>
/// Identifies one place where a value the <see cref="SourceWalk"/> follows was set. Two paths that
/// bring the same origin to a point bring one source, not two.
/// </summary>
/// <param name="Kind">
/// What set the value. A walk never makes a <see cref="SourceKind.Macro"/> origin. An instruction
/// from a macro expansion is an <see cref="SourceKind.Instruction"/> origin until it is mapped to
/// the line of the call.
/// </param>
/// <param name="At">
/// The step that set the value, or the blocker for an <see cref="SourceKind.Unknown"/> origin. It
/// is null for an <see cref="SourceKind.Entry"/> origin, which the routine's opening line stands
/// for.
/// </param>
internal readonly record struct Origin(SourceKind Kind, StepKey? At)
{
    /// <summary>Gets the origin of a value the routine's caller set.</summary>
    public static Origin Entry => new(SourceKind.Entry, null);
}
