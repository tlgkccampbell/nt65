using System.Collections.Concurrent;
using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Represents a program analysis that the test holds back until it releases it, so that a test
/// can see what the server does while an analysis is running. Every analysis waits for the same
/// release, and runs the real analysis once released.
/// </summary>
internal sealed class HeldAnalysis
{
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<ProgramAnalysis?> previous = new();

    /// <summary>Gets a source that completes when the first analysis starts.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a source that completes when any analysis is cancelled while it is held.</summary>
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the number of analyses started.</summary>
    public int Calls => previous.Count;

    /// <summary>Gets the analysis each analysis started from, in the order they started.</summary>
    public IReadOnlyList<ProgramAnalysis?> Previous => [.. previous];

    /// <summary>Lets every held analysis, and every later one, run.</summary>
    public void Release() => released.TrySetResult();

    /// <summary>
    /// Analyzes a program once the test releases it. A cancellation while it is held is recorded
    /// and ends the analysis.
    /// </summary>
    public ProgramAnalysis Analyze(
        IReadOnlyCollection<SyntaxTree> files, ProjectSettings project, ProgramAnalysis? previous,
        CancellationToken cancellation)
    {
        this.previous.Enqueue(previous);
        Started.TrySetResult();
        using (cancellation.Register(() => Cancelled.TrySetResult()))
            released.Task.Wait(cancellation);
        return Compiler.Analyze(files, project, previous, cancellation);
    }
}
