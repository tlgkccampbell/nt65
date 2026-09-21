namespace Norristown.LanguageServer.Protocol;

/// <summary>What a hint is, which is how an editor colours it.</summary>
internal enum InlayHintKind
{
    /// <summary>Something about what a name holds, which the line does not write.</summary>
    Type = 1,

    /// <summary>The name of the parameter an argument is for.</summary>
    Parameter = 2,
}
