namespace Norristown.LanguageServer;

/// <summary>
/// Waiting for a stretch of time. The server takes one rather than calling
/// <see cref="Task.Delay(TimeSpan)"/>, so that a test can drive the waiting itself and the
/// suite does not spend real seconds watching a debounce run out.
/// </summary>
/// <param name="quiet">How long to wait.</param>
internal delegate Task Delay(TimeSpan quiet);
