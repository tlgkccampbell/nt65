using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>Builds the model for one file of nt65, the way a test wants to ask about it.</summary>
internal static class Analysis
{
    public const string Path = "main.nt65";

    /// <summary>The model for <paramref name="text"/>, with the segments the file declares.</summary>
    public static SemanticModel Model(string text)
    {
        var tree = SyntaxTree.Parse(Path, text);
        return SemanticModel.Create(tree, SegmentTable.Build([tree], []));
    }

    /// <summary>
    /// Settings for a test that writes a fragment rather than a whole program. Such a fragment is
    /// typically an unexported routine that nothing calls, which would draw the unused-symbol
    /// warning, and the tests that use these settings are about something else, so the warning is
    /// turned off. A test about the warning itself writes a whole program.
    /// </summary>
    public static ProjectSettings Fragment { get; } =
        ProjectSettings.None with
        {
            Severities = new SortedDictionary<string, Severity?>(StringComparer.Ordinal) { ["unused-symbol"] = null },
        };

    /// <summary>
    /// The whole program for several files, the way the compiler reads it: every file sees
    /// what the others export.
    /// </summary>
    public static ProgramAnalysis Program(params (string Path, string Text)[] files) =>
        Program(ProjectSettings.None, files);

    /// <summary>The same, for a program the project file says something about.</summary>
    public static ProgramAnalysis Program(ProjectSettings project, params (string Path, string Text)[] files) =>
        Compiler.Analyze([.. files.Select(file => new SourceFile(file.Path, file.Text))], project);

    /// <summary>One file of a program, by the path it was given.</summary>
    public static SemanticModel File(this ProgramAnalysis analysis, string path)
    {
        var model = analysis.ModelFor(path);
        Assert.NotNull(model);
        return model;
    }

    /// <summary>What the whole program says is wrong, as <c>file:line: message</c>.</summary>
    public static IReadOnlyList<string> Problems(this ProgramAnalysis analysis) =>
        [.. analysis.Diagnostics.Select(d => $"{d.Span.File}:{d.Span.Line}: {d.Message}")];

    /// <summary>The ca65 a program becomes, by output path.</summary>
    public static Dictionary<string, string> Outputs(params (string Path, string Text)[] files) =>
        Outputs(ProjectSettings.None, files);

    /// <summary>The same, for a program the project file says something about.</summary>
    public static Dictionary<string, string> Outputs(
        ProjectSettings project, params (string Path, string Text)[] files) =>
        Compiler.Compile([.. files.Select(file => new SourceFile(file.Path, file.Text))], project)
            .Outputs.ToDictionary(output => output.Path, output => output.Text, StringComparer.Ordinal);

    /// <summary>The one symbol named <paramref name="name"/>, wherever it is declared.</summary>
    public static Symbol Symbol(this SemanticModel model, string name) =>
        model.Symbols.Single(symbol => symbol.DisplayName == name);

    /// <summary>What the file says is wrong, as <c>line: message</c>.</summary>
    public static IReadOnlyList<string> Problems(this SemanticModel model) =>
        [.. model.Diagnostics.Select(d => $"{d.Span.Line}: {d.Message}")];

    /// <summary>The offset of the <paramref name="occurrence"/>th <paramref name="find"/> in the file.</summary>
    public static int Offset(this SemanticModel model, string find, int occurrence = 1)
    {
        var offset = -1;
        for (var i = 0; i < occurrence; i++)
            offset = model.Tree.Text.IndexOf(find, offset + 1, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"the file has no {occurrence} occurrence(s) of \"{find}\"");
        return offset;
    }

    /// <summary>What the name written at the <paramref name="occurrence"/>th <paramref name="find"/> means.</summary>
    public static Symbol SymbolAt(this SemanticModel model, string find, int occurrence = 1)
    {
        var reference = model.ReferenceAt(model.Offset(find, occurrence));
        Assert.NotNull(reference);
        return reference.Symbol;
    }
}
