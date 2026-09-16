namespace Norristown.Semantics;

/// <summary>What a declaration declares.</summary>
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

    /// <summary><c>.import NAME = expr</c>: a checked import, whose value nt65 uses.</summary>
    ImportedConstant,

    /// <summary><c>.enum Name { }</c>: a scope of constants.</summary>
    Enum,

    /// <summary><c>.struct Name { }</c>: a layout, whose members are offsets.</summary>
    Struct,

    /// <summary><c>.union Name { }</c>: a layout whose members all sit at offset zero.</summary>
    Union,

    /// <summary>One member of a struct or union: a constant offset with a size of its own.</summary>
    Member,

    /// <summary>A label declared with <c>.tag</c>: an address, and a scope of its fields.</summary>
    Instance,

    /// <summary><c>.charmap Name { }</c>: a mapping from characters to bytes.</summary>
    Charmap,

    /// <summary><c>.list Name { }</c>: a named sequence of expressions.</summary>
    List,

    /// <summary><c>.func name(a, b) = expr</c>: a pure expression function.</summary>
    Func,

    /// <summary>The name a <c>.repeat</c> or an <c>.each</c> binds: one value per turn.</summary>
    Binding,

    /// <summary><c>.macro name(...) { }</c>: a body expanded at each of its calls.</summary>
    Macro,

    /// <summary>One parameter of a macro: the name its body gives an argument.</summary>
    MacroParameter,
}
