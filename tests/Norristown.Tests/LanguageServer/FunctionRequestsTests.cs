namespace Norristown.Tests.LanguageServer;

/// <summary>
/// Tests what an editor can do with a named argument. A named argument is a reference to the
/// parameter it names, in a call to a <c>.func</c> and in a macro call alike, so going to the
/// definition, finding every use, renaming and coloring all treat it as the parameter.
/// </summary>
public sealed class FunctionRequestsTests
{
    private const string LibUri = "file:///c:/work/lib.nt65";
    private const string MainUri = "file:///c:/work/main.nt65";

    /// <summary>A module that exports a function with a default.</summary>
    private const string Lib = """
        .module lib
        .export scaled
        .func scaled(value, factor = 2) = value * factor
        """;

    /// <summary>A module that calls the function, and a macro of its own, with named arguments.</summary>
    private const string Main = """
        .module main
        .use lib::scaled
        .macro note(pitch, frames = 1) {
            .byte pitch, frames
        }
        .segment RODATA
        .export .data table {
            .byte scaled(3, factor = 4)
            .byte scaled(
                5,
                factor = 6)
            note!(60, frames = 8)
        }
        """;

    /// <summary>
    /// A named argument goes to the parameter it names, in the module that declares the function,
    /// and is found among the parameter's uses, in each module.
    /// </summary>
    [Fact]
    public async Task ANamedArgumentIsAUseOfItsParameter()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (LibUri, Lib), (MainUri, Main));

        var definition = await client.DefinitionAsync(MainUri, Locate.At(Main, "3, fac|tor"), timeout);
        Assert.Equal(LibUri, definition?.Uri);
        Assert.Equal(Locate.At(Lib, "|factor = 2"), definition?.Range.Start);

        var hover = await client.HoverAsync(MainUri, Locate.At(Main, "3, fac|tor"), timeout);
        Assert.Contains("factor", hover?.Contents.Value, StringComparison.Ordinal);

        var uses = await client.ReferencesAsync(MainUri, Locate.At(Main, "3, fac|tor"), includeDeclaration: true, timeout);
        Assert.Equal(
            [(LibUri, 2), (LibUri, 2), (MainUri, 7), (MainUri, 10)],
            uses.Select(use => (use.Uri, use.Range.Start.Line)).Order());
    }

    /// <summary>
    /// Renaming a parameter renames the named arguments that name it, in every module, and
    /// renaming from a named argument renames the parameter. A macro's parameter is renamed the
    /// same way.
    /// </summary>
    [Fact]
    public async Task RenamingAParameterRenamesItsNamedArguments()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (LibUri, Lib), (MainUri, Main));

        var edit = await client.RenameAsync(MainUri, Locate.At(Main, "fac|tor = 6"), "scale", timeout);
        Assert.NotNull(edit);
        Assert.Equal([2, 2], edit.Changes[LibUri].Select(change => change.Range.Start.Line).Order());
        Assert.Equal([7, 10], edit.Changes[MainUri].Select(change => change.Range.Start.Line).Order());

        var macro = await client.RenameAsync(MainUri, Locate.At(Main, "fra|mes = 8"), "length", timeout);
        Assert.NotNull(macro);
        Assert.Equal([2, 3, 11], macro.Changes[MainUri].Select(change => change.Range.Start.Line).Order());
    }

    /// <summary>A named argument is colored as the parameter it names.</summary>
    [Fact]
    public async Task ANamedArgumentIsColouredAsAParameter()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (LibUri, Lib), (MainUri, Main));

        var at = DecodedTokens.TypesAt(
            client.Initialized.Capabilities.SemanticTokensProvider!.Legend, await client.SemanticTokensAsync(MainUri, timeout));
        var call = Locate.At(Main, "3, |factor");
        Assert.Equal("parameter", at[(call.Line, call.Character)]);
        var continued = Locate.At(Main, "|factor = 6");
        Assert.Equal("parameter", at[(continued.Line, continued.Character)]);
        var macro = Locate.At(Main, "|frames = 8");
        Assert.Equal("parameter", at[(macro.Line, macro.Character)]);
    }

    /// <summary>
    /// Signature help shows a function's defaults and marks the parameter a named argument names.
    /// </summary>
    [Fact]
    public async Task SignatureHelpMarksTheNamedParameter()
    {
        var timeout = TestTimeout.Token();
        await using var client = await TestClient.OpenedCleanlyAsync(timeout, (LibUri, Lib), (MainUri, Main));

        var help = await client.SignatureHelpAsync(MainUri, Locate.At(Main, "factor = |4"), timeout);
        Assert.Equal("scaled(value, factor = 2)", help?.Signatures[0].Label);
        Assert.Equal(1, help?.ActiveParameter);
    }
}
