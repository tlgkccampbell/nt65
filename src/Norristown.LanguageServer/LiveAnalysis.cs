using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents the analysis of one program as its files change. Every request that asks while the
/// files stay the same shares one analysis, which runs on the thread pool rather than under the
/// workspace's lock. A change makes the next request start a new analysis, from the last one that
/// finished.
/// <para>
/// The analyses of one program run one after another, in the order their files were taken. An
/// analysis leaves the one it starts from unchanged, so two could run at once safely. They wait
/// so that each starts from the one just before it. Then only what the latest edit changed is
/// analyzed again, and two analyses of the whole program never compete for the processor. An
/// analysis that every request has stopped waiting for is cancelled.
/// </para>
/// <para>
/// An analysis after an edit may leave the program-wide answers for later, as
/// <see cref="ProgramAnalysis.IsSettled"/> describes. A request that needs them asks for the
/// settled analysis, which works them out once for the files as they stand. Settling runs beside
/// the next edit's analysis rather than before it, so that it never delays a keystroke, and a
/// change to the files cancels it. Once it has finished, every request gets the settled analysis.
/// </para>
/// </summary>
/// <param name="analyzer">The function that analyzes a program.</param>
/// <param name="settler">
/// The function that works out the program-wide answers, or null for
/// <see cref="Compiler.Settle"/>.
/// </param>
internal sealed class LiveAnalysis(Analyzer analyzer, Func<ProgramAnalysis, CancellationToken, ProgramAnalysis>? settler = null)
{
    private readonly Func<ProgramAnalysis, CancellationToken, ProgramAnalysis> settle = settler ?? Compiler.Settle;
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
                return current?.Settled is { IsCompletedSuccessfully: true } settled ? settled.Result
                    : current?.Task is { IsCompletedSuccessfully: true } done ? done.Result
                    : null;
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
    /// <param name="settled">
    /// Whether to wait for the program-wide answers. Without it, the settled analysis is returned
    /// where it has finished, and the analysis that may leave them for later otherwise.
    /// </param>
    public Task<ProgramAnalysis> AnalysisAsync(
        Func<(IReadOnlyCollection<SyntaxTree> Files, ProjectSettings Project)> inputs, CancellationToken cancellation,
        bool settled = false)
    {
        Run run;
        lock (gate)
        {
            if (current is null || !current.IsUsable)
                current = Start(inputs());
            run = current;
            if (run.Settled is { IsCompletedSuccessfully: true } done)
                return done;
            run.Waiting++;
            if (settled)
                run.Settled ??= Settle(run);
        }
        return WaitAsync(run, settled ? run.Settled! : run.Task, cancellation);
    }

    /// <summary>
    /// Discards the analysis of the files as they stand, because one of them has changed, and
    /// stops settling it. The last analysis that finished is kept for the next one to start from.
    /// </summary>
    public void Invalidate()
    {
        Run? replaced;
        lock (gate)
        {
            replaced = current;
            current = null;
        }
        replaced?.StopSettling();
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
    /// Starts working out the program-wide answers for the analysis <paramref name="run"/> makes,
    /// once it has finished. It does not wait for the analyses before it, so it runs beside the
    /// next edit's. The settled analysis becomes the one the next analysis starts from, unless a
    /// later one has finished meanwhile.
    /// </summary>
    private Task<ProgramAnalysis> Settle(Run run)
    {
        var cancellation = run.Settling.Token;
        return Task.Run(
            async () =>
            {
                var analysis = await run.Task.ConfigureAwait(false);
                var settled = settle(analysis, cancellation);
                lock (gate)
                {
                    if (ReferenceEquals(latest, analysis))
                        latest = settled;
                }
                return settled;
            },
            cancellation);
    }

    /// <summary>
    /// Returns the result of <paramref name="awaited"/>, which is <paramref name="run"/>'s
    /// analysis or its settling, once it finishes. The run is cancelled when this was the last
    /// request waiting for it and its analysis has not finished.
    /// </summary>
    private async Task<ProgramAnalysis> WaitAsync(Run run, Task<ProgramAnalysis> awaited, CancellationToken cancellation)
    {
        try
        {
            return await awaited.WaitAsync(cancellation).ConfigureAwait(false);
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

        /// <summary>Gets the source that stops settling the analysis once the files change.</summary>
        public CancellationTokenSource Settling { get; } = new();

        /// <summary>
        /// Gets or sets the analysis with the program-wide answers worked out, or null until a
        /// request asks for it.
        /// </summary>
        public Task<ProgramAnalysis>? Settled { get; set; }

        /// <summary>Gets or sets the number of requests waiting for the analysis.</summary>
        public int Waiting { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether every request stopped waiting before the
        /// analysis finished, so that it is being cancelled.
        /// </summary>
        public bool IsAbandoned { get; set; }

        /// <summary>
        /// Gets a value indicating whether another request can wait for this analysis. It cannot
        /// once the analysis has been cancelled or abandoned. An analysis that failed is still the
        /// answer for the files as they stand, since running it again would only fail again.
        /// </summary>
        public bool IsUsable => Task.IsCompletedSuccessfully || Task.IsFaulted || (!Task.IsCompleted && !IsAbandoned);

        /// <summary>Stops settling the analysis, unless that has finished.</summary>
        public void StopSettling()
        {
            if (Settled is not { IsCompleted: true })
                Settling.Cancel();
        }
    }
}
