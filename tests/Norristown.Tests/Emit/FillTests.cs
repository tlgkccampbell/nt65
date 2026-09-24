using Norristown.Tests.Semantics;

namespace Norristown.Tests.Emit;

/// <summary>
/// Checks which rows of data the output folds into a <c>.res</c>. A row folds when every line
/// writes the same single byte, however the source spells the directive, and never when a line
/// writes anything other than one byte.
/// </summary>
public sealed class FillTests
{
    /// <summary>
    /// Directives are case-insensitive, and the space after one is any whitespace, so neither the
    /// case nor the spacing of <c>.byte</c> decides whether a row is a fill.
    /// </summary>
    [Theory]
    [InlineData(".data d {\n.byte 7\n.byte 7\n.byte 7\n}\n")]
    [InlineData(".data d {\n.BYTE 7\n.BYTE 7\n.BYTE 7\n}\n")]
    [InlineData(".data d {\n.byte\t7\n.byte\t7\n.byte\t7\n}\n")]
    [InlineData(".data d: .BYTE[] {\n7\n7\n7\n}\n")]
    public void ARowOfOneByteFoldsHoweverItIsSpelled(string text)
    {
        var main = Output(text);

        Assert.Contains("    .res 3, 7\n", main, StringComparison.Ordinal);
        Assert.DoesNotContain("7\n    .", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty string writes no bytes, so a row of them is not a fill, even though each line has
    /// a single value.
    /// </summary>
    [Fact]
    public void ARowOfEmptyTextIsNotAFill()
    {
        var main = Output(".data d {\n.byte \"\"\n.byte \"\"\n.byte \"\"\n}\n");

        Assert.DoesNotContain(".res", main, StringComparison.Ordinal);
        Assert.Equal(3, main.Split('\n').Count(line => line.Trim() == ".byte \"\""));
    }

    /// <summary>
    /// A row inside a counted <c>.repeat</c> folds to the counter, not to the value the first
    /// iteration wrote.
    /// </summary>
    [Fact]
    public void ARowInACountedRepeatFoldsToTheCounter()
    {
        var main = Output(".data t: .byte[] {\n.repeat 4, i {\n i\n i\n i\n}\n}\n");

        Assert.Contains(".repeat 4, i\n        .res 3, i\n    .endrepeat\n", main, StringComparison.Ordinal);
    }

    /// <summary>Returns the output for <paramref name="text"/>, which is put in the code segment.</summary>
    private static string Output(string text) =>
        Analysis.Outputs(("main.nt65", ".module main\n.segment CODE\n" + text))["main.s"];
}
