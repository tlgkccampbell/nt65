namespace Norristown.LanguageServer.Protocol;

/// <summary>Specifies what happened to a file on disk.</summary>
internal enum FileChangeType
{
    /// <summary>The file was created.</summary>
    Created = 1,

    /// <summary>The file's contents changed.</summary>
    Changed = 2,

    /// <summary>The file was deleted.</summary>
    Deleted = 3,
}
