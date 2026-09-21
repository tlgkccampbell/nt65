namespace Norristown.LanguageServer;

/// <summary>Where a server is in its life, which is what says whether a request may be answered.</summary>
internal enum ServerPhase
{
    /// <summary>Before <c>initialize</c>: nothing but <c>initialize</c> and <c>exit</c> is answered.</summary>
    Starting,

    /// <summary>Initialized: every request is answered.</summary>
    Running,

    /// <summary>After <c>shutdown</c>: nothing but <c>exit</c> is answered.</summary>
    ShuttingDown,
}
