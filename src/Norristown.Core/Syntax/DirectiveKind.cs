namespace Norristown.Syntax;

/// <summary>
/// Specifies the directive that a directive token names when it begins a statement. The lexer
/// reads every such directive, regardless of case, as one of these kinds, so later passes compare
/// kinds rather than spellings. Each member's name, in lower case and after a <c>.</c>, is its
/// spelling in source.
/// <para>
/// The built-in functions, such as <c>.lobyte</c>, and the <c>.mod</c> and <c>.in</c> operators
/// are spelled like directives but are not among these kinds, because they appear inside
/// expressions rather than beginning a statement.
/// </para>
/// </summary>
public enum DirectiveKind
{
    /// <summary>No directive; the kind of every token that is not one of the directives below.</summary>
    None,

    /// <summary><c>.cpu</c>: sets the processor the program is built for.</summary>
    Cpu,

    /// <summary><c>.segment</c>: declares a segment, or places what follows in one.</summary>
    Segment,

    /// <summary><c>.data</c>: declares data.</summary>
    Data,

    /// <summary><c>.proc</c>: declares a routine.</summary>
    Proc,

    /// <summary><c>.multiproc</c>: declares one routine per member of an enum.</summary>
    MultiProc,

    /// <summary><c>.scope</c>: declares a scope of names.</summary>
    Scope,

    /// <summary><c>.export</c>: makes a name visible to other modules.</summary>
    Export,

    /// <summary><c>.import</c>: declares a name from outside the program.</summary>
    Import,

    /// <summary><c>.module</c>: names the module a file is part of.</summary>
    Module,

    /// <summary><c>.place</c>: emits another module's code here.</summary>
    Place,

    /// <summary><c>.use</c>: brings another module's names in.</summary>
    Use,

    /// <summary><c>.byte</c>: one-byte values.</summary>
    Byte,

    /// <summary><c>.word</c>: two-byte values.</summary>
    Word,

    /// <summary><c>.long</c>: three-byte values.</summary>
    Long,

    /// <summary><c>.dword</c>: four-byte values.</summary>
    Dword,

    /// <summary><c>.beword</c>: big-endian two-byte values.</summary>
    BeWord,

    /// <summary><c>.belong</c>: big-endian three-byte values.</summary>
    BeLong,

    /// <summary><c>.bedword</c>: big-endian four-byte values.</summary>
    BeDword,

    /// <summary><c>.addr</c>: two-byte addresses.</summary>
    Addr,

    /// <summary><c>.faraddr</c>: three-byte addresses.</summary>
    FarAddr,

    /// <summary><c>.res</c>: reserves bytes.</summary>
    Res,

    /// <summary><c>.strz</c>: text with a terminating zero.</summary>
    Strz,

    /// <summary><c>.type</c>: values of a declared type.</summary>
    Type,

    /// <summary><c>.align</c>: pads to a multiple of a boundary.</summary>
    Align,

    /// <summary><c>.incbin</c>: the bytes of a file.</summary>
    IncBin,

    /// <summary><c>.lobytes</c>: the low byte of each value.</summary>
    LoBytes,

    /// <summary><c>.hibytes</c>: the high byte of each value.</summary>
    HiBytes,

    /// <summary><c>.bankbytes</c>: the bank byte of each value.</summary>
    BankBytes,

    /// <summary><c>.enum</c>: declares a set of named constants.</summary>
    Enum,

    /// <summary><c>.struct</c>: declares a structure.</summary>
    Struct,

    /// <summary><c>.union</c>: declares a union.</summary>
    Union,

    /// <summary><c>.charmap</c>: declares what each character assembles to.</summary>
    Charmap,

    /// <summary><c>.list</c>: declares a list of values to repeat over.</summary>
    List,

    /// <summary><c>.func</c>: declares a function of its arguments.</summary>
    Func,

    /// <summary><c>.signature</c>: declares a named set of processor-state items.</summary>
    Signature,

    /// <summary><c>.config</c>: declares a setting the build supplies.</summary>
    Config,

    /// <summary><c>.if</c>: assembles a block when a condition holds.</summary>
    If,

    /// <summary><c>.elseif</c>: continues an <c>.if</c> with another condition.</summary>
    ElseIf,

    /// <summary><c>.else</c>: continues an <c>.if</c> with what to assemble instead.</summary>
    Else,

    /// <summary><c>.repeat</c>: assembles a block a number of times.</summary>
    Repeat,

    /// <summary><c>.each</c>: assembles a block once for each item.</summary>
    Each,

    /// <summary><c>.assert</c>: checks that something holds.</summary>
    Assert,

    /// <summary><c>.error</c>: fails the build with a message.</summary>
    Error,

    /// <summary><c>.warning</c>: reports a message.</summary>
    Warning,

    /// <summary><c>.macro</c>: declares a macro.</summary>
    Macro,

    /// <summary><c>.next</c>: states where control goes after the statement above.</summary>
    Next,

    /// <summary><c>.fallthrough</c>: states the routine this one runs into.</summary>
    Fallthrough,

    /// <summary><c>.patch</c>: states the target the instruction above is patched to.</summary>
    Patch,

    /// <summary><c>.state</c>: states what the processor state is here.</summary>
    State,

    /// <summary><c>.ensure</c>: states the widths the code from here needs.</summary>
    Ensure,

    /// <summary><c>.frame</c>: states the stack frame the code from here works in.</summary>
    Frame,
}
