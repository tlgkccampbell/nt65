using Norristown.LanguageServer;

namespace Norristown.Tests.LanguageServer;

/// <summary>Tests the wait that holds back work until typing stops.</summary>
public sealed class DebounceTests
{
    /// <summary>
    /// Checks that work which has already started is cancelled when a later call replaces it.
    /// Without this, a slow publish of the whole program keeps running after the next edit
    /// has made it out of date.
    /// </summary>
    [Fact]
    public async Task ReplacingWorkThatHasStartedCancelsIt()
    {
        var timeout = TestTimeout.Token();
        var delay = new HeldDelay();
        var debounce = new Debounce(TimeSpan.Zero, delay.Wait);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);

        debounce.After(cancellation =>
        {
            started.TrySetResult(cancellation);
            return Task.Delay(Timeout.Infinite, cancellation);
        });
        await delay.ReleaseAsync(timeout);
        var running = await started.Task.WaitAsync(timeout);
        Assert.False(running.IsCancellationRequested);

        debounce.After(_ => Task.CompletedTask);

        Assert.True(running.IsCancellationRequested);
    }
}
