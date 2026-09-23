namespace Norristown.LanguageServer.Protocol;

/// <summary>What a hint is, which is how an editor colours it.</summary>
internal enum InlayHintKind
{
    /// <summary>Information about what a name holds that the line itself does not show.</summary>
    Type = 1,

    /// <summary>The name of the parameter an argument is for.</summary>
    Parameter = 2,
}
