namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents an <c>nt65/processor</c> request, which asks what the analysis knows of the
/// processor at the caret.
/// </summary>
/// <param name="TextDocument">The document.</param>
/// <param name="Position">The caret.</param>
/// <param name="Callers">
/// The calls the reader chose to see routines entered from, each one of an earlier result's
/// <see cref="ProcessorResult.Callers"/>, or null for none. The one that calls the caret's routine,
/// if any does, is used.
/// </param>
internal sealed record ProcessorParams(TextDocumentIdentifier TextDocument, Position Position, IReadOnlyList<Location>? Callers);
