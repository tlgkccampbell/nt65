namespace Norristown.Cli;

/// <summary>
/// Represents the exit code nt65 returns to the program that ran it. The numeric values are what
/// scripts and build tools test, so they do not change.
/// </summary>
public enum ExitCode
{
    /// <summary>nt65 did what it was asked.</summary>
    Success = 0,

    /// <summary>The input is wrong, which is the program being built or a file nt65 was given.</summary>
    InputError = 1,

    /// <summary>The command line is wrong.</summary>
    UsageError = 2,

    /// <summary>
    /// nt65 itself failed. This is EX_SOFTWARE, the conventional exit code for an internal
    /// software error.
    /// </summary>
    InternalError = 70,
}
