using System.Collections.Immutable;
using Norristown.SyntaxGenerator;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Provides the repository's node table, the XML the source generator writes the syntax classes
/// from, for the tests that check the generated classes and the parser against it.
/// </summary>
internal static class SyntaxTable
{
    /// <summary>The table's full path.</summary>
    public static readonly string Path = Repo.Path(NodeTable.File.Split('/'));

    /// <summary>Returns the table's text.</summary>
    public static string Text() => Repo.ReadText(Path);

    /// <summary>Returns the table's rows, one per node class.</summary>
    public static ImmutableArray<NodeRow> Rows() => NodeTable.Read(Text());
}
