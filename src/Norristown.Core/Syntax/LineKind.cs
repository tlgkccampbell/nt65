namespace Norristown.Syntax;

/// <summary>
/// Specifies a line's kind, which is determined by its first one or two tokens. The parser
/// decides what a kind means inside a particular block, such as an enum member, a list item or a
/// splice.
/// </summary>
public enum LineKind
{
    /// <summary>A line with no tokens, only whitespace and comments.</summary>
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
    /// <summary>Any other line; a list item inside <c>.list</c>, and an error elsewhere.</summary>
    Expression,
    /// <summary><c>}</c>, optionally continuing with <c>.else {</c>, <c>.elseif expr {</c> or <c>name {</c>.</summary>
    BlockClose,
}
