namespace Norristown.LanguageServer;

/// <summary>
/// Work that waits for the typing to stop. Each call replaces whatever was waiting, so a run of
/// keystrokes does the work once, after the last of them; a wait that has been replaced runs out
/// and then does nothing.
/// </summary>
/// <param name="quiet">How long without a call the work waits for.</param>
/// <param name="delay">How the waiting is done, which a test supplies itself.</param>
internal sealed class Debounce(TimeSpan quiet, Delay delay)
{
    private readonly Lock gate = new();

    // Which wait is the current one, and the task of the latest, for a caller that wants
    // whatever is outstanding to have happened before it looks.
    private long generation;
    private Task running = Task.CompletedTask;

    /// <summary>Runs <paramref name="work"/> once nothing else has asked for a while.</summary>
    public void After(Func<Task> work)
    {
        lock (gate)
        {
            running = WaitAsync(++generation, work);
        }
    }

    /// <summary>Whatever is waiting, done: for a test, and for a request that must see the whole program.</summary>
    public Task SettledAsync()
    {
        lock (gate)
        {
            return running;
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
