using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Works out how many bytes a push or a pull moves, for the stacks that record pushes rather than
/// bytes.
/// </summary>
internal static class PushBytes
{
    /// <summary>
    /// Returns how many bytes a push or a pull of <paramref name="size"/> moves, where the register
    /// it moves is <paramref name="width"/> wide, or null where that width is not a known one.
    /// </summary>
    public static int? Of(PushSize size, Width width) => size switch
    {
        PushSize.OneByte => 1,
        PushSize.TwoBytes => 2,
        _ => width switch
        {
            Width.Eight => 1,
            Width.Sixteen => 2,
            _ => null,
        },
    };
}
