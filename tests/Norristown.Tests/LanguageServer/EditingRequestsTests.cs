using Norristown.LanguageServer.Protocol;

// The protocol has a Range of its own, which is the one these tests mean.
using Range = Norristown.LanguageServer.Protocol.Range;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What an editor gets while code is being written: completion, help with a call, the hints in
/// the lines, and a search for a declaration across the workspace.
/// </summary>
public sealed class EditingRequestsTests
{
    private const string GfxUri = "file:///c:/work/gfx.nt65";

    private const string VicUri = "file:///c:/work/hw/vic.nt65";

    private const string MainUri = "file:///c:/work/main.nt65";

    private const string Gfx = """
        .module gfx
        .export clear, SCREEN
        .export .struct Sprite {
            x: .byte
            y: .byte
        }
        SCREEN = $0400
        .segment CODE
        .proc clear {
            rts
        }
        .proc helper {
            rts
        }
        """;

    private const string Vic = ".module hw::vic\n.export BORDER = $d020\n";

    /// <summary>The file completion is asked in; <c>|</c> is written where each test puts its own line.</summary>
    private const string Main = """
        .module main
        .use gfx::{clear}
        .use hw::vic
        .signature fast = a8, i8
        .func twice(n) = n * 2
        .segment CODE
        .macro poke(address: expr, value: const = 0) {
            |
        }
        .proc main {
        @loop:
            |
            rts
        }
        """;

    public static TheoryData<string, string, string[], string[]> Completions => new()
    {
        // After `::`, what the path leads to exports; nothing it keeps private.
        { "body", "    jsr gfx::|", ["Sprite", "SCREEN", "clear"], ["helper", "main"] },
        { "body", "    lda gfx::Sprite::|", ["x", "y"], ["clear"] },
        { "body", "    lda vic::|", ["BORDER"], ["clear"] },

        // A `.use` starts at the modules' root, and in its braces names what the module exports.
        { "top", ".use |", ["gfx", "hw", "main"], ["clear"] },
        { "top", ".use hw::|", ["vic"], ["gfx"] },
        { "top", ".use gfx::{|", ["SCREEN", "Sprite", "clear"], ["hw", "helper"] },

        // An operand may name anything in scope, what `.use` brought in, and a module to walk into.
        { "body", "    lda |", ["@loop", "clear", "gfx", "main", "twice", "vic"], ["poke", "fast", "lda"] },
        { "macro", "    sta |", ["address", "value"], ["@loop"] },

        // A statement starts with an instruction or a macro call.
        { "body", "    |", ["lda", "poke"], ["clear"] },

        // A signature's items, and the signature sets; a `.state` asserts a point, and a macro is not called.
        { "top", ".proc other: |", ["a16", "a?", "a*", "dp", "far", "fast", "near"], ["clear", "lda"] },
        { "body", "    .state |", ["a16", "dbr", "e?", "native"], ["near", "a*", "fast"] },
        { "top", ".macro m(): |", ["a8", "e*"], ["near", "far"] },
        { "top", ".proc other: a8, dp = |", ["twice", "vic"], ["a16"] },

        // A macro call's argument may name its parameter.
        { "body", "    poke!(|", ["address", "value", "clear"], ["lda"] },
        { "body", "    poke!(1, |", ["value"], [] },

        // A name being declared is not completed.
        { "top", ".proc |", [], ["clear"] },
    };

    [Fact]
    public async Task AnnouncesWhatTheEditingLayerCanDo()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(timeout);

