using Norristown.Emit;
using Norristown.Project;
using Norristown.Semantics;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Checks, end to end, that the addresses of zero-page data are read back from what the pinned
/// ca65 and ld65 build. The program is emitted as <c>nt65 build</c> emits it, assembled with
/// <c>ca65 -g</c> and linked with <c>ld65 --dbgfile</c>.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class BuiltAddressesOracleTests
{
    private const string Config = """
        MEMORY {
            ZP:  start = $0080, size = $0080, type = rw;
            RAM: start = $0800, size = $1000, file = %O;
        }
        SEGMENTS {
            ZEROPAGE: load = ZP, type = zp;
            CODE:     load = RAM, type = ro;
        }
        """;

    /// <summary>
    /// The data of two modules is placed in the zero page in link order, and each declaration
    /// takes the address ld65 gave its label.
    /// </summary>
    [Fact]
    public void TheAddressesAreThoseLd65Gave()
    {
        var ca65 = Ca65Oracle.PinnedPath;
        Assert.SkipUnless(File.Exists(ca65), $"the pinned ca65 is not at {ca65}; run scripts/build-cc65.ps1");

        var project = ProjectSettings.None with { Out = "build" };
        var analysis = Compiler.Analyze(
            [
                new SourceFile("src/a.nt65", ".module alpha\n.segment ZEROPAGE\n.data ptr: .word\n.export .data spare: .byte\n"),
                new SourceFile("src/b.nt65", ".module beta\n.segment ZEROPAGE\n.data count: .byte\n"),
            ],
            project);
        var compilation = Compiler.Emit(analysis, project);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Severity == Severity.Error);
        var result = Ca65Oracle.Pinned.Link(Config, Ca65Oracle.AtTheirPaths(compilation.Ca65), debugFile: true);
        Assert.True(result.Succeeded, result.Messages);

        var root = Directory.CreateTempSubdirectory("nt65-built-");
        try
        {
            foreach (var output in compilation.Outputs.Where(output => output.Kind == OutputKind.LineMap))
                Write(root.FullName, output.Path, output.Text);
            Write(root.FullName, "build/linked.dbg", result.DebugFile);

            var built = BuiltAddresses.Of(analysis, project, root.FullName);

            Assert.NotNull(built);
            Assert.Equal(0x80, built.Addresses[Symbol(analysis, "src/a.nt65", "ptr")]);
            Assert.Equal(0x82, built.Addresses[Symbol(analysis, "src/a.nt65", "spare")]);
            Assert.Equal(0x83, built.Addresses[Symbol(analysis, "src/b.nt65", "count")]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Returns the data declaration <paramref name="file"/> declares under <paramref name="name"/>.</summary>
    private static Symbol Symbol(ProgramAnalysis analysis, string file, string name) =>
        analysis.Program.Current(analysis.ModelFor(file)!.Symbols.Single(symbol => symbol.Name == name && symbol.Kind == SymbolKind.Data));

    /// <summary>Writes <paramref name="text"/> at a path relative to <paramref name="root"/>.</summary>
    private static void Write(string root, string path, string text)
    {
        var full = Path.GetFullPath(path, root);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
