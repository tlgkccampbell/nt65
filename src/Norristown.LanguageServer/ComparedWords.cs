using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The bare words a file's macro conditions compare a parameter's argument with, such as a mode
/// or a word a <c>one(...)</c> lists (<see cref="ComparedWord"/>). Such a word is never looked up
/// as a name, so there is no symbol reference for it; hover, semantic colouring and completion
/// use these instead.
/// </summary>
internal static class ComparedWords
{
    /// <summary>Every compared word in the file.</summary>
    public static IEnumerable<ComparedWord> In(SemanticModel model) => ComparedWord.In(model.Tree.Root, name => model.SymbolOf(name));

    /// <summary>The compared word at <paramref name="position"/>, or null when there is none.</summary>
    public static ComparedWord? At(SemanticModel model, int position)
    {
        var token = model.Tree.Root.FindToken(position);
        return token.Parent?.AncestorsAndSelf().OfType<NameExpressionSyntax>().FirstOrDefault() is { Parent: BinaryExpressionSyntax comparison } name
            && ComparedWord.Of(comparison, name, named => model.SymbolOf(named)) is { } compared
            && compared.Word.Span == token.Span
                ? compared
                : null;
    }
}
