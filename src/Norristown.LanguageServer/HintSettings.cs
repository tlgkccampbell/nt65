using System.Text.Json;

namespace Norristown.LanguageServer;

/// <summary>
/// Represents which kinds of inlay hint the editor shows, with one switch for each kind, named
/// for what the switch shows. A file opened for the first time should look like its source rather
/// than be crowded with hints, so the hints on by default are the rare and surprising ones. A hint
/// that would appear on nearly every line is off until the programmer turns it on.
/// </summary>
/// <param name="StateChanges">
/// Whether to mark a line after which a register width, the emulation flag, D or B changes.
/// </param>
/// <param name="LongBranches">
/// Whether to mark a branch that layout had to emit in its five-byte long form.
/// </param>
/// <param name="ImpliedValues">Whether to show a value the declaration does not give explicitly.</param>
/// <param name="ParameterNames">Whether to show which parameter a positional argument is for.</param>
/// <param name="Cycles">
/// Whether to show the cycle cost of every instruction. This is the only hint off by default.
/// </param>
internal sealed record HintSettings(
    bool StateChanges,
    bool LongBranches,
    bool ImpliedValues,
    bool ParameterNames,
    bool Cycles)
{
    /// <summary>Gets the settings for an editor that has not specified any.</summary>
    public static HintSettings Default { get; } = new(true, true, true, true, false);

    /// <summary>
    /// Gets a value indicating whether any hint is shown at all, so that a request can return
    /// nothing without computing anything.
    /// </summary>
    public bool Any => StateChanges || LongBranches || ImpliedValues || ParameterNames || Cycles;

    /// <summary>
    /// Returns the settings that an editor's <c>nt65</c> settings specify, whether they arrived
    /// with the <c>initialize</c> request or in a later <c>didChangeConfiguration</c>. A switch the
    /// editor does not mention keeps its default.
    /// </summary>
    public static HintSettings Of(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } options
            || !options.TryGetProperty("inlayHints", out var hints)
            || hints.ValueKind != JsonValueKind.Object)
        {
            return Default;
        }
        return new HintSettings(
            Flag(hints, "stateChanges", Default.StateChanges),
            Flag(hints, "longBranches", Default.LongBranches),
            Flag(hints, "impliedValues", Default.ImpliedValues),
            Flag(hints, "parameterNames", Default.ParameterNames),
            Flag(hints, "cycles", Default.Cycles));
    }

    private static bool Flag(JsonElement hints, string name, bool fallback) =>
        hints.TryGetProperty(name, out var setting) && setting.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? setting.ValueKind == JsonValueKind.True
            : fallback;
}
