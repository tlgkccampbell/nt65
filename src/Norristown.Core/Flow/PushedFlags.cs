namespace Norristown.Flow;

/// <summary>
/// Represents what the zero, negative and overflow flags held when a <c>php</c> pushed them. The
/// push's own <see cref="SavedPush.Value"/> is what the carry held, and the matching <c>plp</c>
/// gives each flag back what it held here.
/// </summary>
/// <param name="Z">What the zero flag held.</param>
/// <param name="N">What the negative flag held.</param>
/// <param name="V">What the overflow flag held.</param>
public readonly record struct PushedFlags(RegisterValue Z, RegisterValue N, RegisterValue V)
{
    /// <summary>Gets flags nothing is known about, as a push of some other byte holds.</summary>
    public static PushedFlags Unknown => new(RegisterValue.Unknown, RegisterValue.Unknown, RegisterValue.Unknown);

    /// <summary>
    /// Returns what the flags may hold where two paths meet, or null where either path pushed
    /// something other than the flags.
    /// </summary>
    public static PushedFlags? Merge(PushedFlags? a, PushedFlags? b) =>
        a is { } x && b is { } y
            ? new(RegisterValue.Merge(x.Z, y.Z), RegisterValue.Merge(x.N, y.N), RegisterValue.Merge(x.V, y.V))
            : null;
}
