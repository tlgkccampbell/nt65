using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor can do in and through a macro without expanding one. A body is
/// ordinary nt65 and its names are resolved where they are written, so going to a
/// definition, finding every use and renaming all work on the source as it stands.
/// </summary>
public sealed class MacroRequestsTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A macro with a parameter, a local label and a name from the file around it; a block
    /// macro; and a proc that calls both, one of them with a block.
    /// </summary>
    private const string Source = """
        .module main
        SCREEN = $0400

        .macro set16(dest: operand, value) {
            lda #<value
            sta dest
        @done:
            bvs @done
        }

        .macro times_x(count, body: block) {
            ldx #count
        @loop:
            body
            dex
            bne @loop
        }
        .segment CODE
        .proc main {
        @target:
            set16!(ptr, SCREEN)
            times_x!(8) {
                lda @target
            }
            rts
        }

        ptr = $10
        """;

    private static async Task<TestClient> OpenAsync(CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Source);
        await client.NextDiagnosticsAsync(timeout);
        return client;
    }

    /// <summary>A body is ordinary nt65, so it has nothing wrong with it to report.</summary>
    [Fact]
    public async Task AFileOfMacrosHasNothingWrongWithIt()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Source);

        Assert.Empty((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
    }

    /// <summary>A name in a body goes to where the body could see it, which is where it is declared.</summary>
    [Fact]
    public async Task DefinitionFromInsideABodyReachesTheFile()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `value` in `lda #<value` is the parameter the header declares.
        var parameter = await client.DefinitionAsync(Uri, new Position(4, 11), timeout);
        Assert.NotNull(parameter);
        Assert.Equal(3, parameter.Range.Start.Line);

        // A call goes to the macro it names, without expanding anything.
        var macro = await client.DefinitionAsync(Uri, new Position(20, 4), timeout);
        Assert.NotNull(macro);
        Assert.Equal(3, macro.Range.Start.Line);
    }

    /// <summary>Every place a parameter is named, which is every line of the body that uses it.</summary>
    [Fact]
    public async Task ReferencesToAParameterStayInItsBody()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var uses = await client.ReferencesAsync(Uri, new Position(3, 29), includeDeclaration: false, timeout);
        Assert.Equal([4], uses.Select(use => use.Range.Start.Line));
    }

    /// <summary>Renaming a parameter rewrites the header and every line of the body that names it.</summary>
    [Fact]
    public async Task RenamingAParameterRewritesTheBody()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var edit = await client.RenameAsync(Uri, new Position(3, 29), "amount", timeout);
        Assert.NotNull(edit);
        Assert.Equal([3, 4], edit.Changes[Uri].Select(change => change.Range.Start.Line).Order());
    }

    /// <summary>
    /// A block argument is the caller's code, so a name in it is the caller's name and is
    /// renamed with it — a body that splices the block is never expanded to find that out.
    /// </summary>
    [Fact]
    public async Task RenamingALabelUsedInABlockArgumentWorks()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // `@target:` is declared on line 19 and used on line 22, inside the block.
        var uses = await client.ReferencesAsync(Uri, new Position(19, 0), includeDeclaration: true, timeout);
        Assert.Equal([19, 22], uses.Select(use => use.Range.Start.Line).Order());

        var edit = await client.RenameAsync(Uri, new Position(22, 12), "@here", timeout);
        Assert.NotNull(edit);
        Assert.Equal([19, 22], edit.Changes[Uri].Select(change => change.Range.Start.Line).Order());
    }

    /// <summary>
    /// A body's own label is the macro's, not the caller's: two <c>@loop</c>s, one in a body
    /// and one in a proc, are two labels and neither rename touches the other.
    /// </summary>
    [Fact]
    public async Task ABodysLabelIsNotTheCallers()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var inTheBody = await client.ReferencesAsync(Uri, new Position(12, 0), includeDeclaration: true, timeout);
        Assert.Equal([12, 15], inTheBody.Select(use => use.Range.Start.Line).Order());
        Assert.DoesNotContain(inTheBody, use => use.Range.Start.Line == 19);
    }

    /// <summary>Hover on a macro says what it is and what it takes.</summary>
    [Fact]
    public async Task HoverOnACallDescribesTheMacro()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var hover = await client.HoverAsync(Uri, new Position(20, 4), timeout);
        Assert.NotNull(hover);
        Assert.Contains("**macro** `set16`", hover.Contents.Value);
    }
}
