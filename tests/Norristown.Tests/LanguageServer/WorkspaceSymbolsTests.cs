using Norristown.LanguageServer.Protocol;
using static Norristown.Tests.LanguageServer.EditingWorkspace;

namespace Norristown.Tests.LanguageServer;

/// <summary>Tests the search for a declaration across the workspace.</summary>
public sealed class WorkspaceSymbolsTests
{
    /// <summary>
    /// A search finds declarations in files no one has open, matching names that contain the
    /// query's letters in order.
    /// </summary>
    [Fact]
    public async Task WorkspaceSymbolsFindDeclarationsByTheirLetters()
    {
        var timeout = TestTimeout.Token();
        var (text, _) = WithLine("body", "");
        await using var client = await OpenAsync(text, timeout);

        var found = await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("clr"), timeout);

        var clear = Assert.Single(found);
        Assert.Equal("clear", clear.Name);
        Assert.Equal("gfx", clear.ContainerName);
        Assert.Equal(GfxUri, clear.Location.Uri);
        Assert.Equal(Locate.At(Gfx, ".proc |clear"), clear.Location.Range.Start);
        Assert.Equal([("Sprite", "gfx")], (await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("sprite"), timeout)).Select(s => (s.Name, s.ContainerName)));
    }
}
