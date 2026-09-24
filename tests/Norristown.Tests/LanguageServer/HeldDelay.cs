using System.Threading.Channels;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Represents the server's wait for typing to stop, controlled by the test instead of by a
/// clock. Each wait the server asks for is queued, and the test releases one when it is ready
/// for the step that follows it. A test of the wait therefore takes no real time and never
/// depends on timing.
/// </summary>
internal sealed class HeldDelay
{
    private readonly Channel<TaskCompletionSource> waits = Channel.CreateUnbounded<TaskCompletionSource>();

    /// <summary>Gets the number of waits that are queued and not yet released.</summary>
    public int Waiting => waits.Reader.Count;

    /// <summary>
    /// Returns the delay the server is given in place of a real timer. The
    /// <paramref name="quiet"/> period is ignored.
    /// </summary>
    public Task Wait(TimeSpan quiet)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waits.Writer.TryWrite(held);
        return held.Task;
    }

    /// <summary>
    /// Releases the next queued wait. If the server has not yet asked for a wait, this first
    /// waits for it to ask.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellation) =>
        (await waits.Reader.ReadAsync(cancellation)).TrySetResult();
}
