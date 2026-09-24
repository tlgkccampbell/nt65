namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents one run of numbers replaced by another, so that a long file's tokens can be updated
/// without resending all of them.
/// </summary>
/// <param name="Start">The index in the client's current numbers where the change begins.</param>
/// <param name="DeleteCount">How many numbers are removed.</param>
/// <param name="Data">The numbers inserted in their place, or null when there are none.</param>
internal sealed record SemanticTokensEdit(int Start, int DeleteCount, IReadOnlyList<int>? Data);