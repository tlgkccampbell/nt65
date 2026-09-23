namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// What a green node holds somewhere under it, rolled up from its children as it is built, so
/// that a search for one of those things descends only into the slots that contain one. They
/// share one field because a node rolls them all up together, in one pass over its slots.
/// </summary>
[Flags]
internal enum GreenFlags : byte
{
    /// <summary>Nothing to find below here.</summary>
    None = 0,

    /// <summary>This node or something under it carries a <see cref="GreenDiagnostic"/>.</summary>
    ContainsDiagnostics = 1,

    /// <summary>This node or something under it carries a <see cref="SyntaxAnnotation"/>.</summary>
    ContainsAnnotations = 2,
}
