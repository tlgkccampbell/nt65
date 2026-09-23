using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// What a completion inserts, and the order of the list. A directive that opens a block is
/// inserted as a snippet of the whole block, closing brace included; an instruction is inserted
/// as a plain word, because a snippet such as <c>lda ${1:operand}</c> gets in the way of someone
/// who knows what they are typing. The list is ordered by how close each name is to the caret,
/// since the name wanted is nearly always the closest.
/// </summary>
public sealed class CompletionStructureTests
{
    private const string Uri = "file:///c:/work/main.nt65";

    /// <summary>The directives that open a block, each of which a completion inserts as the whole block.</summary>
    private static readonly string[] Openers =
    [
        ".proc", ".multiproc", ".scope", ".macro", ".struct", ".union", ".enum", ".segment", ".if",
        ".repeat", ".each",
    ];

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

    /// <summary>
    /// The block openers are snippets and nothing else is: the name is the first tab stop and
    /// the body the last, and on the 65816 a routine also has a stop for its signature, filled
    /// in with the one most of the program's other routines declare.
    /// </summary>
    [Fact]
    public async Task ABlockOpenerIsWrittenAsTheBlock()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        var items = await CompletionAsync(client, new Position(5, 0), timeout);
        Assert.Equal(".proc ${1:name}: ${2:std} {\n    $0\n}", Written(items, ".proc"));
        Assert.Equal(".scope ${1:name} {\n    $0\n}", Written(items, ".scope"));
        Assert.Equal(".func ${1:name}(${2:parameters}) = $0", Written(items, ".func"));
        Assert.All(Openers, name => Assert.Equal(InsertTextFormat.Snippet, One(items, name).InsertTextFormat));

        // A directive that opens no block is inserted as its plain name, as is everything else.
        Assert.Null(One(items, ".res").InsertTextFormat);
        Assert.Equal(".res", Written(items, ".res"));

        var inside = await CompletionAsync(client, new Position(14, 4), timeout);
        Assert.Null(One(inside, "lda").InsertTextFormat);
        Assert.Equal("lda ", Written(inside, "lda"));
    }

    /// <summary>A client that did not declare snippet support gets the plain word, even for a block opener.</summary>
    [Fact]
    public async Task AClientWithoutStopsGetsThePlainWord()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await TestClient.StartAsync(new { }, timeout);
        await client.OpenAsync(Uri, Source);
        await client.NextDiagnosticsAsync(Uri, timeout);

        var items = await CompletionAsync(client, new Position(5, 0), timeout);
        Assert.Equal(".proc", Written(items, ".proc"));
        Assert.Null(One(items, ".proc").InsertTextFormat);
    }

    /// <summary>
    /// The list is ordered by nearness rather than by spelling: the labels of the routine the
    /// caret is in, then the file's names, then the modules, then the language's own words, and
    /// the instructions last, since every CPU has far more of those than anyone could mean at
    /// one caret.
    /// </summary>
    [Fact]
    public async Task TheListIsOrderedByNearness()
    {
        var timeout = TestContext.Current.CancellationToken;
        await using var client = await OpenAsync(timeout);

        // Where a name goes: the labels of the routine the caret is in, then the file's names,
        // then the prefixes, such as `$`, that start a number that is not written in decimal.
        var named = await CompletionAsync(client, new Position(13, 8), timeout);
        var reached = (string label) => One(named, label).SortText!;
        Assert.True(string.CompareOrdinal(reached("@again"), reached("SCREEN")) < 0, "this routine's labels first");
        Assert.True(string.CompareOrdinal(reached("SCREEN"), reached("$")) < 0, "the file's names before the marks");

        // Where a statement goes: the language's directives first, and the instructions last.
        var starting = await CompletionAsync(client, new Position(14, 4), timeout);
        var order = (string label) => One(starting, label).SortText!;
        Assert.True(string.CompareOrdinal(order(".byte"), order("lda")) < 0, "the words before the instructions");

        // Instructions are alphabetical among themselves, which is the only order they have.
        Assert.True(string.CompareOrdinal(order("lda"), order("sta")) < 0, "instructions alphabetical");
    }

    private static CompletionItem One(IReadOnlyList<CompletionItem> items, string label) =>
        Assert.Single(items, item => item.Label == label);

    private static string Written(IReadOnlyList<CompletionItem> items, string label) =>
        One(items, label).TextEdit.NewText;

    private static Task<IReadOnlyList<CompletionItem>> CompletionAsync(
        TestClient client, Position position, CancellationToken cancellation) =>
        client.RequestAsync<IReadOnlyList<CompletionItem>>("textDocument/completion",
            new { textDocument = new { uri = Uri }, position }, cancellation);

    private static async Task<TestClient> OpenAsync(CancellationToken cancellation)
    {
        var client = await TestClient.StartAsync(TestClient.Capable(), cancellation);
        await client.OpenAsync(Uri, Source);
        await client.NextDiagnosticsAsync(Uri, cancellation);
        return client;
    }
}
