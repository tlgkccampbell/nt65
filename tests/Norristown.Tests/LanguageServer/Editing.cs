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
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        int Offset(Position position) => position.Line < starts.Count ? starts[position.Line] + position.Character : text.Length;
        foreach (var edit in edits.OrderByDescending(edit => Offset(edit.Range.Start)))
            text = text[..Offset(edit.Range.Start)] + edit.NewText + text[Offset(edit.Range.End)..];
        return text;
    }
}
