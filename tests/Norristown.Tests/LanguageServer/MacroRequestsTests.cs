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
        .export .proc main {
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
        Assert.Contains("```nt65\n.macro set16(dest: operand, value)\n```", hover.Contents.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// A macro whose parameters say what they take, and calls that pass an enum's member by its
    /// bare name, one of them beside data of the same name.
    /// </summary>
    private const string Typed = """
        .module main
        .enum Instrument {
            kick
            snare
        }
        .macro play(what: Instrument, note: const(0..127), src: operand(imm, abs), reg: one(x, y)) {
            .if .mode(src) == imm && reg != y {
                .byte what, note
            } .elseif .mode(src) == absx {
                .byte note
            }
        }
        .segment RODATA
        .data kick: .byte 1
        .data tune {
            play!(kick, 5, {#1}, x)
            play!(Instrument::snare, 6, kick, y)
        }
        """;

    /// <summary>
    /// Hover on a parameter, or anywhere in the kind after its `:`, says what the parameter takes,
    /// and on a mode an `operand` lists, what an operand in that mode is written as.
    /// </summary>
    [Fact]
    public async Task HoverSaysWhatAParameterTakes()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Typed);
        await client.NextDiagnosticsAsync(timeout);
        var header = Typed.ReplaceLineEndings("\n").Split('\n')[5];

        async Task<string> HoverAt(string word) =>
            (await client.HoverAsync(Uri, new Position(5, header.IndexOf(word, StringComparison.Ordinal) + 1), timeout))
                ?.Contents.Value ?? "";

        var what = await HoverAt("what");
        Assert.Contains("macro parameter what: Instrument", what, StringComparison.Ordinal);
        Assert.Contains("a member of Instrument, by its bare name or its path", what, StringComparison.Ordinal);

        var range = await HoverAt("const");
        Assert.Contains("macro parameter note: const(0..127)", range, StringComparison.Ordinal);
        Assert.Contains("a constant from 0 to 127", range, StringComparison.Ordinal);
        Assert.Contains("a constant from 0 to 127", await HoverAt("127"), StringComparison.Ordinal);

        var mode = await HoverAt("abs");
        Assert.Contains("an operand in imm or abs mode", mode, StringComparison.Ordinal);
        Assert.Contains("abs: an address", mode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hover on a word a condition compares with a parameter says what the word is, what it is
    /// compared with and what that takes, and when the parameter can never be it, says so.
    /// </summary>
    [Fact]
    public async Task HoverOnAComparedWordSaysWhatItIs()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Typed);
        await client.NextDiagnosticsAsync(timeout);

        var imm = (await client.HoverAsync(Uri, new Position(6, 23), timeout))?.Contents.Value ?? "";
        Assert.Contains("mode imm", imm, StringComparison.Ordinal);
        Assert.Contains("imm: an immediate", imm, StringComparison.Ordinal);
        Assert.Contains(".mode(src)", imm, StringComparison.Ordinal);
        Assert.DoesNotContain("never", imm, StringComparison.Ordinal);

        var y = (await client.HoverAsync(Uri, new Position(6, 37), timeout))?.Contents.Value ?? "";
        Assert.Contains("word y", y, StringComparison.Ordinal);
        Assert.Contains("one of the words x or y", y, StringComparison.Ordinal);

        var absx = (await client.HoverAsync(Uri, new Position(8, 29), timeout))?.Contents.Value ?? "";
        Assert.Contains("src is never absx: it may be imm, abs", absx, StringComparison.Ordinal);
    }

    /// <summary>
    /// A member passed by its bare name is a use of the member, even where the caller has a name
    /// of its own spelled the same: it is coloured, hovered and found as the member.
    /// </summary>
    [Fact]
    public async Task AMemberPassedByItsBareNameIsTheMember()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(Uri, Typed);

        // The one thing wrong is the comparison `src` can never make true, which is a warning.
        var said = Assert.Single((await client.NextDiagnosticsAsync(timeout)).Diagnostics);
        Assert.Equal(("comparison-never-holds", DiagnosticSeverity.Warning, 8), (said.Code, said.Severity, said.Range.Start.Line));

        var definition = await client.DefinitionAsync(Uri, new Position(15, 11), timeout);
        Assert.Equal(2, definition?.Range.Start.Line);
        var hover = await client.HoverAsync(Uri, new Position(15, 11), timeout);
        Assert.Contains("Instrument::kick", hover?.Contents.Value, StringComparison.Ordinal);

        // The operand argument names the data, which is what it takes.
        var data = await client.DefinitionAsync(Uri, new Position(16, 33), timeout);
        Assert.Equal(13, data?.Range.Start.Line);

        var legend = client.Initialized.Capabilities.SemanticTokensProvider!.Legend;
        var tokens = await client.SemanticTokensAsync(Uri, timeout);
        var (line, character) = (0, 0);
        var at = new Dictionary<(int, int), string>();
        for (var i = 0; i < tokens.Data.Count; i += 5)
        {
            line += tokens.Data[i];
            character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
            at[(line, character)] = legend.TokenTypes[tokens.Data[i + 3]];
        }
        Assert.Equal("enumMember", at[(15, 10)]);

        // A word a condition compares with a parameter is one of the set the parameter names.
        Assert.Equal("enumMember", at[(6, 22)]);
        Assert.Equal("enumMember", at[(6, 36)]);
        Assert.Equal("variable", at[(16, 32)]);
    }
}
