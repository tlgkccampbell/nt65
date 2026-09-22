namespace Norristown;

/// <summary>What a <see cref="DiagnosticFix"/> changes.</summary>
public enum FixKind
{
    /// <summary>A <c>.next ?</c> after the statement reported, which ends the path there.</summary>
    EndPath,

    /// <summary>The statement's mnemonic written as another: <c>jsl</c> for <c>jsr</c>, or the other way.</summary>
    Mnemonic,

    /// <summary>An <c>.export</c> of the name, in the module that declares it.</summary>
    Export,

    /// <summary>A <c>.use</c> of the path, in the file reported.</summary>
    Use,

    /// <summary>A <c>.state</c> after the label, saying what reaches it.</summary>
    State,

    /// <summary>The label reported, and the data under it, as a <c>.data</c> declaration.</summary>
    DataDeclaration,

    /// <summary>The branch reported, written as the long branch that reaches any near target.</summary>
    Branch,

    /// <summary>The return reported, written as the one its routine is left by.</summary>
    Return,

    /// <summary>What ca65 spelled it, written the way nt65 spells it.</summary>
    Spelling,

    /// <summary>The name reported, written as the declared name it is nearly.</summary>
    NearestName,

    /// <summary>
    /// The name reported, for the programmer to give another: the editor puts the caret on it
    /// and starts a rename, since nothing but the programmer knows what it should be called.
    /// </summary>
    Rename,

    /// <summary>ca65's assertion level, dropped: an assertion that fails here is always an error.</summary>
    AssertLevel,

    /// <summary>The <c>.res</c> of a declaration, written as the storage it reserves.</summary>
    Storage,

    /// <summary>The label in mixed data, written as a member of it or as a position in it.</summary>
    DataMember,

    /// <summary>The address size an <c>.export</c> gives, widened to the one the declaration has.</summary>
    ExportSize,

    /// <summary>The expression reported, written with parentheses: one fix per reading of it.</summary>
    Parentheses,

    /// <summary>An <c>.ensure</c> before the immediate reported: one fix per width it may be.</summary>
    Width,

    /// <summary>The width item written into the signature of the routine the immediate is in.</summary>
    Signature,

    /// <summary>The declaration nothing names, removed, or exported so that another module may name it.</summary>
    Unused,

    /// <summary>The <c>.use</c> item that brings in a name nothing writes, removed.</summary>
    UseItem,

    /// <summary>
    /// The bracket the line does not have, written where the tree holds the place for it: the
    /// <c>{</c> a block needs, or the <c>)</c>, <c>]</c> or <c>}</c> that closes what is open.
    /// </summary>
    MissingPiece,

    /// <summary>The declaration of the module a <c>.place</c> names, marked <c>placed</c>.</summary>
    Placed,

    /// <summary>
    /// A <c>.fallthrough</c> naming the routine written next, as the last line of the body the
    /// fix's <see cref="DiagnosticFix.At"/> closes.
    /// </summary>
    Fallthrough,
}
