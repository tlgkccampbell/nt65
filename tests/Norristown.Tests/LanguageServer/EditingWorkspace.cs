using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Holds the three files that the completion, signature help and workspace symbol tests open,
/// and places a line of a test's own in the main one.
/// </summary>
internal static class EditingWorkspace
{
    /// <summary>The URI of the module that exports a routine, a constant and a structure.</summary>
    public const string GfxUri = "file:///c:/work/gfx.nt65";

    /// <summary>The URI of the module whose name has two parts.</summary>
    public const string VicUri = "file:///c:/work/hw/vic.nt65";

    /// <summary>The URI of the file in which the tests make their requests.</summary>
    public const string MainUri = "file:///c:/work/main.nt65";

    /// <summary>
    /// The text of the file at <see cref="GfxUri"/>, which also holds a routine it does not export.
    /// </summary>
    public const string Gfx = """
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

    /// <summary>The text of the file at <see cref="VicUri"/>.</summary>
    public const string Vic = ".module hw::vic\n.export BORDER = $d020\n";

    /// <summary>
    /// The file in which completion is requested. Each <c>|name</c> marks a place where a test may
    /// put a line of its own, and <c>name</c> is what the test calls that place. A test that names
    /// the place <c>top</c> gets its line at the file's top level instead.
    /// </summary>
    public const string Main = """
        .module main
        .use gfx::{clear}
        .use hw::vic
        .signature fast = a8, i8
        .func twice(n) = n * 2
        .struct Point {
            |struct
        }
        .data table: .byte[] {
            |values
        }
        .scope loose {
            |scope
        }
        .segment CODE
        .macro poke(address: expr, value: const = 0) {
            |macro
        }
        .enum Pitch {
            low
            high
        }
        .macro tone(p: Pitch, w: one(up, down), s: list(Pitch)) {
        }
        .macro pick(src: operand(imm, zp), reg: one(x, y)) {
            |pick
        }
        .proc main {
        @loop:
            |body
            rts
        }
        """;

    /// <summary>
    /// Returns the main file with <paramref name="line"/> at the place marked
    /// <paramref name="where"/>, the other marked places left empty, and the caret position
    /// given by the line's own <c>|</c>. The place <c>top</c> puts the line at top level, after
    /// everything else. Any other name the file does not mark is a mistake in the test and
    /// throws, so that a misspelled place cannot quietly test top level instead.
    /// </summary>
    public static (string Text, Position Position) WithLine(string where, string line)
    {
        var text = Main.ReplaceLineEndings("\n");
        var lines = text.Split('\n').ToList();
        var at = where == "top" ? lines.Count : lines.FindIndex(l => l.Trim() == "|" + where);
        if (at < 0)
            throw new ArgumentException($"the main file marks no place named \"{where}\"", nameof(where));
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('|'))
                lines[i] = "";
        }
        if (at == lines.Count)
            lines.Add(line);
        else
            lines[at] = line;
        var column = lines[at].IndexOf('|', StringComparison.Ordinal);
        lines[at] = lines[at].Replace("|", "", StringComparison.Ordinal);
        return (string.Join('\n', lines), new Position(at, Math.Max(0, column)));
    }

    /// <summary>Starts a client with the three files open and <paramref name="main"/> as the main file.</summary>
    public static Task<TestClient> OpenAsync(string main, CancellationToken timeout) =>
        TestClient.OpenedAsync(timeout, (GfxUri, Gfx.ReplaceLineEndings("\n")), (VicUri, Vic), (MainUri, main));
}
