using System.Runtime.ExceptionServices;

namespace Norristown;

/// <summary>
/// Runs independent pieces of an analysis at once, such as the files of a program, each of which
/// is analyzed on its own.
/// </summary>
internal static class ParallelWork
{
    /// <summary>
    /// Runs <paramref name="body"/> for every index from 0 up to <paramref name="count"/>, several
    /// at once. A failure is thrown as it was raised rather than wrapped, so that a caller
    /// sees the same exception as from a loop, and a cancellation is thrown as one.
    /// </summary>
    /// <param name="count">The number of pieces.</param>
    /// <param name="body">The work for one piece, given its index.</param>
    /// <param name="cancellation">The token checked before each piece starts.</param>
    public static void For(int count, Action<int> body, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (count < 2)
        {
            for (var i = 0; i < count; i++)
                body(i);
            return;
        }
        try
        {
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, body);
        }
        catch (AggregateException e)
        {
            var first = e.Flatten().InnerExceptions[0];
            ExceptionDispatchInfo.Capture(
                first is OperationCanceledException && cancellation.IsCancellationRequested
                    ? new OperationCanceledException(cancellation)
                    : first).Throw();
            throw;
        }
    }
}
