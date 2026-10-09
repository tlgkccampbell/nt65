using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents a store that writes bytes outside the instruction its <c>.patch</c> names, where
/// they land in no instruction that another <c>.patch</c> under the store names. A <c>.patch</c>
/// says which instruction a store rewrites, so the bytes it may write must lie inside that
/// instruction.
/// </summary>
/// <param name="Patch">The <c>.patch</c> that follows the store.</param>
/// <param name="Store">The step of the store.</param>
/// <param name="Written">The step of the instruction the <c>.patch</c> names.</param>
/// <param name="Label">The label the <c>.patch</c> names the instruction by.</param>
/// <param name="Length">The number of bytes the named instruction takes.</param>
/// <param name="Before">Whether the bytes outside the instruction come before it rather than after it.</param>
/// <param name="Landing">
/// The step whose bytes the first byte outside the instruction lands in, or null where it lands
/// outside every statement nt65 laid out in that run of bytes.
/// </param>
/// <param name="LandingLabel">The label that names <paramref name="Landing"/>, or null where none does.</param>
/// <param name="Fix">The change that names the landing instruction with a <c>.patch</c>, or null where none is offered.</param>
internal sealed record MissedPatch(
    PatchDirectiveSyntax Patch, Step Store, Step Written, Symbol Label, int Length, bool Before, Step? Landing,
    Symbol? LandingLabel, DiagnosticFix? Fix)
{
    /// <summary>
    /// Gets a value indicating whether <see cref="Landing"/> is code, an instruction or the
    /// instructions an <c>.ensure</c> emits, rather than data.
    /// </summary>
    public bool LandsInCode => Landing?.Statement is InstructionStatementSyntax or EnsureDirectiveSyntax;

    /// <summary>
    /// Gets the end of the diagnostic's message, which says what the bytes outside the named
    /// instruction land in and what to do about it.
    /// </summary>
    public string Into
    {
        get
        {
            var side = Before ? "before it" : "after it";
            if (Landing is not { } landing)
                return Before ? "and before the start of the code" : "and past the end of the code";
            var text = landing.Statement.GetTextOnOneLine();
            if (!LandsInCode)
                return $"into the data `{text}` {side}; a `.patch` names an instruction, and a store into data needs none";
            return LandingLabel is { } label
                ? $"into `{text}` at `{label.DisplayName}` {side}: name `{label.DisplayName}` with a `.patch`"
                : $"into `{text}` {side}: label that instruction and name it with a `.patch`";
        }
    }
}
