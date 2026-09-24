using Norristown.LanguageServer;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests the scanner that finds a <c>files</c> entry in <c>nt65.json</c> so that moving a file can
/// rewrite it.
/// </summary>
public sealed class JsonTests
{
    /// <summary>
    /// Only the top-level <c>files</c> property counts. The same string earlier in the file, as a
    /// value or as a key in a nested object, is passed over.
    /// </summary>
    [Fact]
    public void AnEntryIsFoundUnderTheTopLevelKeyOnly()
    {
        const string Text = """
            {
              "out": "files",
              "settings": { "files": 1 },
              // "files": ["a.nt65"]
              "files" : ["b.nt65", "a.nt65"]
            }
            """;

        var span = Json.Entry(Text, "files", "a.nt65");

        Assert.NotNull(span);
        Assert.Equal("\"a.nt65\"", Text.Substring(span.Value.Start, span.Value.Length));
        Assert.Equal(Text.LastIndexOf("\"a.nt65\"", StringComparison.Ordinal), span.Value.Start);
    }

    /// <summary>A file with no top-level <c>files</c> property has no entry to rewrite.</summary>
    [Fact]
    public void ANestedKeyAloneIsNoEntry() =>
        Assert.Null(Json.Entry("""{ "settings": { "files": ["a.nt65"] } }""", "files", "a.nt65"));
}
