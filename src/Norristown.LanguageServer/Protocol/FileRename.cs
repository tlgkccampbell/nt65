namespace Norristown.LanguageServer.Protocol;

/// <summary>One file about to move.</summary>
/// <param name="OldUri">Where it is now.</param>
/// <param name="NewUri">Where it is going.</param>
internal sealed record FileRename(string OldUri, string NewUri);
