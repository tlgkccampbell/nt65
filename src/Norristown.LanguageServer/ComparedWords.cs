using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.LanguageServer;

/// <summary>
/// The words a file's conditions compare with what a parameter stands for (<see cref="ComparedWord"/>).
/// A word is never looked up, so no reference stands there; these are what hover, colour and
/// completion say of one instead.
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
