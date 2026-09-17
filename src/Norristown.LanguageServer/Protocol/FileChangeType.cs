namespace Norristown.LanguageServer.Protocol;

/// <summary>What happened to a file on disk.</summary>
internal enum FileChangeType
{
    /// <summary>It was created.</summary>
    Created = 1,

    /// <summary>Its contents changed.</summary>
    Changed = 2,

    /// <summary>It was deleted.</summary>
    Deleted = 3,
}
