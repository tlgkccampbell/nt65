namespace Norristown.LanguageServer;

/// <summary>
/// Defines the kinds of code action the server offers, as the protocol names them. A quick fix
/// resolves a reported diagnostic, and a refactor is requested at a selection and is not tied to
/// any diagnostic.
/// </summary>
internal static class CodeActionKinds
{
    /// <summary>A fix for a diagnostic.</summary>
    public const string QuickFix = "quickfix";

    /// <summary>A change that rewrites code without changing what it means.</summary>
    public const string Rewrite = "refactor.rewrite";

    /// <summary>A change that lifts code out into a declaration of its own.</summary>
    public const string Extract = "refactor.extract";

    /// <summary>Gets every kind the server offers, for the capabilities it announces.</summary>
    public static IReadOnlyList<string> All => [QuickFix, Rewrite, Extract];
}
