namespace Norristown.Tests.Semantics;

/// <summary>
/// Random edits replayed both ways, incrementally and from scratch, and compared after each.
/// They are a class of their own so that they run beside the scripted edits rather than after
/// them.
/// </summary>
public sealed class RandomEditAnalysisTests(ITestOutputHelper output)
{
    /// <summary>
    /// Edits at random places, most of which leave a file that does not parse. Whatever the
    /// edit breaks, the analysis that starts from the one before it has to break the same way.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomEditsMatchAnalyzingFromScratch(int seed)
    {
        string[] snippets = ["x", "1", " ", "\n", "#", "lda #2\n", ";", "}", "{", ":", "::", "@", "!", ".export ", "WIDTH", "dex\n"];
        var random = new Random(seed);
        var replay = new ProgramReplay();
        var paths = ProgramReplay.Sources.Keys.Order(StringComparer.Ordinal).ToArray();
        var why = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var step = 0; step < 60; step++)
        {
            var path = paths[random.Next(paths.Length)];
            var length = replay.Text(path).Length;
            var at = random.Next(length + 1);
            var analysis = random.Next(3) == 0
                ? replay.Change(path, at, Math.Min(random.Next(1, 6), length - at), "")
                : replay.Insert(path, at, snippets[random.Next(snippets.Length)]);
            var reason = analysis.WholeProgram?.ToString() ?? $"{analysis.Reanalyzed} of {ProgramReplay.Sources.Count} files";
            why[reason] = why.GetValueOrDefault(reason) + 1;
        }
        foreach (var (reason, count) in why.OrderByDescending(pair => pair.Value))
            output.WriteLine($"{count,3} {reason}");
        Assert.True(why.ContainsKey($"1 of {ProgramReplay.Sources.Count} files"), "no random edit was analyzed on its own");
    }
}
