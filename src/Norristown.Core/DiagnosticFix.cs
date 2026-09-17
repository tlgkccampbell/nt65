namespace Norristown;

/// <summary>
/// The change a diagnostic's message names as its fix, for an editor to offer. It says what to
/// change rather than holding the edit: the edit is worked out against the file as it is when
/// the fix is asked for.
/// </summary>
/// <param name="Kind">What the change is.</param>
/// <param name="Text">What it writes: the mnemonic, the name to export, or the path to bring in.</param>
/// <param name="At">
/// The declaration it changes, where that is not where the diagnostic is reported: the label a
/// <c>.state</c> goes after, or the declaration whose module exports it.
/// </param>
public sealed record DiagnosticFix(FixKind Kind, string? Text = null, Span? At = null);
