using System.Text;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Writes the syntax classes the node table describes into whatever compilation carries the
/// table as an additional file, which is <c>Norristown.Core</c>. The pipeline hangs off the
/// table's own text, so an edit anywhere else in the project regenerates nothing.
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
        var table = context.AdditionalTextsProvider
            .Where(file => NameOf(file.Path) == TableName)
            .Select((file, token) => new Table(file.Path, file.GetText(token)?.ToString()));
        context.RegisterSourceOutput(table, Write);
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

        // No byte-order mark: the repository is UTF-8 without one, and these files are read.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var file in files)
            context.AddSource(file.Key, SourceText.From(file.Value, utf8));
    }

    /// <summary>Reports a table that cannot be read, on <paramref name="line"/> when it knows one.</summary>
    private static void Report(SourceProductionContext context, string path, int line, string message)
    {
        var at = new LinePosition(line > 0 ? line - 1 : 0, 0);
        var where = Location.Create(path, new TextSpan(0, 0), new LinePositionSpan(at, at));
        context.ReportDiagnostic(Diagnostic.Create(Unreadable, where, message));
    }

    /// <summary>The last segment of <paramref name="path"/>, whichever separator it is written with.</summary>
    private static string NameOf(string path)
    {
        var separator = path.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? path : path.Substring(separator + 1);
    }

    /// <summary>The table the generator reads, and where it came from.</summary>
    /// <param name="Path">The additional file's path, which the diagnostic points at.</param>
    /// <param name="Text">Its text, or null when the compiler could not read it.</param>
    private sealed record Table(string Path, string? Text);
}
