namespace Norristown.LanguageServer;

/// <summary>
/// Represents a wait for a span of time. The server takes a delay function rather than calling
/// <see cref="Task.Delay(TimeSpan)"/>, so that a test can drive the waiting itself and the
/// suite does not spend real seconds waiting for a debounce to run out.
/// </summary>
/// <param name="quiet">How long to wait.</param>
internal delegate Task Delay(TimeSpan quiet);
