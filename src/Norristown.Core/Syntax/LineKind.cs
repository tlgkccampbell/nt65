namespace Norristown.Syntax;

/// <summary>
/// A line's kind, from its first one or two tokens. What a kind means inside a particular
/// block (an enum member, a list item, a splice) is the parser's business.
/// </summary>
public enum LineKind
{
    /// <summary>No tokens: whitespace and comments only.</summary>
    Blank,
    /// <summary><c>.word</c>, or any other directive, including block openers.</summary>
    Directive,
    /// <summary><c>name :</c>, optionally followed by a statement.</summary>
    Label,
    /// <summary><c>name = expr</c>.</summary>
    Constant,
    /// <summary><c>name ! (...)</c>.</summary>
    MacroCall,
    /// <summary>An identifier alone.</summary>
    BareIdentifier,
    /// <summary>A mnemonic.</summary>
    Instruction,
    /// <summary>Anything else; list items inside <c>.list</c>, an error elsewhere.</summary>
    Expression,
    /// <summary><c>}</c>, optionally continuing with <c>.else {</c>, <c>.elseif expr {</c> or <c>name {</c>.</summary>
    BlockClose,
}
