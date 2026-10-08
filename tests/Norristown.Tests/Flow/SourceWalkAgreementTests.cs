using System.Collections.Concurrent;
using Norristown.Flow;
using Norristown.Processor;
using Norristown.Tests.Oracle;

namespace Norristown.Tests.Flow;

/// <summary>
/// Holds the <see cref="SourceWalk"/> to the <see cref="RegisterWalk"/>. The source walk is a second
/// walk over the same blocks, and a second walk can drift from the first. At every statement of
/// every corpus program, the two must agree about whether each register holds an entry value, a
/// written value or an unknown one.
/// </summary>
public sealed class SourceWalkAgreementTests
{
    [Fact]
    public void TheSourceWalkAgreesWithTheRegisterWalkOnTheCorpus()
    {
        var problems = new ConcurrentQueue<string>();
        var compared = 0;
        Parallel.ForEach(Programs(), program =>
        {
            foreach (var file in program.Analyze().Files)
                Interlocked.Add(ref compared, Check(program.Name, file, problems));
        });
        Assert.True(compared > 1000, $"only {compared} statements were compared");
        Assert.True(problems.IsEmpty, string.Join("\n", problems.Order(StringComparer.Ordinal).Take(40)));
    }

    /// <summary>
    /// Returns every corpus program and example the loader can read, with each platform of the
    /// monitor example, which holds a project per platform rather than one at its root.
    /// </summary>
    private static IEnumerable<CorpusProgram> Programs() =>
        CorpusProgram.All().Concat(Directory.GetDirectories(Repo.Path("examples", "monitor"))
            .Where(directory => File.Exists(Path.Combine(directory, "nt65.json")))
            .Order(StringComparer.Ordinal)
            .Select(CorpusProgram.Load));

    /// <summary>
    /// Adds a problem for each statement of a file where the two walks disagree, and returns how many
    /// statements were compared.
    /// </summary>
    private static int Check(string program, FileAnalysis file, ConcurrentQueue<string> problems)
    {
        var flow = file.Flow;
        if (flow.KeepsOf is not { } of || flow.Registers is not { } held)
            return 0;
        var compared = 0;
        var walk = new SourceWalk(file.Model, file.Layout, flow, file.State, of);
        foreach (var region in flow.Regions)
        {
            if (!region.IsEntered)
                continue;
            var reached = walk.Solve(region);
            foreach (var block in region.Blocks)
            {
                if (reached[block.Index] is not { } state)
                    continue;
                for (var i = 0; i < block.Steps.Count; i++)
                {
                    var step = block.Steps[i];
                    if (!step.Closes && held.Before(step.Statement, step.On) is { } expected)
                    {
                        compared++;
                        var where = $"{program}: {file.Path}:{step.Statement.Tree.GetLineIndex(step.Statement.Position) + 1} "
                            + $"`{step.Statement.GetText().Trim()}`";
                        Compare(where, "A", expected.A, state.Of(Tracked.A).Value, problems);
                        Compare(where, "A high", expected.AHigh, state.Of(Tracked.AHigh).Value, problems);
                        Compare(where, "X", expected.X, state.Of(Tracked.X).Value, problems);
                        Compare(where, "Y", expected.Y, state.Of(Tracked.Y).Value, problems);
                        Compare(where, "C", expected.C, state.Of(Tracked.C).Value, problems);
                    }
                    state = walk.After(block, i, state);
                }
            }
        }
        return compared;
    }

    private static void Compare(string where, string name, RegisterValue expected, RegisterValue found, ConcurrentQueue<string> problems)
    {
        // Whether an entry value is held only through a keep nobody promised is the register
        // walk's alone, and the source walk does not follow it.
        expected = expected with { Unbacked = Registers.None };
        if (expected != found)
            problems.Enqueue($"{where}: {name} is {expected} in the register walk but {found} in the source walk");
    }
}
