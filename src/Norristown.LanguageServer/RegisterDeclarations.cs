using Norristown.Flow;
using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// Builds the changes that write what the analysis finds about a routine's registers into its
/// signature. A <c>reads</c> item lists the registers whose values from its caller the routine
/// uses, and a <c>keeps</c> item lists those it returns as it found them. A refactoring offers
/// these changes on the routine's line, and the code lenses that show the same registers make
/// them when clicked.
/// </summary>
internal static class RegisterDeclarations
{
    /// <summary>
    /// Returns the change that declares the <c>reads</c> the analysis finds for
    /// <paramref name="region"/>'s routine. Returns null where the analysis cannot follow every
    /// path, or where the signature already declares a <c>reads</c>. The region then holds the
    /// declaration rather than what the body reads, and a body that reads more is reported with
    /// a fix of its own.
    /// </summary>
    public static Change? Reads(SyntaxTree tree, FlowRegion region)
    {
        var routine = region.Routine;
        if (!Declarable(tree, region) || !region.Reads.Complete || routine.Signature?.Reads == region.Reads.Read)
            return null;
        return Declared(tree, region, "reads", region.Reads.Read == Registers.None ? "none" : Named(region.Reads.Read));
    }

    /// <summary>
    /// Returns the change that declares the <c>keeps</c> the analysis finds for
    /// <paramref name="region"/>'s routine. Returns null where the routine never returns, where
    /// the analysis cannot follow every path, where it keeps nothing, or where the signature
    /// already declares the same registers.
    /// </summary>
    public static Change? Keeps(SyntaxTree tree, FlowRegion region)
    {
        var kept = region.Registers.Kept;
        if (!Declarable(tree, region) || !region.Total.Ends || !region.Registers.Complete || kept == Registers.None
            || region.Routine.Signature?.Keeps == kept)
        {
            return null;
        }
        return Declared(tree, region, "keeps", Named(kept));
    }

    /// <summary>
    /// Returns a value indicating whether the routine's signature is in <paramref name="tree"/>
    /// and may hold register items. An interrupt handler may not, because nothing calls it.
    /// </summary>
    private static bool Declarable(SyntaxTree tree, FlowRegion region) =>
        region.Routine.Tree == tree && region.Routine.Signature?.IsInterrupt != true;

    /// <summary>
    /// Returns the change that writes <paramref name="name"/> <paramref name="registers"/> into
    /// the signature of <paramref name="region"/>'s routine, or null where its line declares no
    /// routine with a body.
    /// </summary>
    private static Change? Declared(SyntaxTree tree, FlowRegion region, string name, string registers)
    {
        var item = $"{name} {registers}";
        return Edits.RegistersItem(tree, region.Routine.DeclarationSpan.LineIndex, name, item) is { } edit
            ? new Change($"Declare `{item}` for `{region.Routine.Name}`", CodeActionKinds.Rewrite, [edit])
            : null;
    }

    /// <summary>Returns the registers as a signature lists them, such as <c>a, x</c>.</summary>
    private static string Named(Registers registers) => RegisterEffects.Format(registers).ToLowerInvariant();
}
