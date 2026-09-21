using System.Threading.Channels;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The server's wait for the typing to stop, held by the test rather than by a clock. Each wait
/// the server asks for is queued; the test lets one go when it is ready for what comes after it,
/// so a test about the wait costs no real time and never depends on one.
/// </summary>
internal sealed class HeldDelay
{
    private readonly Channel<TaskCompletionSource> waits = Channel.CreateUnbounded<TaskCompletionSource>();

    /// <summary>How many waits are queued and not yet let go.</summary>
    public int Waiting => waits.Reader.Count;

    /// <summary>The wait itself, which is what the server is given in place of a clock.</summary>
    public Task Wait(TimeSpan quiet)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waits.Writer.TryWrite(held);
        return held.Task;
    }

    /// <summary>Lets the next wait go, waiting for the server to ask for one where it has not yet.</summary>
    public async Task ReleaseAsync(CancellationToken cancellation) =>
        (await waits.Reader.ReadAsync(cancellation)).TrySetResult();
}
