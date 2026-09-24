namespace Norristown.Syntax;

/// <summary>
/// Specifies what surrounds a line, as far as it rules a directive in or out there regardless of
/// the line's context. A line's nesting combines every block that holds it, not only the
/// innermost one.
/// </summary>
[Flags]
public enum DirectiveNesting
{
    /// <summary>Nothing that rules a directive in or out.</summary>
    None = 0,

    /// <summary>The line is the first line of its file.</summary>
    FirstLine = 1 << 0,

    /// <summary>A <c>.proc</c> body holds the line.</summary>
    Routine = 1 << 1,

    /// <summary>A macro body holds the line.</summary>
    MacroBody = 1 << 2,

    /// <summary>A <c>.repeat</c> or <c>.each</c> body holds the line.</summary>
    Repetition = 1 << 3,

    /// <summary>A block of any kind holds the line, including an open <c>.segment</c> region.</summary>
    Block = 1 << 4,

    /// <summary>A block other than an open <c>.segment</c> region holds the line.</summary>
    PastFileLevel = 1 << 5,
}
