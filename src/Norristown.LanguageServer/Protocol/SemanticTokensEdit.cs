namespace Norristown.LanguageServer.Protocol;

/// <summary>One run of numbers replaced by another, which is how a long file is kept in step.</summary>
/// <param name="Start">Where in the numbers the client holds the change begins.</param>
/// <param name="DeleteCount">How many of them go.</param>
/// <param name="Data">What takes their place, or null where nothing does.</param>
internal sealed record SemanticTokensEdit(int Start, int DeleteCount, IReadOnlyList<int>? Data);