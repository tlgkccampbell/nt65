namespace Norristown;

/// <summary>Specifies how much a diagnostic matters.</summary>
public enum Severity
{
    /// <summary>The program is wrong, and no output is produced for it.</summary>
    Error,

    /// <summary>The program is suspect but still compiles.</summary>
    Warning,

    /// <summary>Something worth showing, such as a cycle count.</summary>
    Info,
}
