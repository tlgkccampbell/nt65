namespace Norristown.Syntax;

/// <summary>
/// Specifies the built-in function that a call names. Later passes compare kinds rather than
/// spellings. Each member's name, in lower case after a dot, is its spelling in source, such as
/// <c>.sizeof</c> for <see cref="Sizeof"/>. <see cref="SyntaxFacts.Builtins"/> holds what each
/// one allows.
/// </summary>
public enum BuiltinKind
{
    /// <summary>No built-in function; the kind of a call to a <c>.func</c> or a charmap.</summary>
    None,

    /// <summary><c>.lobyte(v)</c>: the low byte of a value.</summary>
    Lobyte,

    /// <summary><c>.hibyte(v)</c>: the second byte of a value.</summary>
    Hibyte,

    /// <summary><c>.bankbyte(v)</c>: the third byte of a value.</summary>
    Bankbyte,

    /// <summary><c>.loword(v)</c>: the low 16 bits of a value.</summary>
    Loword,

    /// <summary><c>.hiword(v)</c>: bits 16 to 31 of a value.</summary>
    Hiword,

    /// <summary><c>.sizeof(name)</c>: the number of bytes a declaration takes.</summary>
    Sizeof,

    /// <summary><c>.countof(name)</c>: the number of elements a declaration holds.</summary>
    Countof,

    /// <summary><c>.endof(name)</c>: the address just past a routine or a data declaration.</summary>
    Endof,

    /// <summary><c>.spanof(name)</c>: the number of bytes a routine, data or segment takes once laid out.</summary>
    Spanof,

    /// <summary><c>.loadof(segment)</c>: the address a segment is loaded at.</summary>
    Loadof,

    /// <summary><c>.runof(segment)</c>: the address a segment runs at.</summary>
    Runof,

    /// <summary><c>.strlen(text)</c>: the length of a text.</summary>
    Strlen,

    /// <summary><c>.strat(text, i)</c>: one character of a text.</summary>
    Strat,

    /// <summary><c>.strsub(text, start, count)</c>: part of a text.</summary>
    Strsub,

    /// <summary><c>.strcat(part, ...)</c>: texts and bytes joined.</summary>
    Strcat,

    /// <summary><c>.min(a, b)</c>: the smaller of two numbers.</summary>
    Min,

    /// <summary><c>.max(a, b)</c>: the larger of two numbers.</summary>
    Max,

    /// <summary><c>.addrsize(e)</c>: the address size of an expression.</summary>
    Addrsize,

    /// <summary><c>.target(cpu)</c>: whether the build is for a CPU.</summary>
    Target,

    /// <summary><c>.defined(name)</c>: whether a name is a define.</summary>
    Defined,

    /// <summary><c>.has(mnemonic)</c>: whether the build's CPU has an instruction.</summary>
    Has,

    /// <summary><c>.select(c, a, b)</c>: one of two values, chosen by a constant.</summary>
    Select,

    /// <summary>
    /// <c>.switch(v, [set], value, ..., otherwise)</c>: the value of the first arm whose set holds
    /// a constant.
    /// </summary>
    Switch,

    /// <summary><c>.sqrt(n)</c>: a whole square root.</summary>
    Sqrt,

    /// <summary><c>.muldiv(a, b, c)</c>: a scaled product.</summary>
    Muldiv,

    /// <summary><c>.sin(angle, turn, scale)</c>: a scaled sine.</summary>
    Sin,

    /// <summary><c>.cos(angle, turn, scale)</c>: a scaled cosine.</summary>
    Cos,

    /// <summary><c>.mincycles(from, to)</c>: the fewest cycles a span of code takes.</summary>
    Mincycles,

    /// <summary><c>.maxcycles(from, to)</c>: the most cycles a span of code takes.</summary>
    Maxcycles,

    // The built-in functions only a macro body may call.
    /// <summary><c>.mode(p)</c>: the addressing mode of an operand argument.</summary>
    Mode,

    /// <summary><c>.byteof(p, n)</c>: byte n of an operand argument.</summary>
    Byteof,

    /// <summary><c>.exprof(p)</c>: the expression inside an operand argument.</summary>
    Exprof,

    /// <summary><c>.empty(p)</c>: whether a block argument is empty.</summary>
    Empty,
}
