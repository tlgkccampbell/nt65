using System.Threading.Channels;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The server's wait for typing to stop, controlled by the test rather than by a clock. Each
/// wait the server asks for is queued, and the test releases one when it is ready for what
/// follows it, so a test of the wait takes no real time and never depends on timing.
/// </summary>
internal sealed class HeldDelay
{
    private readonly Channel<TaskCompletionSource> waits = Channel.CreateUnbounded<TaskCompletionSource>();

    /// <summary>How many waits are queued and not yet let go.</summary>
    public int Waiting => waits.Reader.Count;

    /// <summary>The delay the server is given in place of a real timer; <paramref name="quiet"/> is ignored.</summary>
    public Task Wait(TimeSpan quiet)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waits.Writer.TryWrite(held);
        return held.Task;
    }

    /// <summary>Releases the next queued wait, first waiting for the server to ask for one if it has not yet.</summary>
    public async Task ReleaseAsync(CancellationToken cancellation) =>
        (await waits.Reader.ReadAsync(cancellation)).TrySetResult();
}
