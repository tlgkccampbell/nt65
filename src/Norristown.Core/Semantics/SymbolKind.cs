namespace Norristown.Semantics;

/// <summary>What a declaration declares (§6.1).</summary>
public enum SymbolKind
{
    /// <summary>A label, <c>name:</c>: an address in the segment it was written in.</summary>
    Label,

    /// <summary><c>NAME = expr</c> where the expression names no address.</summary>
    Constant,

    /// <summary><c>NAME = expr</c> where it does: sized, exported and imported like a label.</summary>
    AddressAlias,

    /// <summary><c>.proc name { }</c>: a label and a scope.</summary>
    Proc,

    /// <summary><c>.proc name = expr</c>: a routine with a signature and no body.</summary>
    ExternProc,

    /// <summary><c>.scope name { }</c>: a scope and nothing else.</summary>
    Scope,

    /// <summary><c>.import name</c> or <c>.import name: size</c>: an address from another module.</summary>
    ImportedAddress,

    /// <summary><c>.import NAME = expr</c>: a checked import, whose value nt65 uses (§12).</summary>
    ImportedConstant,
}
