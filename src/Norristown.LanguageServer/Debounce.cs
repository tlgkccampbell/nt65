namespace Norristown.LanguageServer;

/// <summary>
/// Work that waits for the typing to stop. Each call replaces whatever was waiting, so a run of
/// keystrokes does the work once, after the last of them; a wait that has been replaced runs out
/// and then does nothing.
/// </summary>
/// <param name="quiet">How long there must be no further call before the work runs.</param>
/// <param name="delay">How the waiting is done, which a test supplies itself.</param>
internal sealed class Debounce(TimeSpan quiet, Delay delay)
{
    private readonly Lock gate = new();

    // Which wait is the current one. A wait that something else has replaced runs out and then
    // finds that it is no longer the one whose work is wanted.
    private long generation;

    /// <summary>Runs <paramref name="work"/> once nothing else has asked for a while.</summary>
    public void After(Func<Task> work)
    {
        lock (gate)
        {
            _ = WaitAsync(++generation, work);
        }
    }

    private async Task WaitAsync(long mine, Func<Task> work)
    {
        await delay(quiet).ConfigureAwait(false);
        lock (gate)
        {
            // Something was typed while this was waiting, and that call's wait is the one
            // whose work will run.
            if (generation != mine)
                return;
        }
        await work().ConfigureAwait(false);
    }
}
