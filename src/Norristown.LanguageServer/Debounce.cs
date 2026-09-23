namespace Norristown.LanguageServer;

/// <summary>
/// Work that waits for the typing to stop. Each call replaces whatever was waiting, so a run of
/// keystrokes does the work once, after the last of them; a wait that has been replaced runs out
/// and then does nothing. Work that had already started when it was replaced is cancelled,
/// because what it was doing is now out of date.
/// </summary>
/// <param name="quiet">How long there must be no further call before the work runs.</param>
/// <param name="delay">How the waiting is done, which a test supplies itself.</param>
internal sealed class Debounce(TimeSpan quiet, Delay delay) : IDisposable
{
    private readonly Lock gate = new();

    // Which wait is the current one. A wait that something else has replaced runs out and then
    // finds that it is no longer the one whose work is wanted.
    private long generation;

    // Cancels the current wait's work once a later call replaces it.
    private CancellationTokenSource current = new();

    /// <summary>
    /// Runs <paramref name="work"/> once nothing else has asked for a while, and cancels the
    /// token it was given if another call replaces it while it runs.
    /// </summary>
    public void After(Func<CancellationToken, Task> work)
    {
        lock (gate)
        {
            // The replaced source is not disposed: its work may still be registering on the
            // token, and a source with no timer holds nothing that needs releasing.
            current.Cancel();
            current = new CancellationTokenSource();
            _ = WaitAsync(++generation, work, current.Token);
        }
    }

    /// <summary>Cancels the current work, if any, and releases the source that cancels it.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            current.Cancel();
            current.Dispose();
        }
    }

    private async Task WaitAsync(long mine, Func<CancellationToken, Task> work, CancellationToken cancellation)
    {
        await delay(quiet).ConfigureAwait(false);
        lock (gate)
        {
            // Something was typed while this was waiting, and that call's wait is the one
            // whose work will run.
            if (generation != mine)
                return;
        }
        await work(cancellation).ConfigureAwait(false);
    }
}
