namespace Norristown.Tests;

/// <summary>
/// Represents a new folder under the system's temporary folder, which a test writes files into
/// and which is deleted with everything in it when the folder is disposed.
/// </summary>
/// <param name="prefix">The start of the folder's name, which says which tests made it.</param>
internal sealed class TempFolder(string prefix) : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory(prefix);

    /// <summary>Gets the folder's full path.</summary>
    public string FullName => directory.FullName;

    /// <summary>Returns the full path of <paramref name="path"/>, which is relative to the folder.</summary>
    public string PathOf(string path) => Path.Combine(FullName, path);

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> in the folder, as
    /// <see cref="Repo.WriteText"/> does, with <c>\n</c> line endings and the file's folder created if needed.
    /// </summary>
    public void Write(string path, string text) => Repo.WriteText(PathOf(path), text);

    /// <summary>Returns the text of the file at <paramref name="path"/> in the folder.</summary>
    public string Read(string path) => File.ReadAllText(PathOf(path));

    /// <summary>Deletes the folder and everything in it.</summary>
    public void Dispose() => directory.Delete(recursive: true);
}
