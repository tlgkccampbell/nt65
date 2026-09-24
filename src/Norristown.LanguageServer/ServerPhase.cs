namespace Norristown.LanguageServer;

/// <summary>
/// Specifies where a server is in its lifecycle, which decides whether a request may be answered.
/// </summary>
internal enum ServerPhase
{
    /// <summary>Before <c>initialize</c>. Only <c>initialize</c> and <c>exit</c> are answered.</summary>
    Starting,

    /// <summary>After initialization. Every request is answered.</summary>
    Running,

    /// <summary>After <c>shutdown</c>. Only <c>exit</c> is answered.</summary>
    ShuttingDown,
}