        var capabilities = client.Initialized.Capabilities;
        Assert.Equal([":", "@", "!", "(", ","], capabilities.CompletionProvider?.TriggerCharacters);
        Assert.Equal(["(", ",", "="], capabilities.SignatureHelpProvider?.TriggerCharacters);
        Assert.True(capabilities.InlayHintProvider);
        Assert.True(capabilities.WorkspaceSymbolProvider);
        Assert.True(capabilities.CodeActionProvider);
    }

    [Theory]
    [MemberData(nameof(Completions))]
    public async Task CompletionOffersWhatMayBeWrittenThere(string where, string line, string[] offered, string[] notOffered)
    {
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Place(where, line);
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var labels = items.Select(item => item.Label).ToHashSet();
        Assert.All(offered, label => Assert.Contains(label, labels));
        Assert.All(notOffered, label => Assert.DoesNotContain(label, labels));
    }

    /// <summary>Completion replaces the part of a name already typed, and writes a named argument with its <c>=</c>.</summary>
    [Fact]
    public async Task ACompletionReplacesWhatIsTypedAndWritesWhatTheItemNeeds()
    {
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Place("body", "    poke!(val|");
        await using var client = await OpenAsync(text, timeout);

        var items = await client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        var value = Assert.Single(items, item => item.Label == "value");
        Assert.Equal(CompletionItemKind.Property, value.Kind);
        Assert.Equal("value = ", value.TextEdit.NewText);
        Assert.Equal(new Position(position.Line, position.Character - 3), value.TextEdit.Range.Start);
        Assert.Equal(position, value.TextEdit.Range.End);
    }

    public static TheoryData<string, string, string, int> Calls => new()
    {
        { "body", "    poke!(|", "poke!(address: expr, value: const = 0)", 0 },
        { "body", "    poke!(SCREEN, |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(value = |", "poke!(address: expr, value: const = 0)", 1 },
        { "body", "    poke!(twice(|", "twice(n)", 0 },
        { "body", "    poke!(twice(1), |", "poke!(address: expr, value: const = 0)", 1 },
        { "top", "X = .select(1, 2, |", ".select(condition, chosen, otherwise)", 2 },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task SignatureHelpSaysWhatTheCallTakes(string where, string line, string signature, int active)
    {
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Place(where, line);
        await using var client = await OpenAsync(text, timeout);

        var help = await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout);

        Assert.NotNull(help);
        var only = Assert.Single(help.Signatures);
        Assert.Equal(signature, only.Label);
        Assert.Equal(active, help.ActiveParameter);
    }

    [Fact]
    public async Task SignatureHelpIsNothingOutsideACall()
    {
        var timeout = TestContext.Current.CancellationToken;
        var (text, position) = Place("body", "    lda (vic::BORDER),y|");
        await using var client = await OpenAsync(text, timeout);

        Assert.Null(await client.RequestAsync<SignatureHelp?>("textDocument/signatureHelp",
            new TextDocumentPositionParams(new TextDocumentIdentifier(MainUri), position), timeout));
    }

    /// <summary>
    /// Each instruction's cycles at the end of its line, each block's after them, and on the 65816
    /// the state reaching a label beside its name.
    /// </summary>
    [Fact]
    public async Task InlayHintsShowCyclesAndTheStateAtALabel()
    {
        var timeout = TestContext.Current.CancellationToken;
        const string Source = """
            .module main
            .cpu 65816
            .segment CODE
            .proc main: a8, i16 {
                lda #0      ; clear
            @loop:
                dex
                bne @loop
                rts
            }
            """;
        await using var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(MainUri, Source.ReplaceLineEndings("\n"));
        await client.NextDiagnosticsAsync(timeout);

        var hints = await client.RequestAsync<IReadOnlyList<InlayHint>>("textDocument/inlayHint",
            new InlayHintParams(new TextDocumentIdentifier(MainUri), new Range(new Position(0, 0), new Position(10, 0))), timeout);

        Assert.Equal(
            [
                (4, 10, "2 cycles"), (4, 10, "block: 2 cycles"),
                (5, 5, "a8, i16, native"),
                (6, 7, "2 cycles"), (6, 7, "block: 4-5 cycles"),
                (7, 13, "2-3 cycles"),
                (8, 7, "6 cycles"), (8, 7, "block: 6 cycles"),
            ],
            hints.Select(hint => (hint.Position.Line, hint.Position.Character, hint.Label)));
    }

    /// <summary>A search finds declarations in files no one has open, by the letters of their names in order.</summary>
    [Fact]
    public async Task WorkspaceSymbolsFindDeclarationsByTheirLetters()
    {
        var timeout = TestContext.Current.CancellationToken;
        var (text, _) = Place("body", "");
        await using var client = await OpenAsync(text, timeout);

        var found = await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("clr"), timeout);

        var clear = Assert.Single(found);
        Assert.Equal("clear", clear.Name);
        Assert.Equal("gfx", clear.ContainerName);
        Assert.Equal(GfxUri, clear.Location.Uri);
        Assert.Equal(new Position(8, 6), clear.Location.Range.Start);
        Assert.Equal([("Sprite", "gfx")], (await client.RequestAsync<IReadOnlyList<SymbolInformation>>("workspace/symbol",
            new WorkspaceSymbolParams("sprite"), timeout)).Select(s => (s.Name, s.ContainerName)));
    }

    /// <summary>
    /// The main file with <paramref name="line"/> in the place <paramref name="where"/> names —
    /// the macro's body, the routine's, or the top level — and where its <c>|</c> is.
    /// </summary>
    private static (string Text, Position Position) Place(string where, string line)
    {
        var text = Main.ReplaceLineEndings("\n");
        var lines = text.Split('\n').ToList();
        var body = lines.FindLastIndex(l => l.Trim() == "|");
        var macro = lines.FindIndex(l => l.Trim() == "|");
        var at = where switch { "macro" => macro, "body" => body, _ => lines.Count };
        lines[macro] = "";
        lines[body] = "";
        if (at == lines.Count)
            lines.Add(line);
        else
            lines[at] = line;
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Replace("|", "", StringComparison.Ordinal);
        return (string.Join('\n', lines), new Position(at, Math.Max(0, column)));
    }

    private static async Task<TestClient> OpenAsync(string main, CancellationToken timeout)
    {
        var client = await TestClient.StartAsync(timeout);
        await client.OpenAsync(GfxUri, Gfx.ReplaceLineEndings("\n"));
        await client.OpenAsync(VicUri, Vic);
        await client.OpenAsync(MainUri, main);
        for (var i = 0; i < 1 + 2 + 3; i++)
            await client.NextDiagnosticsAsync(timeout);
        return client;
    }
}
