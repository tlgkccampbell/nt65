using Norristown.LanguageServer.Protocol;

namespace Norristown.Tests.LanguageServer;

/// <summary>Applies the edits the server sends back, the way an editor would.</summary>
internal static class Editing
{
    /// <summary>
    /// Returns <paramref name="text"/> with <paramref name="edits"/> applied. The edits are
    /// applied from the end of the file backward, so that earlier edits' positions stay valid.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        // An editor ends a line at \r\n, \n or a lone \r, which is the rule a tree splits lines by.
        var starts = Norristown.Syntax.SyntaxTree.LineOffsets(text);
        int Offset(Position position) => position.Line < starts.Length ? starts[position.Line] + position.Character : text.Length;
        foreach (var edit in edits.OrderByDescending(edit => Offset(edit.Range.Start)))
            text = text[..Offset(edit.Range.Start)] + edit.NewText + text[Offset(edit.Range.End)..];
        return text;
    }
}
