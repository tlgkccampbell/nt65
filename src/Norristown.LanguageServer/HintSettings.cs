using System.Text.Json;

namespace Norristown.LanguageServer;

/// <summary>
/// Which kinds of hint the editor shows, one switch each, named for what the switch shows. A
/// file opened for the first time should look like the file, so what is on by default is what
/// is rare and surprising; what is on nearly every line is asked for rather than assumed.
/// </summary>
/// <param name="StateChanges">A line after which a width, the emulation flag, D or B differs.</param>
/// <param name="LongBranches">A branch layout had to write as the five-byte form.</param>
/// <param name="ImpliedValues">A value the declaration does not write.</param>
/// <param name="ParameterNames">Which parameter a positional argument is for.</param>
/// <param name="Cycles">What every instruction costs, which is the one that is off.</param>
internal sealed record HintSettings(
    bool StateChanges,
    bool LongBranches,
    bool ImpliedValues,
    bool ParameterNames,
    bool Cycles)
{
    /// <summary>What an editor that has said nothing gets.</summary>
    public static HintSettings Default { get; } = new(true, true, true, true, false);

    /// <summary>Whether anything at all is shown, so that a request answers nothing without working.</summary>
    public bool Any => StateChanges || LongBranches || ImpliedValues || ParameterNames || Cycles;

    /// <summary>
    /// What the <c>nt65</c> settings of an editor say, whether they arrived with the
    /// <c>initialize</c> request or in a later <c>didChangeConfiguration</c>. A switch the
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

    private static bool Flag(JsonElement hints, string name, bool unsaid) =>
        hints.TryGetProperty(name, out var said) && said.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? said.ValueKind == JsonValueKind.True
            : unsaid;
}
