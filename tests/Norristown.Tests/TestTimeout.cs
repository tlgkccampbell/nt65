namespace Norristown.Tests;

/// <summary>
/// Gives a test a cancellation token with a time limit, so that a wait which never ends fails
/// the test instead of hanging the whole run with no output.
/// </summary>
internal static class TestTimeout
{
    /// <summary>
    /// The longest a test may wait. Each test finishes in well under a second, so this leaves
    /// ample room for a loaded machine.
    /// </summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns a token that is cancelled when the test run is cancelled or when the time limit
    /// has passed, whichever comes first.
    /// </summary>
    public static CancellationToken Token()
    {
        // The source is not disposed. It lives until its timer fires, which costs one timer
        // per test.
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(Limit);
        return source.Token;
    }
}
