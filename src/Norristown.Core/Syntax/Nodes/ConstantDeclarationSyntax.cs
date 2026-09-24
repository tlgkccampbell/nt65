namespace Norristown.Syntax;

// Hand-written members that Syntax.xml cannot describe. The rest of the class, and its
// summary, are generated.
public sealed partial class ConstantDeclarationSyntax
{
    /// <summary>
    /// Gets a value indicating whether the declaration is a setting, written with <c>?=</c>,
    /// whose value the build may set.
    /// </summary>
    public bool IsSetting => EqualsToken.Kind == SyntaxKind.QuestionEquals;
}
