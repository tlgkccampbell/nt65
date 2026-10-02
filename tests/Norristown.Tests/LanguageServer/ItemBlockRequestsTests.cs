namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests that the names a list directive gives in an item block are followed, found and renamed
/// as the same names given on the directive's own line are.
/// </summary>
public sealed class ItemBlockRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A file whose exports, imports and <c>.next</c> labels are each listed in an item block.
    /// </summary>
    private const string Source = """
        .module main
        .cpu 6502

        .export {
            main, SCREEN
            dispatch
        }

        .import {
            vsync: proc()
        }

        .const SCREEN = $0400

        .segment ZEROPAGE
        .data vector: .word

        .segment CODE
        .proc main {
            jsr vsync
            jmp dispatch
        }

        .proc dispatch {
            jmp (vector)
            .next {
                @move
                @fire
            }
        @move:
            rts
        @fire:
            rts
        }
        """;

    /// <summary>An exported name and a <c>.next</c> label in a block lead to their declarations.</summary>
    [Fact]
    public async Task DefinitionFollowsANameInABlock()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Source));

        var exported = await client.DefinitionAsync(Uri, Locate.At(Source, "    |main, SCREEN"), timeout);
        Assert.Equal(Locate.Span(Source, ".proc |main"), exported?.Range);

        var label = await client.DefinitionAsync(Uri, Locate.At(Source, "|@move"), timeout);
        Assert.Equal(Locate.Span(Source, "@move", occurrence: 2), label?.Range);
    }

    /// <summary>
    /// The references to a routine include its export in a block, and those to an import include
    /// the import itself, declared in a block.
    /// </summary>
    [Fact]
    public async Task ReferencesIncludeTheNamesInABlock()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Source));

        var routine = await client.ReferencesAsync(Uri, Locate.At(Source, ".proc |dispatch"), includeDeclaration: true, timeout);
        Assert.Equal([5, 20, 23], routine.Select(location => location.Range.Start.Line).Order());

        var import = await client.ReferencesAsync(Uri, Locate.At(Source, "jsr |vsync"), includeDeclaration: true, timeout);
        Assert.Equal([9, 19], import.Select(location => location.Range.Start.Line).Order());
    }

    /// <summary>Renaming a label renames it in the <c>.next</c> block that names it.</summary>
    [Fact]
    public async Task ARenameReachesIntoABlock()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (Uri, Source));

        var edit = await client.RenameAsync(Uri, Locate.At(Source, "|@fire:"), "@shoot", timeout);
        Assert.NotNull(edit);
        Assert.Equal([27, 31], edit.Changes[Uri].Select(e => e.Range.Start.Line).Order());
        Assert.All(edit.Changes[Uri], e => Assert.Equal("@shoot", e.NewText));
    }
}
