namespace Norristown.Cli;

/// <summary>
/// Writes a file so that a reader sees either its old text or its new text, never a mixture of
/// two.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to a temporary file beside <paramref name="path"/> and then
    /// moves it onto the path. When the move fails, as it does on Windows while another program
    /// holds the file open, the temporary file is removed before the failure is passed on.
    /// </summary>
    public static void Write(string path, string text)
    {
        var beside = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(beside, text);
        try
        {
            File.Move(beside, path, overwrite: true);
        }
        catch
        {
            File.Delete(beside);
            throw;
        }
    }
}
