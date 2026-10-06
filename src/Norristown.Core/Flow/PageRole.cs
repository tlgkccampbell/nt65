namespace Norristown.Flow;

/// <summary>
/// Specifies what one routine does with one location on a <see cref="DirectPage"/>, from its own
/// instructions alone.
/// </summary>
public enum PageRole
{
    /// <summary>The routine reads the location before it writes it, and never writes it.</summary>
    In,

    /// <summary>The routine writes the location and never reads it.</summary>
    Out,

    /// <summary>The routine reads the location before it writes it, and also writes it.</summary>
    InOut,

    /// <summary>The routine writes the location before every read of it.</summary>
    Temp,

    /// <summary>The routine reads a hardware register and never writes it.</summary>
    Read,

    /// <summary>The routine writes a hardware register.</summary>
    Write,
}
