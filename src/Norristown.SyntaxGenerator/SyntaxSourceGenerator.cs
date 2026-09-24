using System.Collections.Immutable;
using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Generates the syntax classes the node table describes into any compilation that includes
/// the table as an additional file, which is <c>Norristown.Core</c>. The pipeline depends only
/// on the table's text, so an edit anywhere else in the project regenerates nothing.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SyntaxSourceGenerator : IIncrementalGenerator
{
    /// <summary>The name of the additional file the table is read from.</summary>
    private const string TableName = "Syntax.xml";

    private static readonly DiagnosticDescriptor Unreadable = new(
        "NT1001",
        "The node table cannot be read",
        "{0}",
        "Norristown.SyntaxGenerator",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The tables are collected rather than taken one at a time, so that a project without the
        // table gets one diagnostic saying the table is missing, rather than errors about the
        // hundred classes it then lacks.
        var tables = context.AdditionalTextsProvider
            .Where(file => NameOf(file.Path) == TableName)
            .Select((file, token) => new Table(file.Path, file.GetText(token)?.ToString()))
            .Collect();
        context.RegisterSourceOutput(tables, Write);
    }

    private static void Write(SourceProductionContext context, ImmutableArray<Table> tables)
    {
        if (tables.IsEmpty)
        {
            Report(context, path: null, 0,
                $"{TableName} is not among the project's AdditionalFiles, so there is no syntax to write");
            return;
        }
        foreach (var table in tables)
            Write(context, table);
    }

    private static void Write(SourceProductionContext context, Table table)
    {
        if (table.Text is null)
        {
            Report(context, table.Path, 0, $"{TableName} holds no text the compiler can read");
            return;
        }

        SortedDictionary<string, string> files;
        try
        {
            files = SyntaxWriter.Files(NodeTable.Read(table.Text));
        }
        catch (XmlException malformed)
        {
            Report(context, table.Path, malformed.LineNumber, malformed.Message);
            return;
        }
        catch (InvalidOperationException wrong)
        {
            Report(context, table.Path, 0, wrong.Message);
            return;
        }

        // No byte-order mark, to match the repository's UTF-8 files; generated files are read too.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var file in files)
            context.AddSource(file.Key, SourceText.From(file.Value, utf8));
    }

    /// <summary>
    /// Reports a table that cannot be read, on <paramref name="line"/> when the line is known, and
    /// at no location when there is no file to point at.
    /// </summary>
    private static void Report(SourceProductionContext context, string? path, int line, string message)
    {
        var at = new LinePosition(line > 0 ? line - 1 : 0, 0);
        var where = path is null
            ? Location.None
            : Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(at, at));
        context.ReportDiagnostic(Diagnostic.Create(Unreadable, where, message));
    }

    /// <summary>
    /// Returns the last segment of <paramref name="path"/>, with either <c>/</c> or <c>\</c> as the
    /// separator.
    /// </summary>
    private static string NameOf(string path)
    {
        var separator = path.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? path : path.Substring(separator + 1);
    }

    /// <summary>Represents the table the generator reads, and where it came from.</summary>
    /// <param name="Path">The additional file's path, which the diagnostic points at.</param>
    /// <param name="Text">The file's text, or null if the compiler could not read it.</param>
    private sealed record Table(string Path, string? Text);
}
