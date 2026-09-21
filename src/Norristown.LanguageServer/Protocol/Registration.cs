namespace Norristown.LanguageServer.Protocol;

/// <summary>Something a server asks a client to do for it, after they have connected.</summary>
/// <param name="Id">What it is called, so that it can be taken back.</param>
/// <param name="Method">The message it is about.</param>
/// <param name="RegisterOptions">What it asks for, whose shape the method decides.</param>
internal sealed record Registration(string Id, string Method, object? RegisterOptions);
