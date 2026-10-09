using Norristown.Layout;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents a store that may write the opcode of a patched instruction, under a <c>.patch</c>
/// that lists no variants with <c>as</c>. Without variants nothing bounds what the instruction
/// can become, so such a <c>.patch</c> is an error.
/// </summary>
/// <param name="Patch">The <c>.patch</c> that follows the store.</param>
/// <param name="Store">The step of the store.</param>
/// <param name="Written">The step of the patched instruction.</param>
/// <param name="Label">The label the <c>.patch</c> names the patched instruction by.</param>
/// <param name="Inferred">
/// The one instruction the store can be seen to write, from the immediate load of the stored
/// register before it, or <see cref="MnemonicKind.None"/> where none can be seen.
/// </param>
internal sealed record UnlistedPatch(
    PatchDirectiveSyntax Patch, Step Store, Step Written, Symbol Label, MnemonicKind Inferred);
