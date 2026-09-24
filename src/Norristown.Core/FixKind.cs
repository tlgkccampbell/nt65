namespace Norristown;

/// <summary>Identifies what a <see cref="DiagnosticFix"/> changes.</summary>
public enum FixKind
{
    /// <summary>A <c>.next ?</c> after the statement reported, which ends the path there.</summary>
    EndPath,

    /// <summary>
    /// The statement's mnemonic replaced with another, either <c>jsl</c> for <c>jsr</c> or the
    /// reverse.
    /// </summary>
    Mnemonic,

    /// <summary>An <c>.export</c> of the name, in the module that declares it.</summary>
    Export,

    /// <summary>A <c>.use</c> of the path, in the file reported.</summary>
    Use,

    /// <summary>A <c>.state</c> after the label, saying what reaches it.</summary>
    State,

    /// <summary>The label reported, and the data under it, as a <c>.data</c> declaration.</summary>
    DataDeclaration,

    /// <summary>The branch reported, replaced with the long branch that reaches any near target.</summary>
    Branch,

    /// <summary>
    /// The return reported, replaced with the instruction its routine must return with, such as
    /// <c>rti</c> in an interrupt handler.
    /// </summary>
    Return,

    /// <summary>A construct in ca65's syntax, rewritten in nt65's syntax.</summary>
    Spelling,

    /// <summary>The name reported, replaced with the declared name it most nearly matches.</summary>
    NearestName,

    /// <summary>
    /// The name reported, for the programmer to rename. The editor puts the caret on the name and
    /// starts a rename, since only the programmer knows what it should be called.
    /// </summary>
    Rename,

    /// <summary>
    /// ca65's assertion level, removed because an assertion that fails in nt65 is always an error.
    /// </summary>
    AssertLevel,

    /// <summary>The <c>.res</c> of a declaration, replaced with the storage it reserves.</summary>
    Storage,

    /// <summary>The label in mixed data, replaced with a member of the data or a position in it.</summary>
    DataMember,

    /// <summary>The address size an <c>.export</c> gives, widened to the one the declaration has.</summary>
    ExportSize,

    /// <summary>
    /// The expression reported, with parentheses added. There is one fix for each way the
    /// expression can be read.
    /// </summary>
    Parentheses,

    /// <summary>
    /// An <c>.ensure</c> before the immediate reported. There is one fix for each width the
    /// immediate may have.
    /// </summary>
    Width,

    /// <summary>The width item added to the signature of the routine that contains the immediate.</summary>
    Signature,

    /// <summary>The declaration nothing names, removed, or exported so that another module may name it.</summary>
    Unused,

    /// <summary>The <c>.use</c> item that imports a name nothing refers to, removed.</summary>
    UseItem,

    /// <summary>
    /// The bracket missing from the line, inserted where the syntax tree has a missing token for
    /// it. The bracket is either the <c>{</c> a block needs, or the <c>)</c>, <c>]</c> or
    /// <c>}</c> that closes an open bracket.
    /// </summary>
    MissingPiece,

    /// <summary>The declaration of the module a <c>.place</c> names, marked <c>placed</c>.</summary>
    Placed,

    /// <summary>
    /// A <c>.fallthrough</c> naming the routine that comes next, as the last line of the body the
    /// fix's <see cref="DiagnosticFix.At"/> closes.
    /// </summary>
    Fallthrough,

    /// <summary>
    /// A <c>.next</c> naming the branch's own target after the conditional branch at the fix's
    /// <see cref="DiagnosticFix.At"/>, stating that the branch is always taken.
    /// </summary>
    AlwaysTaken,
}
