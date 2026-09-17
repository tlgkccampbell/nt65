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
}
