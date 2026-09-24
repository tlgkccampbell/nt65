using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what a completion inserts and how the list is ordered. A directive that opens a block is
/// inserted as a snippet of the whole block, closing brace included; an instruction is inserted
/// as a plain word, because a snippet such as <c>lda ${1:operand}</c> gets in the way of someone
/// who knows what they are typing. The list is ordered by how close each name is to the caret,
/// since the name wanted is nearly always the closest.
/// </summary>
public sealed class CompletionStructureTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>
    /// A 65816 file whose routines all declare the same signature, so that the snippet for a new
    /// routine can offer that signature too.
    /// </summary>
    private const string Source = """
        .module main
        .cpu 65816
        .signature std = a8, i16
        .export SCREEN = $0400
        .segment CODE

        .export .proc reset: std {
            jsr step
            rts
        }

        .export .proc step: std {
        @again:
            bne @again
            rts
        }
        """;

    /// <summary>The directives that open a block, each of which a completion inserts as the whole block.</summary>
    private static readonly string[] Openers =
    [
        ".proc", ".multiproc", ".scope", ".macro", ".struct", ".union", ".enum", ".segment", ".if",
        ".repeat", ".each",
    ];

    /// <summary>
    /// Only the block openers are snippets. The name is the first tab stop and the body the last.
    /// On the 65816 a routine also has a stop for its signature, filled in with the signature
    /// that most of the program's other routines declare.
    /// </summary>
    [Fact]
    public async Task ABlockOpenerInsertsTheWholeBlock()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        var items = await CompletionAsync(client, new Position(5, 0), timeout);
        Assert.Equal(".proc ${1:name}: ${2:std} {\n    $0\n}", InsertedText(items, ".proc"));
        Assert.Equal(".scope ${1:name} {\n    $0\n}", InsertedText(items, ".scope"));
        Assert.Equal(".func ${1:name}(${2:parameters}) = $0", InsertedText(items, ".func"));
        Assert.All(Openers, name => Assert.Equal(InsertTextFormat.Snippet, One(items, name).InsertTextFormat));

        // A directive that opens no block is inserted as its plain name, as is everything else.
        Assert.Null(One(items, ".res").InsertTextFormat);
        Assert.Equal(".res", InsertedText(items, ".res"));

        var inside = await CompletionAsync(client, Locate.At(Source, "rts", 2), timeout);
        Assert.Null(One(inside, "lda").InsertTextFormat);
        Assert.Equal("lda ", InsertedText(inside, "lda"));
    }

    /// <summary>A client that did not declare snippet support gets the plain word, even for a block opener.</summary>
    [Fact]
    public async Task AClientWithoutStopsGetsThePlainWord()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.StartAsync(new { }, timeout);
        await client.OpenAsync(Uri, Source);
        await client.NextDiagnosticsAsync(Uri, timeout);

        var items = await CompletionAsync(client, new Position(5, 0), timeout);
        Assert.Equal(".proc", InsertedText(items, ".proc"));
        Assert.Null(One(items, ".proc").InsertTextFormat);
    }

    /// <summary>
    /// The list is ordered by nearness to the caret rather than alphabetically. The labels of the
    /// routine the caret is in come first, then the file's names, then the modules, then the
    /// language's own words. The instructions come last, since every CPU has far more of those
    /// than anyone could mean at one caret.
    /// </summary>
    [Fact]
    public async Task TheListIsOrderedByNearness()
    {
        var timeout = TestTimeout.Token();
        await using var client = await OpenAsync(timeout);

        // Where a name goes, the labels of the routine the caret is in come first, then the
        // file's names, then the prefixes, such as `$`, that start a number not in decimal.
        var named = await CompletionAsync(client, Locate.At(Source, "bne |@again"), timeout);
        var reached = (string label) => One(named, label).SortText!;
        Assert.True(string.CompareOrdinal(reached("@again"), reached("SCREEN")) < 0, "this routine's labels first");
        Assert.True(string.CompareOrdinal(reached("SCREEN"), reached("$")) < 0, "the file's names before the marks");

        // Where a statement goes, the language's directives come first and the instructions last.
        var starting = await CompletionAsync(client, Locate.At(Source, "rts", 2), timeout);
        var order = (string label) => One(starting, label).SortText!;
        Assert.True(string.CompareOrdinal(order(".byte"), order("lda")) < 0, "the words before the instructions");

        // Instructions are alphabetical among themselves, which is the only order they have.
        Assert.True(string.CompareOrdinal(order("lda"), order("sta")) < 0, "instructions alphabetical");
    }

    private static CompletionItem One(IReadOnlyList<CompletionItem> items, string label) =>
        Assert.Single(items, item => item.Label == label);

    private static string InsertedText(IReadOnlyList<CompletionItem> items, string label) =>
        One(items, label).TextEdit.NewText;

    private static Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        TestClient client, Position position, CancellationToken cancellation) =>
        client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new { textDocument = new { uri = Uri }, position }, cancellation);

    private static Task<TestClient> OpenAsync(CancellationToken cancellation) =>
        TestClient.OpenedAsync(cancellation, (Uri, Source));
}
