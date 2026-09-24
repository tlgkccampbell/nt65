using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents the analysis of one program as its files change. Every request that asks while the
/// files stay the same shares one analysis, which runs on the thread pool rather than under the
/// workspace's lock. A change makes the next request start a new analysis, from the last one that
/// finished.
/// <para>
/// The analyses of one program run one after another, in the order their files were taken. The
/// next analysis starts from the one before it, and composing it updates the routines of the files
/// it keeps from that one, so two at once would each change what the other is reading. An
/// analysis that every request has stopped waiting for is cancelled.
/// </para>
/// </summary>
/// <param name="analyzer">The function that analyzes a program.</param>
internal sealed class LiveAnalysis(Analyzer analyzer)
{
    private readonly Lock gate = new();

    // The analysis of the files as they stand, or null when they have changed since it started.
    private Run? current;

    // The last analysis started, which the next one waits for.
    private Task last = Task.CompletedTask;

    // The last analysis that finished, which the next one starts from.
    private ProgramAnalysis? latest;

    /// <summary>
    /// Gets the last analysis that finished, which may be of files that have changed since, or
    /// null when none has.
    /// </summary>
    public ProgramAnalysis? Latest
    {
        get
        {
            lock (gate)
            {
                return latest;
            }
        }
    }

    /// <summary>
    /// Gets the analysis of the files as they stand when it has finished, or null when it has not
    /// or no request has asked for it.
    /// </summary>
    public ProgramAnalysis? Finished
    {
        get
        {
            lock (gate)
            {
                return current?.Task is { IsCompletedSuccessfully: true } done ? done.Result : null;
            }
        }
    }

    /// <summary>
    /// Returns the analysis of the program, starting it from <paramref name="inputs"/> when no
    /// analysis of the files as they stand is running or done. The inputs are taken at once, so a
    /// caller that holds the workspace's lock is asking about the files as the lock sees them.
    /// </summary>
    /// <param name="inputs">Returns the program's files and settings as they stand.</param>
    /// <param name="cancellation">
    /// Stops this request waiting. The analysis itself stops only when no other request is waiting
    /// for it.
    /// </param>
    public Task<ProgramAnalysis> AnalysisAsync(
        Func<(IReadOnlyCollection<SyntaxTree> Files, ProjectSettings Project)> inputs, CancellationToken cancellation)
    {
        Run run;
        lock (gate)
        {
            if (current is null || !current.IsUsable)
                current = Start(inputs());
            run = current;
            run.Waiting++;
        }
        return WaitAsync(run, cancellation);
    }

    /// <summary>
    /// Discards the analysis of the files as they stand, because one of them has changed. The last
    /// analysis that finished is kept for the next one to start from.
    /// </summary>
    public void Invalidate()
    {
        lock (gate)
        {
            current = null;
        }
    }

    /// <summary>
    /// Starts an analysis of <paramref name="inputs"/> once the analysis before it has ended.
    /// </summary>
    private Run Start((IReadOnlyCollection<SyntaxTree> Files, ProjectSettings Project) inputs)
    {
        var source = new CancellationTokenSource();
        var before = last;
        var cancellation = source.Token;
        var task = Task.Run(
            async () =>
            {
                await before.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                cancellation.ThrowIfCancellationRequested();
                ProgramAnalysis? from;
                lock (gate)
                {
                    from = latest;
                }
                var analysis = analyzer(inputs.Files, inputs.Project, from, cancellation);
                lock (gate)
                {
                    latest = analysis;
                }
                return analysis;
            },
            cancellation);
        last = task;
        return new Run(source, task);
    }

    /// <summary>
    /// Returns the result of <paramref name="run"/> once it finishes, and cancels the run when this
    /// was the last request waiting for it and it has not finished.
    /// </summary>
    private async Task<ProgramAnalysis> WaitAsync(Run run, CancellationToken cancellation)
    {
        try
        {
            return await run.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            bool abandoned;
            lock (gate)
            {
                abandoned = --run.Waiting == 0 && !run.Task.IsCompleted;
                run.IsAbandoned |= abandoned;
            }

            // The source is cancelled outside the lock, because cancelling runs whatever the
            // analysis registered on its token. The run was marked under the lock, so no request
            // joins it in between.
            if (abandoned)
                await run.Source.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Represents one analysis and the requests waiting for it.</summary>
    /// <param name="source">Cancels the analysis.</param>
    /// <param name="task">The analysis.</param>
    private sealed class Run(CancellationTokenSource source, Task<ProgramAnalysis> task)
    {
        public CancellationTokenSource Source { get; } = source;

        public Task<ProgramAnalysis> Task { get; } = task;

        /// <summary>Gets or sets the number of requests waiting for the analysis.</summary>
        public int Waiting { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether every request stopped waiting before the
        /// analysis finished, so that it is being cancelled.
        /// </summary>
        public bool IsAbandoned { get; set; }

        /// <summary>
        /// Gets a value indicating whether another request can wait for this analysis. It cannot
        /// once the analysis has failed or been abandoned.
        /// </summary>
        public bool IsUsable => Task.IsCompletedSuccessfully || (!Task.IsCompleted && !IsAbandoned);
    }
}
