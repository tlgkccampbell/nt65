using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Represents what a <see cref="RecordWriter"/> needs from the emitter it writes for. That is
/// writing a line, naming a line, reporting what cannot be written and writing one value. The
/// emitter answers each at the point its walk has reached.
/// </summary>
internal interface IRecordOutput
{
    /// <summary>
    /// Writes one line that came from <paramref name="line"/>, holding <paramref name="bytes"/>
    /// bytes, with an optional comment. <paramref name="value"/> is the value of the one byte the
    /// line writes, or null when it writes anything else.
    /// </summary>
    void Code(LineSyntax line, string text, int bytes, string? comment, string? value);

    /// <summary>Writes the label of <paramref name="symbol"/> on a line of its own.</summary>
    void Label(LineSyntax line, Symbol symbol);

    /// <summary>
    /// Writes a line of data with the name of <paramref name="symbol"/> in front, or with no name
    /// when <paramref name="symbol"/> is null.
    /// </summary>
    void Named(LineSyntax line, Symbol? symbol, string text, int bytes, string? comment);

    /// <summary>Reports a node for which nothing can be written.</summary>
    void NotTranspiled(SyntaxNode node);

    /// <summary>
    /// Returns one value of a slot <paramref name="width"/> bytes wide, as the output writes it.
    /// </summary>
    string ValueText(SyntaxNode value, int width, bool bigEndian);
}
