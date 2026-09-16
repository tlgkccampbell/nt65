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
