namespace Norristown.Semantics;

/// <summary>
/// Specifies what, at the end of a chain of declarations, the configuration alone does not
/// decide. Each cause has its own diagnostic, because each has its own fix.
/// </summary>
internal enum UndecidedCause
{
    /// <summary>A measurement of a declaration, such as <c>.sizeof</c>, known only once the declarations are read.</summary>
    Measurement,

    /// <summary>A constant declared under an <c>.if</c>, which exists only where that branch is taken.</summary>
    Conditional,

    /// <summary>A constant declared inside a block, which is not named where the conditions are answered.</summary>
    InBlock,

    /// <summary>An address, or a declaration that is not a value, such as a routine, data, a macro or a type.</summary>
    Place,

    /// <summary>A name that nothing declares.</summary>
    Unknown,
}
