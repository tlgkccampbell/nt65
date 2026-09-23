using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// The modules that come with nt65, in the editor: no file holds them, so a location in one
/// has an <c>nt65:</c> URI whose text the server supplies, and what they declare is theirs to
/// name, not the program's to rename.
/// </summary>
public sealed class StandardModuleRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    // `screen` is at column 19 of the last line, and `text`, an alias of the program's own, at 33.
    private const string Source =
        ".module main\n.use nt65::cbm::screen\n.use nt65::cbm::petscii as text\n.segment RODATA\n"
        + ".data title: .byte screen(\"HI\"), text(\"HI\")\n";

    /// <summary>A definition in a module that comes with nt65 is at its <c>nt65:</c> URI, and the server gives that URI's text.</summary>
    [Fact]
    public async Task ADefinitionLeadsToTheModulesOwnText()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        var definition = await client.DefinitionAsync(Uri, Locate.At(Source, ".byte |screen("), timeout);
        Assert.NotNull(definition);
        Assert.Equal("nt65:/cbm.nt65", definition.Uri);

        var text = await client.RequestAsync<string?>(
            "nt65/standardModule", new { textDocument = new { uri = definition.Uri } }, timeout);
        Assert.NotNull(text);
        Assert.Contains(".module nt65::cbm", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name a module that comes with nt65 declares is not offered for renaming, and a rename
    /// asked for anyway is refused; a name the program brings it in under is its own to rename.
    /// </summary>
    [Fact]
    public async Task ItsNamesAreNotTheProgramsToRename()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedAsync(timeout, (Uri, Source));

        Assert.Null(await client.PrepareRenameAsync(Uri, Locate.At(Source, ".byte |screen("), timeout));
        var refused = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => client.RenameAsync(Uri, Locate.At(Source, ".byte |screen("), "shown", timeout));
        Assert.Contains("comes with nt65", refused.Message, StringComparison.Ordinal);

        var edit = await client.RenameAsync(Uri, Locate.At(Source, ", |text("), "petscii_text", timeout);
        Assert.NotNull(edit?.Changes);
        Assert.Equal([Uri], edit.Changes.Keys);
        Assert.Equal(2, edit.Changes[Uri].Count);
    }
}
