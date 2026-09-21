namespace Norristown.Cli;

/// <summary>
/// The program the command is in the middle of building, for the handler of last resort to
/// name. nt65 builds on the thread it was asked on, so this is that thread's and two builds at
/// once cannot read each other's.
/// </summary>
internal static class Building
{
    [ThreadStatic]
    private static string? building;

    /// <summary>The program being built, as the person running nt65 would name it, or null.</summary>
    public static string? Program => building;

    /// <summary>Says what is being built, until <see cref="Nothing"/> says it is done.</summary>
    /// <param name="program">The program, as the person running nt65 would name it.</param>
    public static void Started(string program) => building = program;

    /// <summary>Says that nothing is being built.</summary>
    public static void Nothing() => building = null;
}
