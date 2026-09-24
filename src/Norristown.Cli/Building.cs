namespace Norristown.Cli;

/// <summary>
/// Records the program the command is currently building, so that the top-level exception
/// handler can name it in its report. nt65 builds on the calling thread, so the value is kept
/// per thread, and two builds running at once do not see each other's values.
/// </summary>
internal static class Building
{
    [ThreadStatic]
    private static string? building;

    /// <summary>
    /// Gets the program being built, as the person running nt65 would name it, or null when
    /// nothing is being built.
    /// </summary>
    public static string? Program => building;

    /// <summary>Records what is being built, until <see cref="Nothing"/> clears it.</summary>
    /// <param name="program">The program, as the person running nt65 would name it.</param>
    public static void Started(string program) => building = program;

    /// <summary>Clears the record, because nothing is being built.</summary>
    public static void Nothing() => building = null;
}
