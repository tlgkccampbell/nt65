namespace Norristown.LanguageServer;

/// <summary>
/// The kinds of change an editor offers, as the protocol names them. A fix answers something
/// reported; a refactor is asked for at a selection and answers nothing.
/// </summary>
internal static class CodeActionKinds
{
    /// <summary>A fix for a diagnostic.</summary>
    public const string QuickFix = "quickfix";

    /// <summary>A change that says the same thing another way.</summary>
    public const string Rewrite = "refactor.rewrite";

    /// <summary>A change that lifts code out into a declaration of its own.</summary>
    public const string Extract = "refactor.extract";

    /// <summary>Everything the server offers, for the capabilities it announces.</summary>
    public static IReadOnlyList<string> All => [QuickFix, Rewrite, Extract];
}
