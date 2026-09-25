namespace Norristown.SyntaxGenerator;

/// <summary>
/// Represents a node table that cannot be read, and the line of Syntax.xml that holds the
/// problem, so that the generator's diagnostic can point at it.
/// </summary>
public sealed class TableException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TableException"/> class with a message and
    /// the line it concerns.
    /// </summary>
    /// <param name="message">The message that describes the problem.</param>
    /// <param name="line">The one-based line of the element at fault, or 0 if no line is known.</param>
    public TableException(string message, int line)
        : base(message)
    {
        Line = line;
    }

    /// <summary>Gets the one-based line of the element at fault, or 0 if no line is known.</summary>
    public int Line { get; }
}
