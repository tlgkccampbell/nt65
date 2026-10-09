namespace Norristown;

/// <summary>
/// Represents the change a diagnostic's message names as its fix, for an editor to offer. It
/// describes what to change rather than holding the edit, because the edit is worked out against
/// the file as it is when the fix is requested.
/// </summary>
/// <param name="Kind">The kind of change.</param>
/// <param name="Text">
/// The text the change inserts, such as the mnemonic, the name to export or the path to import.
/// </param>
/// <param name="At">
/// The declaration the change applies to, when that is not where the diagnostic is reported. For
/// example, it is the label a <c>.state</c> goes after, or the declaration whose module exports it.
/// </param>
/// <param name="Caveat">
/// The words the change's title ends with to say what the change relies on that nothing promises,
/// such as ", though `g` does not promise Z", or null where it relies on nothing of the kind.
/// </param>
public sealed record DiagnosticFix(FixKind Kind, string? Text = null, Span? At = null, string? Caveat = null);
