using System.Reflection;

namespace Norristown;

/// <summary>
/// Every diagnostic nt65 reports, by name. A name says what is wrong rather than which
/// pass found it, is kebab-case, and is stable once released: it is what a project file
/// switches, what the editor shows beside the message, and what CI matches on.
/// <para>
/// A reporting site names one of these and supplies the arguments for its message. The
/// explanation is separate from the message: it holds what a one-line message has no room
/// for, and is what <c>nt65 explain</c> prints.
/// </para>
/// </summary>
public static class Catalogue
{
    // The area most recently opened. Each entry takes the area whose heading is written above
    // it, rather than naming its area, which would add a line to every entry. This works
    // because static initializers run in the order they appear in the file, so each heading is
    // set before the entries below it are created.
    private static DiagnosticArea? opening;

    /// <summary>Every area, in the order they are written here and printed.</summary>
    public static IReadOnlyList<DiagnosticArea> Areas =>
    [
        ReadingALine, Names, Values, Macros, Data, Placement,
        Instructions, ControlFlow, ProcessorState, Output, TheProjectFile, Signatures,
    ];

    private static DiagnosticArea ReadingALine { get; } =
        Opens("Reading a line", "Syntax: numbers, text, braces, and what may be written where on a line.");

    internal static DiagnosticDescriptor NumberInvalid { get; } = Entry(
        "number-invalid",
        Severity.Error,
        "invalid {0} number `{1}`",
        "A number is read to the end of the word, so every character in it must be a digit of its base: `0`-`9` "
            + "for decimal, `0`-`9` and `a`-`f` after `$`, and `0` and `1` after `%`, with `_` allowed between "
            + "digits. A character outside the base makes the whole number invalid rather than starting something "
            + "new. Check for a typo or a missing `$`.");

    internal static DiagnosticDescriptor NumberSeparator { get; } = Entry(
        "number-separator",
        Severity.Error,
        "`{0}` has a misplaced `_`: a digit separator needs a digit on each side",
        "A `_` may group the digits of a number in any base, as in `$7f_ff`, `%1010_1010` or `1_000`. It must have "
            + "a digit on each side, so it cannot start or end the number or stand next to another `_`.");

    internal static DiagnosticDescriptor DigitsMissing { get; } = Entry(
        "digits-missing",
        Severity.Error,
        "expected {0} digits after `{1}`{2}",
        "`$` starts a hexadecimal number and `%` a binary one, so digits must follow directly, as in `$ff` or "
            + "`%1010`. In nt65 `%` is only a binary prefix; the remainder operator is `.mod`.");

    internal static DiagnosticDescriptor NameAfterAt { get; } = Entry(
        "name-after-at",
        Severity.Error,
        "expected a name after `@`",
        "A `@` starts a cheap local, a label private to the routine around it, and the name must follow it "
            + "directly, as in `@loop`.");

    internal static DiagnosticDescriptor StrayDot { get; } = Entry(
        "stray-dot",
        Severity.Error,
        "unexpected `.`",
        "A `.` begins a directive or a built-in function, and a `..` a range. On its own it is neither.");

    internal static DiagnosticDescriptor UnexpectedCharacter { get; } = Entry(
        "unexpected-character",
        Severity.Error,
        "unexpected character `{0}`",
        "This character cannot start anything in nt65. Any character may appear inside a string, a character "
            + "literal or a comment, but nowhere else.");

    internal static DiagnosticDescriptor EscapeHexDigits { get; } = Entry(
        "escape-hex-digits",
        Severity.Error,
        "`\\x` must be followed by two hexadecimal digits",
        "A `\\xHH` escape gives one byte as exactly two hexadecimal digits, as in `\\x0d` or `\\xff`. Write a "
            + "leading zero for a value below `$10`.");

    internal static DiagnosticDescriptor EscapeUnknown { get; } = Entry(
        "escape-unknown",
        Severity.Error,
        "unknown escape `\\{0}`",
        "The escapes are `\\n`, `\\r`, `\\t`, `\\0`, `\\\\`, `\\\"`, `\\'` and `\\xHH`, the same in a character "
            + "literal and in a string. To write a backslash itself, use `\\\\`. Each unknown escape is reported "
            + "separately, since each needs its own correction.");

    internal static DiagnosticDescriptor TextUnterminated { get; } = Entry(
        "text-unterminated",
        Severity.Error,
        "unterminated {0}",
        "A string or character literal must close on the line where it opens; nt65 has no literals that span "
            + "lines. Add the closing quote.");

    internal static DiagnosticDescriptor CharacterEmpty { get; } = Entry(
        "character-empty",
        Severity.Error,
        "empty character literal",
        "A character literal is one character, which is one value. Text is written in double quotes.");

    internal static DiagnosticDescriptor CharacterTooLong { get; } = Entry(
        "character-too-long",
        Severity.Error,
        "a character literal holds exactly one character",
        "A character literal is one value. Several characters are text, written in double quotes, and a data "
            + "declaration writes their bytes.");

    internal static DiagnosticDescriptor BlockNotClosed { get; } = Entry(
        "block-not-closed",
        Severity.Error,
        "missing `}}` to close this block",
        "Blocks are read from the braces before anything else in a file is read, so an unclosed one is reported "
            + "at the line that opens it rather than at the end of the file.");

    internal static DiagnosticDescriptor UnmatchedBrace { get; } = Entry(
        "unmatched-brace",
        Severity.Error,
        "unmatched `}}`",
        "A `}` closes a block, and there is no open one here.");

    internal static DiagnosticDescriptor UnexpectedToken { get; } = Entry(
        "unexpected-token",
        Severity.Error,
        "unexpected {0}",
        "The statement was already complete before this token, so nt65 cannot tell what the rest of the line is "
            + "for. Common causes are a missing comma or operator, or a comment without its `;`. Only the first "
            + "unexpected token on a line is reported.");

    internal static DiagnosticDescriptor BlockBraceEndsTheLine { get; } = Entry(
        "block-brace-ends-the-line",
        Severity.Error,
        "a block's `{{` must end its line: move {0} and the rest to the next line, and put `}}` on a line of its own",
        "A block always spans several lines: its opening `{` is the last thing on its line, its contents follow on "
            + "lines of their own, and its closing `}` stands alone on its line. This keeps the structure of a "
            + "file readable from its indentation and braces alone.");

    internal static DiagnosticDescriptor Ca65Spelling { get; } = Entry(
        "ca65-spelling",
        Severity.Error,
        "ca65's `{0}` is `{1}` in nt65",
        "nt65 has this directive under a different name. The fix replaces it with the nt65 spelling.");

    internal static DiagnosticDescriptor Ca65Tag { get; } = Entry(
        "ca65-tag",
        Severity.Error,
        "`.tag T` is written `.type T`, and `.tag T, n` is `.type T[n]`",
        "A record type is written the same way wherever it is used, and a count is written in brackets after it, "
            + "as every other count is.");

    internal static DiagnosticDescriptor Ca65BlockEnd { get; } = Entry(
        "ca65-block-end",
        Severity.Error,
        "nt65 closes blocks with `}}`, not `{0}`",
        "nt65 closes every block with `}` rather than with an end directive such as `.endproc`, `.endscope` or "
            + "`.endif`. Replace the directive with `}`; the fix does this.");

    internal static DiagnosticDescriptor DataNeedsAName { get; } = Entry(
        "data-needs-a-name",
        Severity.Error,
        "`.data` declares data, and needs a name: the segment is written `.segment DATA`",
        "`.data` is a declaration, and a declaration has a name. The segment called DATA is named on a "
            + "`.segment` line.");

    internal static DiagnosticDescriptor DataValuesNeedBraces { get; } = Entry(
        "data-values-need-braces",
        Severity.Error,
        "{0}",
        "The values of an array (a declaration with a count, `[n]`) or of a record (`.type T`) are written in "
            + "braces, as in `{ 1, 2 }`, so that where the values end is written down rather than worked out from "
            + "the count.");

    internal static DiagnosticDescriptor ExpectedName { get; } = Entry(
        "expected-name",
        Severity.Error,
        "expected {0}",
        "A declaration, a parameter, a path or a `.use` item needs a name at this point, and the line does not "
            + "have one. The message says which name is expected; add it. nt65 never makes up a name, so nothing "
            + "after the missing one on the line is read.");

    internal static DiagnosticDescriptor ExpectedStatement { get; } = Entry(
        "expected-statement",
        Severity.Error,
        "expected {0}",
        "A line must be a label, a constant, an instruction, a directive or a macro call, and this one starts as "
            + "none of them. After a label, the rest of the line may only be an instruction, a data directive or a "
            + "macro call.");

    internal static DiagnosticDescriptor ExpectedExpression { get; } = Entry(
        "expected-expression",
        Severity.Error,
        "expected an expression",
        "An expression belongs here and the line has something that starts none: a stray operator, a closing "
            + "bracket, or nothing at all.");

    internal static DiagnosticDescriptor ExpectedElementIndex { get; } = Entry(
        "expected-element-index",
        Severity.Error,
        "expected an index between the brackets: `name[i]` is element `i` of `name`",
        "The brackets after a name pick one element of what it declares, and the brackets here are empty. A count "
            + "is written on the declaration; an index is written on a use of it.");

    internal static DiagnosticDescriptor ExpectedParenthesis { get; } = Entry(
        "expected-parenthesis",
        Severity.Error,
        "expected {0}",
        "A parameter list, an argument list or a parenthesised expression is unbalanced or unopened.");

    internal static DiagnosticDescriptor ExpectedBrace { get; } = Entry(
        "expected-brace",
        Severity.Error,
        "expected {0}",
        "A block opens with a `{` at the end of its first line and closes with a `}` on a line of its own; a "
            + "braced value such as `{ 1, 2 }` opens and closes on one line. One of the two braces is missing here.");

    internal static DiagnosticDescriptor ExpectedBracket { get; } = Entry(
        "expected-bracket",
        Severity.Error,
        "expected {0}",
        "A count, an index, a long indirect operand or a list of banks is written in brackets, and one of them is "
            + "not closed or not opened.");

    internal static DiagnosticDescriptor ExpectedDotDot { get; } = Entry(
        "expected-dot-dot",
        Severity.Error,
        "expected {0}",
        "A range is written from its lower end to its higher, with `..` between them.");

    internal static DiagnosticDescriptor ExpectedEquals { get; } = Entry(
        "expected-equals",
        Severity.Error,
        "expected {0}",
        "This construct gives its value after an `=`: a record member's value, a `.charmap` entry, a segment "
            + "attribute, a `.config` setting, a signature set's items or a `.func` body. The `=` is missing here; "
            + "the message shows the expected form.");

    internal static DiagnosticDescriptor ExpectedColon { get; } = Entry(
        "expected-colon",
        Severity.Error,
        "expected {0}",
        "A `:` separates a declaration from what it is: a segment from its address size, data from its element "
            + "type, a frame from the record it is laid out as.");

    internal static DiagnosticDescriptor ExpectedComma { get; } = Entry(
        "expected-comma",
        Severity.Error,
        "expected {0}",
        "A `.multiproc` names the enum it walks and then the name each routine is named from, with a comma "
            + "between them.");

    internal static DiagnosticDescriptor ExpectedText { get; } = Entry(
        "expected-text",
        Severity.Error,
        "expected {0}",
        "A message, or a linker name, is written in double quotes. nt65 has no bare-word text.");

    internal static DiagnosticDescriptor ExpectedDataType { get; } = Entry(
        "expected-data-type",
        Severity.Error,
        "expected {0}",
        "A `.data` declaration or a typed import says what its bytes are: a number type such as `.byte` or "
            + "`.word`, an address type such as `.addr`, or a record type, `.type T`; a `.data` declaration may "
            + "also include a file with `.incbin`. What is written here is none of those.");

    internal static DiagnosticDescriptor ExpectedMemberValue { get; } = Entry(
        "expected-member-value",
        Severity.Error,
        "expected `member = value`",
        "Each line of a multi-line record initializer gives one member a value.");

    internal static DiagnosticDescriptor ExpectedCpu { get; } = Entry(
        "expected-cpu",
        Severity.Error,
        "expected {0}",
        "`.cpu` names one of the processors nt65 knows, written as the project file writes it.");

    internal static DiagnosticDescriptor ExpectedAddressSize { get; } = Entry(
        "expected-address-size",
        Severity.Error,
        "expected {0}",
        "An address size is one of `zp` (one byte), `abs` (two bytes) or `far` (three bytes), and has no other "
            + "spellings. An import may give a routine signature, `proc(...)`, or an element type such as `.byte`, "
            + "in place of a size.");

    internal static DiagnosticDescriptor ImportNeedsAnElementType { get; } = Entry(
        "import-needs-an-element-type",
        Severity.Error,
        "`{0}` is not an element type: an import says what its bytes are as `.byte`, `.word`, `.addr` or `.type T`",
        "A typed import describes storage another object holds, so it writes an element type and a count. A "
            + "directive that reads a file or writes text describes bytes this program would emit, and an import "
            + "emits none.");

    internal static DiagnosticDescriptor ImportHoldsNoValues { get; } = Entry(
        "import-holds-no-values",
        Severity.Error,
        "an import describes its `{0}` data but cannot give it values: the bytes are defined in another object file",
        "An import declares what a symbol another object defines looks like, so that nt65 can size it and reach "
            + "its members. The bytes themselves belong to whoever defines it.");

    internal static DiagnosticDescriptor ExpectedSegmentAttribute { get; } = Entry(
        "expected-segment-attribute",
        Severity.Error,
        "expected {0}",
        "After its address size, a segment declaration may give only these attributes: `dp`, the direct page it is "
            + "reached through; `bank`, the bank it is in; `mirrors`, the banks it is mirrored in; and `space`, "
            + "the address space it is in.");

    internal static DiagnosticDescriptor ExpectedPlacement { get; } = Entry(
        "expected-placement",
        Severity.Error,
        "expected {0}",
        "A module's declaration may say whether another module places it, with `placed` or `placeable` after a "
            + "`:`, and there is nothing else it may say there.");

    internal static DiagnosticDescriptor ExpectedParameterKind { get; } = Entry(
        "expected-parameter-kind",
        Severity.Error,
        "expected {0}",
        "A macro parameter may say what kind of argument it takes, with one of a fixed set of words: `expr`, "
            + "`const`, `ident`, `operand`, `one(...)`, `list(...)` or `block`, or the name of an enum. A "
            + "parameter that says nothing takes an expression.");

    internal static DiagnosticDescriptor ExpectedStateItem { get; } = Entry(
        "expected-state-item",
        Severity.Error,
        "expected {0}",
        "A signature, a `.state` and an `.ensure` are made of processor-state items, and this is not one.");

    internal static DiagnosticDescriptor ExpectedKeptRegisters { get; } = Entry(
        "expected-kept-registers",
        Severity.Error,
        "expected {0}",
        "`keeps` lists the registers a routine returns with the same values they had on entry, for example `keeps "
            + "x, y`. It must list at least one register.");

    internal static DiagnosticDescriptor ExpectedLabel { get; } = Entry(
        "expected-label",
        Severity.Error,
        "expected {0}",
        "`.next` names where execution continues, `.fallthrough` the routine that execution runs into, and "
            + "`.patch` the instruction whose bytes are overwritten. Each takes a label; `.next` also takes `?`, "
            + "which ends the path so that nothing beyond it is checked.");

    internal static DiagnosticDescriptor NestingTooDeep { get; } = Entry(
        "nesting-too-deep",
        Severity.Error,
        "expression nested more than {0} levels deep: nt65 reads no further",
        "nt65 stops reading an expression nested deeper than this limit. The limit is far beyond anything written "
            + "by hand; it is there so that a half-typed line of brackets cannot overflow the stack and crash the "
            + "assembler or the editor's language server.");

    internal static DiagnosticDescriptor UnnamedLabel { get; } = Entry(
        "unnamed-label",
        Severity.Error,
        "unnamed labels (`:`, `:+`, `:-`) are not supported: use a cheap local instead, `@name:`",
        "ca65's unnamed labels are found by counting, so `:+` means the next `:`, and adding or removing one "
            + "changes what the others refer to. nt65 has no unnamed labels. Use a cheap local, such as `@loop:`, "
            + "and branch to `@loop`; it is private to the routine around it and costs nothing more in the output.");

    internal static DiagnosticDescriptor DirectiveUnknown { get; } = Entry(
        "directive-unknown",
        Severity.Error,
        "unknown directive `{0}`",
        "The word begins with a `.` and is no directive nt65 has. Directives are a fixed set: nothing declares "
            + "one.");

    internal static DiagnosticDescriptor DirectiveAfterLabel { get; } = Entry(
        "directive-after-label",
        Severity.Error,
        "`{0}` may not follow a label",
        "A label is a position in code, so what may follow it on the line is what takes bytes there: an "
            + "instruction, a data directive or a macro call.");

    internal static DiagnosticDescriptor ElseIfMisplaced { get; } = Entry(
        "elseif-misplaced",
        Severity.Error,
        "`{0}` must follow the `}}` that closes the previous branch, on the same line",
        "Each branch of an `.if` is continued on the line that closes the one before it, `} .elseif condition {` "
            + "or `} .else {`, so that the shape of the block is readable without matching braces by eye.");

    internal static DiagnosticDescriptor AssertLevel { get; } = Entry(
        "assert-level",
        Severity.Error,
        "nt65's `.assert` takes no level: remove `{0}`, since a failed assertion is always an error",
        "ca65's `.assert` takes a level such as `warning` or `error`, because ca65 cannot always decide the "
            + "condition itself. nt65 always reports a failed assertion as an error, checking it as soon as the "
            + "value is known and otherwise leaving it for the linker. Write `.assert condition, \"message\"`; the "
            + "fix removes the level.");

    internal static DiagnosticDescriptor SegmentNameQuoted { get; } = Entry(
        "segment-name-quoted",
        Severity.Error,
        "a segment name is written without quotes: `.segment {0}`",
        "Segments are a table of their own and share no namespace with symbols, so a segment name is written as a "
            + "word. The quotes are ca65's habit.");

    internal static DiagnosticDescriptor ModuleNameQuoted { get; } = Entry(
        "module-name-quoted",
        Severity.Error,
        "a module name is written without quotes: `.module hw::vic`",
        "A module name is a path of plain names, such as `hw::vic`, written without quotes.");

    internal static DiagnosticDescriptor ExportDeclaresNothing { get; } = Entry(
        "export-declares-nothing",
        Severity.Error,
        "`.export` goes before a declaration, and `{0}` declares nothing to export",
        "`.export` before a declaration exports what that declaration declares, so the directive after it has to "
            + "be one that declares something.");

    internal static DiagnosticDescriptor DataBodyHoldsValues { get; } = Entry(
        "data-body-holds-values",
        Severity.Error,
        "`{0}` cannot appear in an array body, which holds only values: the element type goes on the declaration "
            + "line",
        "The lines between an array's braces hold only its values, separated by commas. The element type, such as "
            + "`.byte` or `.word`, is given once on the declaration line, so a directive in the body would give it "
            + "a second time. Remove the directive and keep the values.");

    internal static DiagnosticDescriptor DataBodyNeedsACount { get; } = Entry(
        "data-body-needs-a-count",
        Severity.Error,
        "values in a body need a count: `{0}[] {{` counts them",
        "A body of values belongs to an array, and an array says how many elements it holds. `[]` counts the "
            + "values given, which is what a body without a count usually meant.");

    internal static DiagnosticDescriptor StateItemUnknown { get; } = Entry(
        "state-item-unknown",
        Severity.Error,
        "`{0}` is not a processor-state item",
        "The items a signature, a `.state` or an `.ensure` may hold are a fixed set, such as `a8`, `i16`, `dp = 0` "
            + "or `keeps x`. This word looks like an item, or is written in an item's form, but is not one; check "
            + "its spelling. A plain word that is not an item is read as the name of a signature set.");

    internal static DiagnosticDescriptor OperatorsNeedParentheses { get; } = Entry(
        "operators-need-parentheses",
        Severity.Error,
        "`{0}` and `{1}` need parentheses to show which applies first",
        "nt65 gives every operator a precedence, but refuses two combinations that readers often misjudge: a shift "
            + "or bitwise operator with a different operator as its operand, and different logical operators "
            + "mixed. Add parentheses to say which applies first; the fix can add them.");

    internal static DiagnosticDescriptor ByteOperatorNeedsParentheses { get; } = Entry(
        "byte-operator-needs-parentheses",
        Severity.Error,
        "unary `{0}` binds tighter than `{1}`: write `({0}x) {1} y` or `{2}(x {1} y)` to say which is meant",
        "The byte operators bind tighter than any binary operator, so `<label + 1` means `(<label) + 1`, the low "
            + "byte plus one, not the low byte of `label + 1`. Because that is easy to misread, nt65 asks for "
            + "parentheses rather than guessing.");

    internal static DiagnosticDescriptor NotAFunction { get; } = Entry(
        "not-a-function",
        Severity.Error,
        "`{0}` is not a function",
        "The built-in functions are a fixed set. A function the program declares is a `.func` and is written "
            + "without the leading `.`.");

    private static DiagnosticArea Names { get; } =
        Opens("Names", "Declarations, scopes, modules and what a path reaches.");

    internal static DiagnosticDescriptor NotDeclared { get; } = Entry(
        "not-declared",
        Severity.Error,
        "`{0}` is not declared{1}",
        "Nothing in scope here declares the name. Where a name one letter away is declared, or another module "
            + "exports it, the message says so, because that is nearly always what was meant.");

    internal static DiagnosticDescriptor NotDeclaredIn { get; } = Entry(
        "not-declared-in",
        Severity.Error,
        "`{0}` is not declared in {1}{2}",
        "The part before `::` names a module, a scope, a type or a routine, and it does not declare the name after "
            + "it. Check the spelling, and whether the name is in a different scope. Order does not matter: every "
            + "name a scope declares can be reached, wherever in the scope it is declared.");

    internal static DiagnosticDescriptor NotExported { get; } = Entry(
        "not-exported",
        Severity.Error,
        "`{0}` is not exported by module `{1}`",
        "A module names what other modules may see, and this is not among it. The fix is an `.export` on the "
            + "declaration in the module that owns it.");

    internal static DiagnosticDescriptor DeclaredInAnotherModule { get; } = Entry(
        "declared-in-another-module",
        Severity.Error,
        "`{0}` is not declared here, and module `{1}` exports it: write `{2}::{3}`, or bring it in with `.use {4}::{5}`",
        "The name is not in scope in this file, and exactly one module in the program exports it, which is almost "
            + "always the one meant.");

    internal static DiagnosticDescriptor ModuleNotInTheBuild { get; } = Entry(
        "module-not-in-the-build",
        Severity.Error,
        "`{0}` is not declared, and no module `{1}` is in this build",
        "The first part of a path names a module or something in scope, and is neither. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ModuleUnknown { get; } = Entry(
        "module-unknown",
        Severity.Error,
        "no module `{0}` is in this build",
        "A path written from the root of the modules names a module the program has. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ExportAmbiguous { get; } = Entry(
        "export-ambiguous",
        Severity.Error,
        "`{0}` is ambiguous: modules `{1}` and `{2}` both export it, and both are brought in with `::*`; write "
            + "`{3}::{4}` to choose",
        "Two modules brought in with `.use module::*` export the same name, so which one is meant would depend on "
            + "the order the `.use` lines are read in. Write the full path, or bring in the one you mean by name "
            + "with `.use module::name`, which takes precedence over `::*`.");

    internal static DiagnosticDescriptor NotAScope { get; } = Entry(
        "not-a-scope",
        Severity.Error,
        "`{0}` is {1}, not a scope",
        "A `::` reaches into a module, a scope, a routine or a named type. The symbol before it is none of those, "
            + "so nothing can be reached through it.");

    internal static DiagnosticDescriptor NameAlreadyDeclared { get; } = Entry(
        "name-already-declared",
        Severity.Error,
        "`{0}` is already declared in this scope",
        "Declarations are a set, and a scope declares each name once. Where the other declaration is in this file "
            + "it is shown beside this one.");

    internal static DiagnosticDescriptor IdentParameterDeclared { get; } = Entry(
        "ident-parameter-declared",
        Severity.Error,
        "`{0}` is an `ident` parameter, so the macro body may not declare it: that would declare the caller's name",
        "An `ident` parameter is replaced by a name the caller passes in, so declaring it in the macro body would "
            + "declare that name in the caller's scope, deciding for the caller what its own argument means. "
            + "Declare the name in the caller, or give the body's declaration a different name, such as a cheap "
            + "local.");

    internal static DiagnosticDescriptor RegisterName { get; } = Entry(
        "register-name",
        Severity.Error,
        "`{0}` is a register name and cannot be used as a name",
        "Register names are reserved everywhere, because a symbol with the same name could not be told apart from "
            + "the register: `asl a` could mean the accumulator or a symbol called `a`. Mnemonics are not "
            + "reserved; register names are. Rename the symbol.");

    internal static DiagnosticDescriptor MnemonicName { get; } = Entry(
        "mnemonic-name",
        Severity.Warning,
        "`{0}` is an instruction on the {1}; as a name it is legal and easy to misread",
        "No mnemonic is reserved: when a line's first word is followed by `:` or `=`, it declares that word as a "
            + "name, even if it is spelled like an instruction. ca65 cannot define a bare symbol spelled like an "
            + "instruction, so nt65 writes a top-level one with its module in front, `main__lda`, as it already "
            + "does for exported names. The program and its output are correct; the warning is there because a "
            + "reader sees an instruction. Rename the symbol, or set `\"mnemonic-name\"` to `\"off\"` or "
            + "`\"error\"` under `diagnostics` in nt65.json.");

    internal static DiagnosticDescriptor CheapLocalOutsideAScope { get; } = Entry(
        "cheap-local-outside-a-scope",
        Severity.Error,
        "`{0}` is a cheap local, which needs an enclosing `.proc` or `.scope`",
        "A `@name` is private to the routine or scope around it, so there has to be one for it to be private to.");

    internal static DiagnosticDescriptor CheapLocalInAPath { get; } = Entry(
        "cheap-local-in-a-path",
        Severity.Error,
        "`{0}` is a cheap local and cannot be reached with `::`",
        "A cheap local is private to the routine or scope that declares it, so no path can reach it from outside. "
            + "Use an ordinary label if other code needs to refer to it.");

    internal static DiagnosticDescriptor ModuleUsedAsAName { get; } = Entry(
        "module-used-as-a-name",
        Severity.Error,
        "`{0}` is a module: a name in it is written `{1}::name`",
        "A module is not a value and has no address. What is wanted is a name inside it.");

    internal static DiagnosticDescriptor NameAloneOnALine { get; } = Entry(
        "name-alone-on-a-line",
        Severity.Error,
        "`{0}` is {1}, and only a `block` parameter may be written alone on a line",
        "A name on a line by itself inserts a macro's `block` parameter at that point. Any other name needs more "
            + "on the line: `name:` declares a label, `name = value` a constant, and `name!` calls a macro.");

    internal static DiagnosticDescriptor NotAMacro { get; } = Entry(
        "not-a-macro",
        Severity.Error,
        "`{0}` is {1}, not a macro: only a macro is called with `!`",
        "`!` after a name calls a macro, and marks the line as one whose code comes from somewhere else. Nothing "
            + "else is called that way: a routine is called with `jsr` or `jsl`.");

    internal static DiagnosticDescriptor DeclarationInABlockArgument { get; } = Entry(
        "declaration-in-a-block-argument",
        Severity.Error,
        "`{0}` is declared in a block argument, which may declare only cheap locals: the macro may insert the "
            + "block more than once",
        "A block argument may be spliced into a macro body in more than one place, and a declaration in it would "
            + "then be declared more than once. A cheap local is renamed per expansion, so it is safe.");

    internal static DiagnosticDescriptor ModuleMissing { get; } = Entry(
        "module-missing",
        Severity.Error,
        "this file has no `.module` line: start it with `.module name`",
        "Every file is one module and names it before anything else, with `.module name`, so that everything the "
            + "file declares has a path such as `name::symbol`.");

    internal static DiagnosticDescriptor MultiprocMisplaced { get; } = Entry(
        "multiproc-misplaced",
        Severity.Error,
        "`.multiproc` declares routines, and this one is inside {0}: a routine belongs at file level or in a "
            + "`.scope`",
        "`.multiproc` declares one routine per member of an enum, so it follows the same placement rule as "
            + "`.proc`: at file level or inside a `.scope`, not inside a routine, a `.data` block or a type.");

    internal static DiagnosticDescriptor ModuleDeclaredTwice { get; } = Entry(
        "module-declared-twice",
        Severity.Error,
        "this file already has a `.module` line: a file is exactly one module",
        "A file is one module. A large module is split into submodules, each its own file.");

    internal static DiagnosticDescriptor ModuleNotFirst { get; } = Entry(
        "module-not-first",
        Severity.Error,
        "`.module` must come before everything else in the file",
        "Everything a file declares belongs to the module it names, so the name comes before the declarations.");

    internal static DiagnosticDescriptor ModuleNameTaken { get; } = Entry(
        "module-name-taken",
        Severity.Error,
        "module `{0}` is already declared by `{1}`: a module is one file, and a large one is split into submodules",
        "A module name is the path everything in it is reached by, and the name its output file is named after, so "
            + "two files may not share one.");

    internal static DiagnosticDescriptor ModuleNameReserved { get; } = Entry(
        "module-name-reserved",
        Severity.Error,
        "module `{0}` is under `nt65`, which is reserved for the modules that come with nt65",
        "The modules under `nt65`, such as `nt65::cbm`, come with nt65 and join any program that names them. "
            + "A module of the program's own under that root could collide with one, now or in a later version.");

    internal static DiagnosticDescriptor ModuleNamesDifferInCase { get; } = Entry(
        "module-names-differ-in-case",
        Severity.Error,
        "modules `{0}` and `{1}` differ only in case, and a file system that ignores case writes both to one file",
        "Output is named after the module, and on a file system that ignores case two such names are one file. "
            + "The build would depend on which ran last.");

    internal static DiagnosticDescriptor PlaceMisplaced { get; } = Entry(
        "place-misplaced",
        Severity.Error,
        "`.place` must be at file level, outside every block: which modules are assembled together cannot depend "
            + "on a condition",
        "Which modules make up one translation unit, and so one `.s` output file, is fixed by the files "
            + "themselves. A `.place` goes at file level, in a `.segment` region or before any, and never inside "
            + "an `.if`, a scope, a routine, a segment block, a repetition or a macro body. For code that only one "
            + "configuration needs, place the module unconditionally and put its contents under an `.if`.");

    internal static DiagnosticDescriptor PlaceNotPlaceable { get; } = Entry(
        "place-not-placeable",
        Severity.Error,
        "module `{0}` is not marked as placeable: declare it `.module {0}: placed` so that another module can "
            + "place it",
        "A module whose `.module` line says nothing is assembled on its own, into an output file of its own. A "
            + "module that another module places has to say so on its own `.module` line, so that its file can be "
            + "read correctly by itself: `placed` means it is only ever placed, and `placeable` means it may be "
            + "placed or stand alone. The fix marks it `placed`.");

    internal static DiagnosticDescriptor PlacedTwice { get; } = Entry(
        "placed-twice",
        Severity.Error,
        "module `{0}` is already placed by `{1}`: a module can be placed only once",
        "A placed module's bytes are written where its `.place` is, so a module can have only one. Code that two "
            + "programs share is a module that each program places once.");

    internal static DiagnosticDescriptor PlacementCycle { get; } = Entry(
        "placement-cycle",
        Severity.Error,
        "this `.place` would make a module place itself: {0}",
        "A module and everything it places are laid out as one translation unit, in the order the `.place` lines "
            + "say, which a cycle cannot be.");

    internal static DiagnosticDescriptor PlacedNowhere { get; } = Entry(
        "placed-nowhere",
        Severity.Error,
        "module `{0}` is declared `placed`, and nothing places it: `.place {0}` goes where its bytes belong, or `placeable` lets it stand alone",
        "A module declared `placed` has no output of its own: its bytes are written where another module places "
            + "it. One that nothing places would be written nowhere.");

    internal static DiagnosticDescriptor NameIsAModulePath { get; } = Entry(
        "name-is-a-module-path",
        Severity.Error,
        "`{0}` is the path of a module, and module `{1}` may not declare `{2}` as well",
        "A path reaches either a module or a name in one, so a module may not declare a name that is already the "
            + "start of another module's path.");

    internal static DiagnosticDescriptor UseMisplaced { get; } = Entry(
        "use-misplaced",
        Severity.Error,
        "`.use` belongs at the top level of a module",
        "What a module brings in is part of its interface, so a `.use` belongs where the interface is: at the top "
            + "level, not under a block.");

    internal static DiagnosticDescriptor UseBringsInTwice { get; } = Entry(
        "use-brings-in-twice",
        Severity.Error,
        "a `.use` already brings in `{0}`",
        "Two `.use` items that bring in the same name would leave which one meant undecided. `as` gives one of "
            + "them a name of its own.");

    internal static DiagnosticDescriptor UseCollidesWithDeclaration { get; } = Entry(
        "use-collides-with-declaration",
        Severity.Error,
        "`{0}` is already declared in this module, so a `.use` cannot bring in another: use `as` to give it a "
            + "different name",
        "Names brought in with `.use` share the file's namespace with its own declarations, so one name cannot "
            + "mean both. Write `.use module::name as other` to bring it in under a different name.");

    internal static DiagnosticDescriptor UseStarNotAModule { get; } = Entry(
        "use-star-not-a-module",
        Severity.Error,
        "`.use {0}::*` brings in what a module exports, and `{1}` is {2}",
        "`::*` brings in everything a module exports, so what is before it has to be a whole module name.");

    internal static DiagnosticDescriptor ReexportStar { get; } = Entry(
        "reexport-star",
        Severity.Error,
        "`.export .use` must name what it re-exports: `*` would re-export everything the other module exports, "
            + "including names it adds later",
        "A `.export .use` makes another module's names part of this module's interface. A `*` would make that "
            + "interface whatever the other module exports later, which is not a promise this module can keep.");

    internal static DiagnosticDescriptor ReexportModule { get; } = Entry(
        "reexport-module",
        Severity.Error,
        "`{0}` is a module: `.export .use` re-exports names in a module, not the module itself",
        "A path cannot end at a module, so there is nothing to re-export there. Re-export the names you want one "
            + "by one, `.export .use module::name`.");

    internal static DiagnosticDescriptor ReexportNeeded { get; } = Entry(
        "reexport-needed",
        Severity.Error,
        "`{0}` is declared in module `{1}`, not this one: re-export it with `.export .use {2}`",
        "A module can export only what it declares itself. A name brought in with `.use` still belongs to the "
            + "module that declares it; to make it part of this module's interface as well, re-export it with "
            + "`.export .use`, which records where it came from.");

    internal static DiagnosticDescriptor LabelOutsideARoutine { get; } = Entry(
        "label-outside-a-routine",
        Severity.Error,
        "`{0}` is a label outside a `.proc`: labels mark positions in code{1}",
        "Labels belong inside a `.proc`, where they mark positions in code. Outside a routine, data is named by a "
            + "`.data` declaration, such as `.data name: .byte 1, 2`; the fix can rewrite the line as one.");

    internal static DiagnosticDescriptor LabelInData { get; } = Entry(
        "label-in-data",
        Severity.Error,
        "`{0}` is a label in `.data`: a named member is `.data {1}: ...`, and a position is `@{2}:`",
        "Inside mixed data (`.data name {`), a named member is declared with `.data member: ...`, which gives it a "
            + "type and a size, and a position is marked with a cheap local, `@here:`. A plain label is not "
            + "allowed there.");

    internal static DiagnosticDescriptor SegmentRegionMisplaced { get; } = Entry(
        "segment-region-misplaced",
        Severity.Error,
        "a `.segment NAME` region belongs at file level, outside every block: inside one, `.segment NAME {{ }}` places what it holds",
        "A `.segment NAME` region says where everything after it in the file goes, which only makes sense at file "
            + "level. Inside a block, the block form places what it holds.");

    internal static DiagnosticDescriptor SignatureSetNameIsAnItem { get; } = Entry(
        "signature-set-name-is-an-item",
        Severity.Error,
        "`{0}` is a signature item, and cannot name a signature set",
        "A signature set is a name for a list of processor-state items, and it is used in the same places as those "
            + "items. Its own name therefore cannot be an item such as `a8`, or a signature that used it would be "
            + "ambiguous. Choose another name.");

    internal static DiagnosticDescriptor SignatureItemNeeds65816 { get; } = Entry(
        "signature-item-needs-65816",
        Severity.Error,
        "`{0}` describes 65816 state, which the {1} does not have",
        "Widths, the emulation flag, the direct page and the data bank are the 65816's. Earlier processors have "
            + "eight-bit registers and none of that state.");

    internal static DiagnosticDescriptor FamilyMisplaced { get; } = Entry(
        "family-misplaced",
        Severity.Error,
        "{0}",
        "A family, a `.proc` or `.data` named after an `.each` binding, declares one name per enum member into the "
            + "scope around the `.each`. So the `.each` must be where those declarations could be written "
            + "directly, at file level or in a `.scope`, and must walk a named enum, whose member names become the "
            + "declared names.");

    internal static DiagnosticDescriptor FamilyNotOverAnEnum { get; } = Entry(
        "family-not-over-an-enum",
        Severity.Error,
        "a family needs a named enum to walk, and `{0}` {1}",
        "A family declares one routine or one data declaration per member of a named enum, and is named after the "
            + "members, so the enum has to be one whose members have names.");

    internal static DiagnosticDescriptor FamilyDeclaresTooMuch { get; } = Entry(
        "family-declares-too-much",
        Severity.Error,
        "`{0}` would declare one {1} per member, repeating everything inside it: a family declares only routines "
            + "and data, so write one family per role, such as `note::{2}` and `stop::{3}`",
        "A family declares one routine or data declaration per enum member. A scope or `.data` block named after "
            + "the binding would repeat everything inside it once per member, which is really several families "
            + "written as one. Write one family per role instead, each inside the scope for that role.");

    internal static DiagnosticDescriptor FamilyMemberCollides { get; } = Entry(
        "family-member-collides",
        Severity.Error,
        "`{0}` is a member of `{1}` and is already declared in this scope: a family declares one name per member",
        "Each instance of a family takes the member's name, so a name already declared in the scope would be "
            + "declared twice.");

    internal static DiagnosticDescriptor MacroMisplaced { get; } = Entry(
        "macro-misplaced",
        Severity.Error,
        "a `.macro` belongs at file level or in a `.scope`, not inside {0}",
        "A macro declared inside a routine could refer to that routine's cheap locals, and an expansion in another "
            + "routine would then branch into them, out of sight of the flow analysis. Declare macros at file "
            + "level or in a `.scope`.");

    internal static DiagnosticDescriptor DefinedAsksAboutDefines { get; } = Entry(
        "defined-asks-about-defines",
        Severity.Error,
        "`.defined` tests only build defines, and `{0}` is declared by the program: to check the program, use "
            + "`.assert`",
        "Conditions are decided before the program is read, from the build configuration alone, so `.defined` of a "
            + "name the program declares would be false whatever the program says. `.defined` therefore takes only "
            + "defines. To check something about the program, use `.assert`, which the analysis can answer.");

    internal static DiagnosticDescriptor DefineRedeclared { get; } = Entry(
        "define-redeclared",
        Severity.Error,
        "`{0}` is already a define, visible in every file: a file may not declare it again",
        "A define is visible in every file as if every module had brought it in, so a declaration of the same name "
            + "would shadow it in one file and not in another.");

    internal static DiagnosticDescriptor SignatureMissing { get; } = Entry(
        "signature-missing",
        Severity.Error,
        "`{0}` is {1} with no processor-state signature: nt65 cannot see its body, so declare what it expects and "
            + "leaves, or `?` if that is unknown",
        "On the 65816, register widths and other processor state matter at every call. A routine with no body, an "
            + "extern proc or an imported routine, is all the analysis has to go on at its callers, so its "
            + "signature has to say what state it expects and what it leaves, for example `.proc TOOLBOX = "
            + "$E10000: a16, i16`. `?` says that nothing is known.");

    internal static DiagnosticDescriptor ExportNarrowsAddressSize { get; } = Entry(
        "export-narrows-address-size",
        Severity.Error,
        "`{0}` is `{1}`, and an export may widen an address size but not narrow it: `{2}: {3}` or wider",
        "Other modules reach the name at the size the export gives, and reaching a two-byte address as if it were "
            + "one byte reads the wrong place. Widening is safe; narrowing is not.");

    internal static DiagnosticDescriptor LinkerNameIsAnInstruction { get; } = Entry(
        "linker-name-is-an-instruction",
        Severity.Error,
        "cannot export as `{0}`: ca65 reads it as an instruction, and an `as` name is written to the output "
            + "unchanged",
        "An `as` name is written into the output exactly as given, with no module in front, and ca65 reads a line "
            + "that starts with an instruction name, for any CPU it supports, as an instruction, so it could never "
            + "define this symbol. Choose another linker name. This is an error, where `mnemonic-name` is only a "
            + "warning, because there is no other spelling to fall back on.");

    internal static DiagnosticDescriptor UnusedSymbol { get; } = Entry(
        "unused-symbol",
        Severity.Warning,
        "`{0}` is never used: nothing names it, and it is not exported",
        "Nothing in the program names the declaration and the file does not export it, so nothing reads it. Data "
            + "that holds values may be there for where it lands, so only a declaration that reserves storage is "
            + "reported. A declaration another module writes without the export is named, wrongly, and is reported "
            + "there instead.");

    internal static DiagnosticDescriptor UnusedUseItem { get; } = Entry(
        "unused-use-item",
        Severity.Warning,
        "`{0}` is brought in and nothing names it: the `.use` item may go",
        "The `.use` brings the name in and the file never writes it. A `.export .use` re-exports rather than "
            + "uses, and is not reported.");

    private static DiagnosticArea Values { get; } =
        Opens("Values", "Constants, expressions, built-in functions and the build configuration.");

    internal static DiagnosticDescriptor DefinedInTermsOfItself { get; } = Entry(
        "defined-in-terms-of-itself",
        Severity.Error,
        "`{0}` is defined in terms of itself",
        "The value depends on itself, directly or through other names, so it can never be worked out. Break the "
            + "cycle by defining one of the names without referring back to the others. The cycle is reported "
            + "once, at one of its declarations, and the other names in it are shown as related locations.");

    internal static DiagnosticDescriptor NumberTooWide { get; } = Entry(
        "number-too-wide",
        Severity.Error,
        "{0} does not fit in 32 bits: a value written to the ca65 output must be between -$80000000 and $ffffffff",
        "nt65 computes in 64-bit signed arithmetic, but ca65 computes in 32 bits, so every value that reaches the "
            + "output must fit ca65's range. The check is made where the value is declared, not again at every "
            + "place it is used.");

    internal static DiagnosticDescriptor ArithmeticOverflow { get; } = Entry(
        "arithmetic-overflow",
        Severity.Error,
        "{0} overflows nt65's 64-bit signed arithmetic",
        "nt65 computes in 64-bit signed arithmetic. An operation whose result does not fit in 64 bits is an error "
            + "rather than wrapping around, because a wrapped result is almost never what was meant.");

    internal static DiagnosticDescriptor ShiftCountOutOfRange { get; } = Entry(
        "shift-count-out-of-range",
        Severity.Error,
        "shift count {0} is out of range: a shift moves 0 to 63 places",
        "Values are 64 bits wide, so `<<` and `>>` shift by 0 to 63 places. A count outside that range, including "
            + "a negative one, is an error rather than being reduced or clamped.");

    internal static DiagnosticDescriptor SqrtOfANegative { get; } = Entry(
        "sqrt-of-a-negative",
        Severity.Error,
        "`.sqrt({0})` has no result: the argument cannot be negative",
        "`.sqrt(n)` is the largest whole number whose square is at most n. No number has a negative square, so n "
            + "must be 0 or more.");

    internal static DiagnosticDescriptor StrsubOutOfRange { get; } = Entry(
        "strsub-out-of-range",
        Severity.Error,
        "`.strsub` reaches outside the text: it asks for {0}, and the text is {1}",
        "`.strsub(s, start, count)` is the `count` bytes of `s` starting at byte `start`, counting from 0. The "
            + "whole range must lie inside the text; nt65 reports an error rather than returning a shorter result.");

    internal static DiagnosticDescriptor StrcatNotAByte { get; } = Entry(
        "strcat-not-a-byte",
        Severity.Error,
        "`.strcat` adds a number as a single byte, and {0} is not in the range 0 to 255",
        "`.strcat` joins texts, and a number among its arguments is added as one byte, as in `.strcat(\"A\", $80 | "
            + "'X')`. A number outside 0 to 255 does not fit in a byte, and nt65 does not truncate it. Mask it "
            + "with `& $ff`, or use `<` for its low byte, if that is what was meant.");

    internal static DiagnosticDescriptor TurnOrScaleOutOfRange { get; } = Entry(
        "turn-or-scale-out-of-range",
        Severity.Error,
        "`{0}` needs a turn from 1 to {1} and a scale from -{1} to {1}",
        "In `.sin(angle, turn, scale)` and `.cos(angle, turn, scale)`, `turn` is how many angle units make a full "
            + "circle and `scale` is the value the result has at 1.0. Both are limited to 32-bit values, because "
            + "anything written to the ca65 output has to fit in 32 bits anyway.");

    internal static DiagnosticDescriptor CyclesNeedsAPosition { get; } = Entry(
        "cycles-needs-a-position",
        Severity.Error,
        "`{0}` is {1}, not a position in code: a cycle count runs from one label or routine name to another",
        "`.mincycles(from, to)` and `.maxcycles(from, to)` count the cycles one pass takes from one point in the "
            + "code to another, so each argument must be a label or a routine's name.");

    internal static DiagnosticDescriptor CyclesSpanHasNoBound { get; } = Entry(
        "cycles-span-has-no-bound",
        Severity.Error,
        "`{0}` cannot count the cycles between these positions: {1}",
        "`.mincycles` and `.maxcycles` add up the cycles of the instructions between two positions, which gives a "
            + "true count only when that code runs straight through once. A call takes as long as the routine it "
            + "calls, a loop repeats its body an unknown number of times, and a jump nt65 cannot follow could go "
            + "anywhere, so none of these may be inside the span. Both positions must also be in the same routine "
            + "and the same segment block, with the start before the end.");

    internal static DiagnosticDescriptor BuiltinArguments { get; } = Entry(
        "builtin-arguments",
        Severity.Error,
        "`{0}` takes {1}",
        "The built-in function was given the wrong number or kind of arguments. The message says what it takes.");

    internal static DiagnosticDescriptor DivisionByZero { get; } = Entry(
        "division-by-zero",
        Severity.Error,
        "division by zero",
        "nt65 evaluates constant expressions while it builds, and a division by zero has no value to write to the "
            + "output. Check the divisor.");

    internal static DiagnosticDescriptor OperatorOnText { get; } = Entry(
        "operator-on-text",
        Severity.Error,
        "`{0}` cannot be used on a string",
        "Text is a sequence of bytes for a data declaration to write. The arithmetic and bitwise operators are on "
            + "numbers: `.strcat` joins texts, `.strsub` takes part of one, and `.strat` reads one of its bytes.");

    internal static DiagnosticDescriptor ScopeHasNoAddress { get; } = Entry(
        "scope-has-no-address",
        Severity.Error,
        "`{0}` is a scope and has no address: name a routine or data inside it instead",
        "A `.scope` only groups names and takes no bytes of its own, so there is no address to use. Name a routine "
            + "or a declaration inside it, such as `scope::name`.");

    internal static DiagnosticDescriptor EnumMemberIsNotAnAddress { get; } = Entry(
        "enum-member-is-not-an-address",
        Severity.Error,
        "`{0}` is an enum member, so its value must be a constant, and this is an address",
        "An enum member is a number, and a member with no value counts on from the one before, so a member cannot "
            + "be set to an address. Where an address was meant, use a constant or an address alias outside the "
            + "enum, or refer to the routine or data declared for that member.");

    internal static DiagnosticDescriptor NotIndexable { get; } = Entry(
        "not-indexable",
        Severity.Error,
        "`{0}` cannot be indexed with `[i]`: it is {1}",
        "`[i]` selects the i-th element of a data declaration that holds a counted list of elements, such as "
            + "`.byte[8]` or `.type Point[4]`. Anything else, including mixed data, has no elements to select.");

    internal static DiagnosticDescriptor ElementIndexNotConstant { get; } = Entry(
        "element-index-not-constant",
        Severity.Error,
        "an element index must be a constant: to index at run time, use indexed addressing such as `{0},x`",
        "Which element `[i]` selects is decided while nt65 builds, because it becomes a fixed address in the "
            + "output. To choose an element while the program runs, use indexed addressing with the X or Y "
            + "register.");

    internal static DiagnosticDescriptor ElementIndexOutOfRange { get; } = Entry(
        "element-index-out-of-range",
        Severity.Error,
        "{0}",
        "The declaration says how many elements it holds, and this index is not one of them.");

    internal static DiagnosticDescriptor NothingToMeasure { get; } = Entry(
        "nothing-to-measure",
        Severity.Error,
        "`{0}` is {1} and takes no bytes of its own, so `{2}` has nothing to measure",
        "`.endof` and `.spanof` measure something that takes bytes in the output, such as a data declaration or a "
            + "routine. The name given refers to something that takes none, such as a constant, so there is "
            + "nothing to measure.");

    internal static DiagnosticDescriptor CountofHasNoElements { get; } = Entry(
        "countof-has-no-elements",
        Severity.Error,
        "`{0}` is {1}, which has bytes and no elements: `.sizeof({2})` is how many bytes it takes",
        "`.countof` answers how many elements a counted declaration holds. Something that is bytes and not "
            + "elements is measured with `.sizeof`.");

    internal static DiagnosticDescriptor SizeofDependsOnExpansion { get; } = Entry(
        "sizeof-depends-on-expansion",
        Severity.Error,
        "cannot compute `.sizeof({0})`: a macro call inside it is expanded only after constants are known; "
            + "`.spanof({1})` gives its size at link time",
        "Constants and data sizes are worked out before any macro is expanded, and how many bytes a macro call in "
            + "a data declaration writes is known only once it is expanded. So the declaration's size is not a "
            + "constant. `.spanof` asks the linker for the size instead, which makes it a link-time value.");

    internal static DiagnosticDescriptor SizeofDependsOnAlignment { get; } = Entry(
        "sizeof-depends-on-alignment",
        Severity.Error,
        "cannot compute `.sizeof({0})`: its `.align` padding depends on where it is placed; `.spanof({1})` gives "
            + "its size at link time",
        "An `.align` inside a declaration pads to the next boundary, and how much padding that takes depends on "
            + "where the linker places it. So the declaration's size is not a constant. `.spanof` asks the linker "
            + "for the size instead, which makes it a link-time value.");

    internal static DiagnosticDescriptor MeasuresADeclaration { get; } = Entry(
        "measures-a-declaration",
        Severity.Error,
        "`{0}` takes a whole declaration, not an element of one like `{1}`",
        "The measuring functions (`.sizeof`, `.countof`, `.endof`, `.spanof`) take the name of a whole "
            + "declaration. An indexed element such as `table[2]` is a position inside one and has no size of its "
            + "own; measure the element type instead, or the whole declaration.");

    internal static DiagnosticDescriptor NotMeasurable { get; } = Entry(
        "not-measurable",
        Severity.Error,
        "`{0}` is {1}: `{2}` measures a `.data` declaration, a routine or a type",
        "The measuring functions take something that occupies bytes, such as a `.data` declaration or a routine, "
            + "or a type, which says how many bytes it takes. A label is only a position and a scope only groups "
            + "names, so neither has a size. An import can be measured once it states its type, as in `.import "
            + "name: .byte[n]`.");

    internal static DiagnosticDescriptor FunctionArgumentCount { get; } = Entry(
        "function-argument-count",
        Severity.Error,
        "`{0}` takes {1} argument(s), and {2} were given",
        "A `.func` takes exactly the parameters it declares. There are no defaults and no overloads.");

    internal static DiagnosticDescriptor ConditionIsText { get; } = Entry(
        "condition-is-text",
        Severity.Error,
        "an `.if` condition must be a number, not text",
        "An `.if` or `.elseif` condition is true when its value is nonzero, so it must evaluate to a number.");

    internal static DiagnosticDescriptor ConditionNamesTheProgram { get; } = Entry(
        "condition-names-the-program",
        Severity.Error,
        "`{0}` is not a define or a `.config` setting: an `.if` condition can only test the build configuration; "
            + "use `.assert` to check the program",
        "Conditions are evaluated first, to decide which parts of the source are assembled, so they can only use "
            + "defines (from the project file or the command line) and `.config` settings. Labels, constants and "
            + "other names the program declares are not known at that point, since which of them exist depends on "
            + "the conditions. To check something about the program, use `.assert`, which is evaluated once the "
            + "program is complete.");

    internal static DiagnosticDescriptor ConditionAsksAboutTheProgram { get; } = Entry(
        "condition-asks-about-the-program",
        Severity.Error,
        "`{0}` asks about the program, which an `.if` condition cannot do: use `.assert` to check the program",
        "Conditions decide which parts of the source are assembled, so they are evaluated before the program "
            + "exists and can only test the build configuration. Functions that measure or inspect the program, "
            + "such as `.sizeof`, have no answer yet. Use `.assert`, which is evaluated once the program is "
            + "complete.");

    internal static DiagnosticDescriptor ConditionCallsAFunction { get; } = Entry(
        "condition-calls-a-function",
        Severity.Error,
        "an `.if` condition cannot call a `.func`",
        "Conditions decide which declarations exist, and a `.func` is one of those declarations, so a condition "
            + "cannot depend on it. Use a define or a `.config` setting, or write the expression out in the "
            + "condition.");

    internal static DiagnosticDescriptor SelectArguments { get; } = Entry(
        "select-arguments",
        Severity.Error,
        "`.select` takes a condition and the two values it chooses between: `.select(c, a, b)`",
        "`.select(c, a, b)` is `a` when `c` is nonzero and `b` otherwise, so it takes exactly three arguments.");

    internal static DiagnosticDescriptor SelectConditionIsText { get; } = Entry(
        "select-condition-is-text",
        Severity.Error,
        "a `.select` condition must be a number, not text",
        "`.select(c, a, b)` chooses `a` when `c` is nonzero and `b` otherwise, so `c` must evaluate to a number.");

    internal static DiagnosticDescriptor SelectConditionNotConstant { get; } = Entry(
        "select-condition-not-constant",
        Severity.Error,
        "a `.select` condition must be a constant",
        "Which of the two values a `.select` gives depends on its condition, so the condition has to be known "
            + "while nt65 builds, not only at link time or at run time.");

    internal static DiagnosticDescriptor CpuUnderACondition { get; } = Entry(
        "cpu-under-a-condition",
        Severity.Error,
        "`.cpu` cannot be inside an `.if`: conditions can test the processor, so it must be set first",
        "Conditions can test the processor with `.target` and `.has`, so the processor has to be known before any "
            + "condition is evaluated. Write `.cpu` outside every `.if`, or set the processor in the project file "
            + "or on the command line.");

    internal static DiagnosticDescriptor CpuDisagrees { get; } = Entry(
        "cpu-disagrees",
        Severity.Error,
        "`.cpu {1}` conflicts with the processor this program is built for, the {0}",
        "A program is built for one processor. When the project file, the command line or another file sets it, a "
            + "`.cpu` in a source file must name the same processor. Change or remove the `.cpu`.");

    internal static DiagnosticDescriptor ElseWithoutIf { get; } = Entry(
        "else-without-if",
        Severity.Error,
        "`{0}` has no `.if` before it",
        "An `.elseif` or an `.else` follows the block of an `.if` or `.elseif`, and there is none open here. Check "
            + "for a missing `.if` or a misplaced closing brace.");

    internal static DiagnosticDescriptor ConfigMisplaced { get; } = Entry(
        "config-misplaced",
        Severity.Error,
        "`.config` must be at file level, outside every block",
        "A `.config` declares a setting that the build can give a value. Which settings a module has cannot depend "
            + "on a condition or be nested inside another declaration, so `.config` is written at the top level of "
            + "the file.");

    internal static DiagnosticDescriptor ConfigIsText { get; } = Entry(
        "config-is-text",
        Severity.Error,
        "a `.config` value must be a number, not text",
        "A setting is a number, so that the project file or the command line can override it.");

    internal static DiagnosticDescriptor SettingUnknown { get; } = Entry(
        "setting-unknown",
        Severity.Error,
        "`{0}` is not a `.config` setting of that module: the build can only set a setting a module declares and "
            + "exports",
        "A define whose name has a module path, in the project file or on the command line, sets that module's "
            + "`.config`. The module declares no `.config` by that name; check the spelling and the module path.");

    internal static DiagnosticDescriptor SettingNotExported { get; } = Entry(
        "setting-not-exported",
        Severity.Error,
        "`{0}` is not exported by module `{1}`, so the build cannot set it",
        "Only a `.config` that its module exports can be set from the project file or the command line; one the "
            + "module keeps private always has the value it declares. Export the setting to let the build change "
            + "it.");

    internal static DiagnosticDescriptor DeclarationInARepetition { get; } = Entry(
        "declaration-in-a-repetition",
        Severity.Error,
        "{0} cannot be inside a `.repeat` or `.each` body: {1}",
        "A `.repeat` or `.each` body is written out once per iteration. Exports, imports, routines, segments and "
            + "definitions each name one thing for the whole program, so declaring them again on every iteration "
            + "makes no sense. Move the statement outside the body.");

    internal static DiagnosticDescriptor RepeatCountNotConstant { get; } = Entry(
        "repeat-count-not-constant",
        Severity.Error,
        "a `.repeat` count must be a constant",
        "How many times the body is written out decides what the program contains, so the count has to be known "
            + "while nt65 builds.");

    internal static DiagnosticDescriptor RepeatCountNegative { get; } = Entry(
        "repeat-count-negative",
        Severity.Error,
        "a `.repeat` count cannot be negative, and this one is {0}",
        "A repetition runs a number of turns, and a negative number of turns is not one.");

    internal static DiagnosticDescriptor EachNotOverAList { get; } = Entry(
        "each-not-over-a-list",
        Severity.Error,
        "`.each` walks a list or an enum, and this is neither",
        "`.each` walks something with items in a fixed order: a `.list`, or the members of an enum.");

    internal static DiagnosticDescriptor BindingNotOverAnEnum { get; } = Entry(
        "binding-not-over-an-enum",
        Severity.Error,
        "`{0}` does not iterate over an enum, so it cannot end a path: only an `.each` over an enum names a member "
            + "this way",
        "Inside `.each Enum, e`, a path such as `handlers::e` names the member of `handlers` that has the same "
            + "name as the current enum member. When `.each` iterates over a `.list` or a list parameter, its "
            + "variable stands for a value rather than a name, so it cannot be used at the end of a path.");

    internal static DiagnosticDescriptor FamilyMemberMissing { get; } = Entry(
        "family-member-missing",
        Severity.Error,
        "`{0}` has no member `{1}`, which `{2}` names on this iteration",
        "Inside `.each Enum, e`, a path `container::e` names the member of `container` with the same name as the "
            + "current enum member. `container` has no member of that name; every enum member needs a matching "
            + "declaration inside it.");

    internal static DiagnosticDescriptor IncbinUnreadable { get; } = Entry(
        "incbin-unreadable",
        Severity.Error,
        "cannot read `{0}` for `.incbin`",
        "nt65 reads an `.incbin` file to learn how many bytes it takes. The path is relative to the source file "
            + "that names it, and there the file is missing or cannot be read. ca65 reads the file again when it "
            + "assembles the output.");

    internal static DiagnosticDescriptor CharmapHasNoEntry { get; } = Entry(
        "charmap-has-no-entry",
        Severity.Error,
        "charmap `{0}` has no entry for `{1}`",
        "A charmap applied to text must map every character in it; nt65 does not fall back to ASCII for a "
            + "character the charmap leaves out. Add an entry for the character, or remove it from the text.");

    internal static DiagnosticDescriptor MemberHasNoValue { get; } = Entry(
        "member-has-no-value",
        Severity.Error,
        "`{0}` is a struct member and cannot have a value: for several `{1}` elements, write `{1}[n]`",
        "A `.struct` or `.union` member only reserves room at an offset and holds no data, so a value written "
            + "after its type is an error. `colors: .word 16` would reserve one word, not sixteen; several "
            + "elements of one type are written as a count, `colors: .word[16]`.");

    internal static DiagnosticDescriptor MemberCountNotANumber { get; } = Entry(
        "member-count-not-a-number",
        Severity.Error,
        "`{0}` needs an element count: write `{1}[n]`",
        "How many elements a member holds is part of its type, so the count must be written as a number. `[]`, "
            + "which counts the values given, has nothing to count in a `.struct`.");

    internal static DiagnosticDescriptor MemberReservesNothing { get; } = Entry(
        "member-reserves-nothing",
        Severity.Error,
        "`{0}` reserves no room: declare a member with a type such as `.byte`, `.word[n]` or `.res n`",
        "A `.struct` member only reserves room at its offset, so it is declared with an element type or with "
            + "`.res`. A directive that writes data, such as `.strz`, reserves nothing a member can use.");

    internal static DiagnosticDescriptor TargetArgument { get; } = Entry(
        "target-argument",
        Severity.Error,
        "`.target` takes {0}",
        "`.target` asks whether the program is built for one named processor, spelled as the project file "
            + "spells it.");

    internal static DiagnosticDescriptor HasArgument { get; } = Entry(
        "has-argument",
        Severity.Error,
        "`.has` takes a mnemonic, such as `.has(phx)`",
        "`.has(mnemonic)` asks whether the processor the program is built for has an instruction. Testing for the "
            + "instruction, rather than listing the processors that have it, keeps code that targets several "
            + "processors correct.");

    private static DiagnosticArea Macros { get; } =
        Opens("Macros", "Macro declarations, calls, arguments and expansion.");

    internal static DiagnosticDescriptor DeclarationInAMacroBody { get; } = Entry(
        "declaration-in-a-macro-body",
        Severity.Error,
        "{0} cannot be inside a macro body: {1}",
        "A macro body is written out at each call, in the module that calls it. These statements would either "
            + "declare a name in the caller or make something that exists once for the whole program depend on how "
            + "many times the macro is called. Move the statement outside the macro.");

    internal static DiagnosticDescriptor MacroRecursive { get; } = Entry(
        "macro-recursive",
        Severity.Error,
        "`{0}` calls itself{1}: a macro cannot be recursive",
        "Every macro expansion must be finite, and nt65 does not use a depth limit to stop one, so a macro may not "
            + "call itself, directly or through other macros. To handle a variable number of arguments, use a "
            + "`list` parameter with `.each` instead of recursion.");

    internal static DiagnosticDescriptor MacroNamesUnexported { get; } = Entry(
        "macro-names-unexported",
        Severity.Error,
        "`{0}!` is exported but uses `{1}`, which is not exported: the macro expands in the caller's module, where "
            + "`{1}` cannot be reached",
        "An exported macro expands in the module that calls it, so every name its body uses must be reachable from "
            + "there. Export the name it uses, or stop exporting the macro.");

    internal static DiagnosticDescriptor ParameterUnknown { get; } = Entry(
        "parameter-unknown",
        Severity.Error,
        "`{0}` has no parameter called `{1}`",
        "A named argument names a parameter the macro declares.");

    internal static DiagnosticDescriptor BlockParameterUnknown { get; } = Entry(
        "block-parameter-unknown",
        Severity.Error,
        "`{0}` has no `block` parameter called `{1}`",
        "A block argument after the parentheses names a `block` parameter the macro declares.");

    internal static DiagnosticDescriptor ArgumentGivenTwice { get; } = Entry(
        "argument-given-twice",
        Severity.Error,
        "parameter `{0}` is given twice",
        "Each parameter takes one argument, either by position or by name, not both.");

    internal static DiagnosticDescriptor ArgumentMissing { get; } = Entry(
        "argument-missing",
        Severity.Error,
        "`{0}` needs an argument for {1}",
        "A parameter with no default must be given an argument at every call, by position or by name.");

    internal static DiagnosticDescriptor ArgumentAfterANamedOne { get; } = Entry(
        "argument-after-a-named-one",
        Severity.Error,
        "a positional argument cannot follow a named one",
        "Positional arguments are matched to parameters in order, so they must all come before any named argument. "
            + "Move this argument before the named ones, or give it by name.");

    internal static DiagnosticDescriptor ArgumentCount { get; } = Entry(
        "argument-count",
        Severity.Error,
        "`{0}` takes {1}, and this call gives more",
        "The call gives more positional arguments than the macro has parameters to bind them to.");

    internal static DiagnosticDescriptor BlockArgumentInParentheses { get; } = Entry(
        "block-argument-in-parentheses",
        Severity.Error,
        "`{0}` is a `block` parameter: write its block after the parentheses, not inside them",
        "A block argument is written after the call's parentheses, as `name!(...) { ... }`, where it reads like "
            + "any other block of code.");

    internal static DiagnosticDescriptor BlockArgumentUnexpected { get; } = Entry(
        "block-argument-unexpected",
        Severity.Error,
        "`{0}` takes no block, and this call gives it one",
        "The macro declares no `block` parameter, so there is nowhere for the block to go.");

    internal static DiagnosticDescriptor BlockContinuesNothing { get; } = Entry(
        "block-continues-nothing",
        Severity.Error,
        "this block argument does not follow a macro call",
        "`} name {` closes one block argument of a macro call and opens the next. There is no macro call before it "
            + "for the block to belong to.");

    internal static DiagnosticDescriptor BlockChangesState { get; } = Entry(
        "block-changes-state",
        Severity.Error,
        "the block given to `{0}!` must leave the processor state as it found it: it starts with `{1}` and ends "
            + "with `{2}`",
        "A macro that takes a block writes its own code around the block, and that code assumes the register "
            + "widths and mode are the same after the block as before it. Restore them at the end of the block.");

    internal static DiagnosticDescriptor ParameterAfterBlock { get; } = Entry(
        "parameter-after-block",
        Severity.Error,
        "`{0}` comes after the `block` parameter `{1}`, and a block is written after the parentheses",
        "A block is written after the parentheses, so a `block` parameter is the last one.");

    internal static DiagnosticDescriptor ParameterAfterList { get; } = Entry(
        "parameter-after-list",
        Severity.Error,
        "`{0}` comes after the `list` parameter `{1}`, which takes every remaining argument",
        "A `list` parameter takes every remaining argument, so nothing after it could ever be given one.");

    internal static DiagnosticDescriptor OperandArgumentParenthesized { get; } = Entry(
        "operand-argument-parenthesized",
        Severity.Error,
        "`{0}` takes an operand, and `{1}` is read as an expression in parentheses: put it in braces to pass "
            + "indirect addressing",
        "Without braces, `(ptr)` is an expression in parentheses, as it is everywhere else. To pass a whole "
            + "operand, including indirection and an index register, put it in braces, as in `load!({(ptr),y})`.");

    internal static DiagnosticDescriptor WordArgumentNotListed { get; } = Entry(
        "word-argument-not-listed",
        Severity.Error,
        "`{0}` takes one of {1}, and this is {2}",
        "A `one(...)` parameter takes one of the words it lists, and the words are never looked up.");

    internal static DiagnosticDescriptor WordArgumentAmbiguous { get; } = Entry(
        "word-argument-ambiguous",
        Severity.Error,
        "`{0}` takes only {1}, but `{2}` can also be {3}",
        "The argument is a `one(...)` parameter of the enclosing macro, passed on. Which word it holds is known "
            + "only when that macro is called, so every word it allows must be one this parameter accepts. Narrow "
            + "the enclosing parameter's list, or add the missing words to this one.");

    internal static DiagnosticDescriptor IdentArgumentNotAName { get; } = Entry(
        "ident-argument-not-a-name",
        Severity.Error,
        "`{0}` takes a name, and this is not one",
        "An `ident` parameter stands for a name the body declares or writes, so the argument has to be one.");

    internal static DiagnosticDescriptor ExpressionArgumentBraced { get; } = Entry(
        "expression-argument-braced",
        Severity.Error,
        "`{0}` takes an expression, so its argument cannot be in braces: braces pass a whole operand",
        "Braces pass a whole operand, with its addressing mode, to an `operand` parameter. A parameter that takes "
            + "an expression takes it without braces.");

    internal static DiagnosticDescriptor ConstArgumentOutOfRange { get; } = Entry(
        "const-argument-out-of-range",
        Severity.Error,
        "`{0}` takes a constant from {1} to {2}, and {3}",
        "A `const(low..high)` parameter says the range it takes, so a call outside it is told at the call, with "
            + "the limits, rather than by an `.assert` in the body.");

    internal static DiagnosticDescriptor EnumArgumentNotAMember { get; } = Entry(
        "enum-argument-not-a-member",
        Severity.Error,
        "`{0}` takes a member of `{1}`, and {2}",
        "A parameter whose kind is an enum takes one of its members, by its bare name or its path, and nothing "
            + "else of the same value.");

    internal static DiagnosticDescriptor OperandArgumentMode { get; } = Entry(
        "operand-argument-mode",
        Severity.Error,
        "`{0}` takes {1}, and this is `{2}`",
        "An `operand(...)` parameter lists the addressing modes it takes, as `.mode` names them, with `zp`, `zpx` "
            + "and `zpy` for a direct-page address, so a call in another mode is told at the call.");

    internal static DiagnosticDescriptor ParameterRangeInvalid { get; } = Entry(
        "parameter-range-invalid",
        Severity.Error,
        "`{0}` is not a range: a `const` range is two constants, the lower first",
        "The range a `const(low..high)` parameter takes is worked out once, where the macro is declared.");

    internal static DiagnosticDescriptor ParameterKindNotAnEnum { get; } = Entry(
        "parameter-kind-not-an-enum",
        Severity.Error,
        "`{0}` is {1}, and a parameter's kind is one of the kind words or an enum",
        "A name after a parameter's `:` that is not one of the kinds' words names the enum whose members the "
            + "parameter takes.");

    internal static DiagnosticDescriptor OperandModeUnknown { get; } = Entry(
        "operand-mode-unknown",
        Severity.Error,
        "`{0}` is not an addressing mode an `operand` takes: {1}",
        "The modes are the words `.mode` gives, and `zp`, `zpx` and `zpy` for the direct-page addresses among "
            + "`abs`, `absx` and `absy`.");

    internal static DiagnosticDescriptor ComparisonNeverHolds { get; } = Entry(
        "comparison-never-holds",
        Severity.Warning,
        "`{0}` is never `{1}`, so this comparison {2}: {3}",
        "A word compared with `.mode(p)` or with a `one(...)` parameter is not looked up, so a misspelt word, or "
            + "one the parameter can never be, is not an error by itself: the comparison just always gives the "
            + "same answer, and the branch is silently never or always taken. The words `.mode` can give are "
            + "listed in the message; for an `operand(...)` parameter that lists its modes, only those are "
            + "possible, with `zp`, `zpx` and `zpy` reported as `abs`, `absx` and `absy`.");

    internal static DiagnosticDescriptor ExpansionLimit { get; } = Entry(
        "expansion-limit",
        Severity.Error,
        "the expansions in this file come to more than {0} statements, which is as far as nt65 goes",
        "Expansion is bounded, and the bound is far beyond any program written by hand. Reaching it means a "
            + "repetition or a nest of macros is multiplying out further than was meant.");

    internal static DiagnosticDescriptor RepeatTooMany { get; } = Entry(
        "repeat-too-many",
        Severity.Error,
        "this repetition runs {0} times, more than the limit of {1}",
        "nt65 limits how many times a `.repeat` or `.each` body can be written out, which bounds how much one line "
            + "of source can expand to. The limit is far beyond anything written by hand; reaching it usually "
            + "means the count is wrong.");

    private static DiagnosticArea Data { get; } =
        Opens("Data", "Declarations, records, arrays, text and padding.");

    internal static DiagnosticDescriptor ElementCountEmpty { get; } = Entry(
        "element-count-empty",
        Severity.Error,
        "`[]` takes its count from the values given, and there are none: write `{0}[n]` to reserve n elements",
        "`[]` means as many elements as there are values, and no values are given. To reserve room without giving "
            + "values, write the count, as in `.byte[16]`; the elements are filled with zeros.");

    internal static DiagnosticDescriptor ElementCountNotConstant { get; } = Entry(
        "element-count-not-constant",
        Severity.Error,
        "an array's element count must be a constant",
        "How many elements a declaration holds decides how many bytes it takes, so the count has to be known while "
            + "nt65 builds.");

    internal static DiagnosticDescriptor ElementCountNegative { get; } = Entry(
        "element-count-negative",
        Severity.Error,
        "an array's count cannot be negative, and this one is {0}",
        "A count is how many elements the declaration holds, and a negative number of them is not one.");

    internal static DiagnosticDescriptor ElementCountMismatch { get; } = Entry(
        "element-count-mismatch",
        Severity.Error,
        "this array is declared with {0} {1}, but {2} given",
        "A declaration with a count holds exactly that many elements: nt65 neither pads missing values nor drops "
            + "extra ones. Correct the count, or write `[]` to take the count from the values.");

    internal static DiagnosticDescriptor ElementNotAValue { get; } = Entry(
        "element-not-a-value",
        Severity.Error,
        "a `{0}` element is a single value, not a braced record or list",
        "Braces hold a record or a list, and an element of this type is one plain value. Remove the braces.");

    internal static DiagnosticDescriptor ElementNotARecord { get; } = Entry(
        "element-not-a-record",
        Severity.Error,
        "each element of {0} is {1}, written `{{ member = value }}`",
        "Each element of an array of records is written as a record, so that which member each value goes to is "
            + "written down.");

    internal static DiagnosticDescriptor ElementIsOneValue { get; } = Entry(
        "element-is-one-value",
        Severity.Error,
        "each element of `{0}` is a single `{1}` value, not a braced record or list",
        "The member holds elements of a plain type, and each of them is one value. Remove the braces around the "
            + "element.");

    internal static DiagnosticDescriptor MemberUnknown { get; } = Entry(
        "member-unknown",
        Severity.Error,
        "`{0}` has no member `{1}`",
        "A record initializer names the members of the type it initializes.");

    internal static DiagnosticDescriptor MemberGivenTwice { get; } = Entry(
        "member-given-twice",
        Severity.Error,
        "`{0}` is given a value twice: a member is named at most once",
        "A record initializer gives each member at most one value. Remove the duplicate.");

    internal static DiagnosticDescriptor UnionManyMembersGiven { get; } = Entry(
        "union-many-members-given",
        Severity.Error,
        "`{0}` is a union, whose members all start at offset 0, so it takes a value for at most one of them",
        "A union's members share the same bytes, so giving two of them values would write each over the other.");

    internal static DiagnosticDescriptor MemberNeedsARecord { get; } = Entry(
        "member-needs-a-record",
        Severity.Error,
        "`{0}` is a `{1}` record, so its value is written in braces: `{{ member = value }}`",
        "The member is itself a record, so its value is written as one, naming its members.");

    internal static DiagnosticDescriptor MemberNeedsAList { get; } = Entry(
        "member-needs-a-list",
        Severity.Error,
        "`{0}` is an array, which takes a braced list: `{1} = {{ … }}`",
        "The member holds several elements, so its value is a braced list of them.");

    internal static DiagnosticDescriptor MemberTakesOneValue { get; } = Entry(
        "member-takes-one-value",
        Severity.Error,
        "`{0}` holds a single value, so it cannot be given a braced record or list",
        "The member is one value of a plain type. Braces would make its value a record or a list, which it is not; "
            + "remove them.");

    internal static DiagnosticDescriptor MemberCountMismatch { get; } = Entry(
        "member-count-mismatch",
        Severity.Error,
        "`{0}` holds {1} {2}, and this list gives {3}",
        "A member holds exactly as many elements as its type says.");

    internal static DiagnosticDescriptor MemberTextTooLong { get; } = Entry(
        "member-text-too-long",
        Severity.Error,
        "`{0}` has room for {1} bytes, and this text is {2} bytes",
        "Text given to a member must fit in the room the member reserves. Shorten the text or reserve more room.");

    internal static DiagnosticDescriptor MemberNotText { get; } = Entry(
        "member-not-text",
        Severity.Error,
        "`{0}` is one `{1}`, and this text is {2} bytes: text takes a member reserved with `.res`",
        "A member of a plain type holds one value. Room for text is reserved with `.res`, which says how much.");

    internal static DiagnosticDescriptor StrzNotText { get; } = Entry(
        "strz-not-text",
        Severity.Error,
        "`.strz` takes one text: a string, a string constant, a call that returns text, or a charmap applied to one",
        "`.strz` writes text and the zero that ends it, so it takes exactly one text.");

    internal static DiagnosticDescriptor StrzZeroInText { get; } = Entry(
        "strz-zero-in-text",
        Severity.Error,
        "{0}: `.strz` writes the zero that ends it",
        "A zero byte is what ends the text, so a charmap that maps a character to zero would end it in the "
            + "middle.");

    internal static DiagnosticDescriptor TextNotAscii { get; } = Entry(
        "text-not-ascii",
        Severity.Error,
        "text is ASCII outside a charmap; write `\\xHH` for a byte above $7f",
        "Which byte a character above $7f becomes depends on an encoding nt65 does not choose. A charmap says "
            + "what the bytes are, and `\\xHH` writes one directly.");

    internal static DiagnosticDescriptor CharmapValueNotAByte { get; } = Entry(
        "charmap-value-not-a-byte",
        Severity.Error,
        "a charmap maps a character of this text to {0}, which is not a byte: a charmap value must be 0 to 255",
        "A charmap maps each character to one byte, so every value it gives must be 0 to 255. Correct the charmap "
            + "entry.");

    internal static DiagnosticDescriptor FarAddressInWord { get; } = Entry(
        "far-address-in-word",
        Severity.Error,
        "`{0}` is a far address, and `{1}` holds 16 bits: `.faraddr` holds all of it, and `.loword({2})` the low 16 bits",
        "A far address is a bank and a sixteen-bit offset. Writing it into two bytes would drop the bank "
            + "silently, so nt65 asks which was meant.");

    internal static DiagnosticDescriptor AddressDoesNotFit { get; } = Entry(
        "address-does-not-fit",
        Severity.Error,
        "`{0}` is {1} address, and {2}: {3}",
        "The address is wider than the place it is written into, and ca65 would stop with a range error. Write the "
            + "part of the address that fits, as the message suggests.");

    internal static DiagnosticDescriptor AddressNegative { get; } = Entry(
        "address-negative",
        Severity.Error,
        "an address cannot be negative, but this one is {0}",
        "`.addr` and `.faraddr` hold addresses, which are never negative.");

    internal static DiagnosticDescriptor ValueTooWide { get; } = Entry(
        "value-too-wide",
        Severity.Error,
        "{0} does not fit in {1}",
        "The value does not fit the bytes the declaration reserves for it. A wider type, or one of the byte "
            + "operators, says which part was meant.");

    internal static DiagnosticDescriptor ResCountNotConstant { get; } = Entry(
        "res-count-not-constant",
        Severity.Error,
        "a `.res` count must be a constant",
        "How much room is reserved decides where everything after it goes, so it is decided while nt65 builds.");

    internal static DiagnosticDescriptor ResCountOutOfRange { get; } = Entry(
        "res-count-out-of-range",
        Severity.Error,
        "a `.res` count must be between 0 and $ffff, not {0}",
        "`.res` is passed straight to ca65, which reserves at most $ffff bytes in one directive. To reserve more, "
            + "use several `.res`, or a declaration with a type, such as `.byte[n]`, which nt65 writes out as "
            + "several directives.");

    internal static DiagnosticDescriptor AlignBoundaryNotConstant { get; } = Entry(
        "align-boundary-not-constant",
        Severity.Error,
        "an `.align` boundary must be a constant",
        "Where the padding ends is worked out from the boundary, so the boundary is known while nt65 builds.");

    internal static DiagnosticDescriptor AlignBoundaryNotPowerOfTwo { get; } = Entry(
        "align-boundary-not-power-of-two",
        Severity.Error,
        "an `.align` boundary must be a power of two from 1 to $10000, not {0}",
        "ld65 aligns only on powers of two, and nt65 accepts boundaries up to $10000.");

    internal static DiagnosticDescriptor ResNotADeclaration { get; } = Entry(
        "res-not-a-declaration",
        Severity.Error,
        "a named declaration cannot use `.res`: declare it with a type such as `.byte[n]`, which fills with zeros "
            + "where no values are given",
        "In nt65, `.res` is only padding inside a declaration. A named declaration says what its bytes are with a "
            + "type: `buf: .res 16` in ca65 becomes `.data buf: .byte[16]`, which reserves the same room.");

    internal static DiagnosticDescriptor AlignNotADeclaration { get; } = Entry(
        "align-not-a-declaration",
        Severity.Error,
        "`.align` cannot be a named declaration: write it between declarations",
        "An `.align` is padding that positions the next declaration. It has no content of its own to name, so it "
            + "is written on its own between declarations.");

    private static DiagnosticArea Placement { get; } =
        Opens("Placement", "Segments, where a declaration sits and how wide an address is.");

    internal static DiagnosticDescriptor SegmentUndeclared { get; } = Entry(
        "segment-undeclared",
        Severity.Error,
        "segment \"{0}\" is not declared",
        "Every segment is declared once, either in a source file (`.segment NAME: abs`) or in the project file's "
            + "`segments`, with the address size it is reached at. nt65 does not create a segment just because a "
            + "line names it. Declare the segment, or check the spelling of its name.");

    internal static DiagnosticDescriptor SegmentDeclaredTwice { get; } = Entry(
        "segment-declared-twice",
        Severity.Error,
        "segment \"{0}\" is already declared",
        "A segment is declared, with its size, once for the whole program, in one file or in the project, so that "
            + "every file reaches it the same way. After that, `.segment NAME` with no size just switches to it. "
            + "Remove the size from this line, or remove one of the declarations.");

    internal static DiagnosticDescriptor CodeInADataSpace { get; } = Entry(
        "code-in-a-data-space",
        Severity.Error,
        "segment \"{0}\" is in space `{1}`, which holds data, not code for this processor: write another "
            + "processor's code there as data or macro calls",
        "An address space declared as `\"data\"` in the project's `spaces` is another processor's memory, such as "
            + "a sound CPU's RAM. nt65 does not assemble that processor's instructions, so its code is written as "
            + "data, or through macros that write the bytes. A space whose code this program's processor runs is "
            + "declared as `\"code\"`.");

    internal static DiagnosticDescriptor TransferToAnotherSpace { get; } = Entry(
        "transfer-to-another-space",
        Severity.Error,
        "`{0}` targets {1}, in segment \"{2}\" of {3}, but this code runs in {4}: that code belongs to another "
            + "processor",
        "Each address space is a different processor's memory. To this code, a name in another space is only a "
            + "number, such as the address the other processor starts at. A jump, branch or call to it would go to "
            + "that number in this processor's memory, which is something else. Use the value as an immediate or "
            + "in data instead, for example to send it to the other processor.");

    internal static DiagnosticDescriptor OperandInAnotherSpace { get; } = Entry(
        "operand-in-another-space",
        Severity.Error,
        "{0} is in segment \"{1}\" of {2}, and this code runs in {3}: here it can only be used as an immediate "
            + "value or in data",
        "An operand that reads or writes memory reaches this processor's memory, where a name from another address "
            + "space is only a number. Take its value as an immediate (`#<name`) or put it in data. To reach the "
            + "bytes as this processor holds them, before they are sent across, use `.loadof`.");

    internal static DiagnosticDescriptor SpaceUndeclared { get; } = Entry(
        "space-undeclared",
        Severity.Error,
        "space `{0}` is not declared",
        "A segment's `space` names an address space declared in the project file's `spaces`, where each space is "
            + "`\"code\"` (this program's processor runs it) or `\"data\"` (another processor's memory). Declare "
            + "the space there, or check the spelling.");

    internal static DiagnosticDescriptor SpaceNotAName { get; } = Entry(
        "space-not-a-name",
        Severity.Error,
        "`space` takes the name of an address space",
        "`space = spc` puts the segment in the address space `spc`, which the project's `spaces` declares. It is a "
            + "plain name, not a number or an expression; in the project file it is a non-empty string.");

    internal static DiagnosticDescriptor SegmentStandardSize { get; } = Entry(
        "segment-standard-size",
        Severity.Error,
        "\"{0}\" is a standard segment and is always `{1}`",
        "The standard segments `CODE`, `RODATA`, `DATA`, `BSS` and `ZEROPAGE` are predeclared with the size ca65 "
            + "gives them in every object file. One may be declared once more to give it a direct page, a bank or "
            + "mirrors, but only at that size. Use a segment of your own for a different size.");

    internal static DiagnosticDescriptor SegmentAttributeTwice { get; } = Entry(
        "segment-attribute-twice",
        Severity.Error,
        "segment \"{0}\" already has a `{1}`",
        "Each of a segment's attributes (`dp`, `bank`, `mirrors`, `space`) takes one value, so it is given once. "
            + "List every mirrored bank in a single `mirrors = [...]`.");

    internal static DiagnosticDescriptor SegmentDpNotZp { get; } = Entry(
        "segment-dp-not-zp",
        Severity.Error,
        "segment \"{0}\" is not `zp`, and only a `zp` segment takes a `dp`",
        "`dp` gives the direct-page base (the 65816's D register) that a `zp` segment's symbols are reached "
            + "through. Symbols in any other segment are reached by absolute or long addresses, so `dp` means "
            + "nothing there. Declare the segment `zp`, or remove the `dp`.");

    internal static DiagnosticDescriptor SegmentAttributeNotConstant { get; } = Entry(
        "segment-attribute-not-constant",
        Severity.Error,
        "`{0}` must be a constant",
        "A segment's `dp` and `bank` decide how every reference into the segment is checked and encoded, so they "
            + "must be numbers nt65 can work out while it builds, not addresses the linker decides.");

    internal static DiagnosticDescriptor SegmentAttributeOutOfRange { get; } = Entry(
        "segment-attribute-out-of-range",
        Severity.Error,
        "{0}",
        "The direct page is a 16-bit address, from $0000 to $ffff, and a bank is one byte, from $00 to $ff.");

    internal static DiagnosticDescriptor SegmentMirrorInvalid { get; } = Entry(
        "segment-mirror-invalid",
        Severity.Error,
        "each mirror must be a constant bank, or a range of banks written lower first, such as `$00..$3f`",
        "`mirrors = [...]` lists the other banks in which the segment's home bank also appears. Each item is a "
            + "constant bank from $00 to $ff, or a range of them written with the lower bank first, such as "
            + "`$00..$3f`.");

    internal static DiagnosticDescriptor SegmentMirrorsNeedABank { get; } = Entry(
        "segment-mirrors-need-a-bank",
        Severity.Error,
        "segment \"{0}\" has `mirrors` but no `bank`: mirrors repeat a home bank, so give the segment a `bank`",
        "`mirrors` lists other banks where the segment's home bank also appears, so the segment needs a home bank, "
            + "given with `bank = ...`.");

    internal static DiagnosticDescriptor SegmentBlockRedundant { get; } = Entry(
        "segment-block-redundant",
        Severity.Error,
        "this block names \"{0}\", the segment it is already in, so it moves nothing: its contents stay inline, "
            + "where execution falls into them",
        "A nested segment block moves its contents out of line into another segment, so that execution does not "
            + "fall through into them. A block that names the segment it is already in moves nothing, and its "
            + "contents stay where the code above runs into them. Name a different segment, or remove the block.");

    internal static DiagnosticDescriptor OutsideEverySegment { get; } = Entry(
        "outside-every-segment",
        Severity.Error,
        "{0} is not in any segment: put a `.segment NAME` line above it, or place it in a `.segment NAME` block",
        "Every byte goes in a segment, and the linker decides where each segment goes. Unlike ca65, which starts "
            + "in `CODE`, nt65 has no default segment, and nothing is placed just by being written first. Put a "
            + "`.segment` line, such as `.segment CODE`, above it.");

    internal static DiagnosticDescriptor InstructionInData { get; } = Entry(
        "instruction-in-data",
        Severity.Error,
        "{0} in a `.proc`, not in a `.data` declaration",
        "A `.data` declaration holds only data. Instructions go in a routine (`.proc`), where the flow analysis "
            + "can follow them. Opcodes written out by hand belong in data as `.byte` values.");

    internal static DiagnosticDescriptor InstructionOutsideARoutine { get; } = Entry(
        "instruction-outside-a-routine",
        Severity.Error,
        "{0} in a `.proc`: nt65 follows and checks code only inside routines",
        "Flow analysis works routine by routine, so code outside a `.proc` could never be followed or checked. Put "
            + "the code in a `.proc`, or, if the bytes are data, declare them with `.data`.");

    internal static DiagnosticDescriptor PaddingOutsideARoutine { get; } = Entry(
        "padding-outside-a-routine",
        Severity.Error,
        "`{0}` outside a `.proc` must be part of a `.data` declaration{1}",
        "Outside a routine, every byte belongs to a named `.data` declaration, such as `.data table: .byte 1, 2, "
            + "3`, which gives the bytes a name and a size. Only an unnamed `.res` or `.align` may stand on its "
            + "own, as padding between declarations.");

    internal static DiagnosticDescriptor FarNeeds65816 { get; } = Entry(
        "far-needs-65816",
        Severity.Error,
        "{0} is declared `far`, and a far address needs the 65816",
        "A far address is a 24-bit bank and offset, which only the 65816 has. ca65 rejects `far` on every other "
            + "processor, so nt65 reports it where it is written rather than writing output ca65 would reject. "
            + "Declare the segment or import `abs` or `zp`, or build for the 65816.");

    private static DiagnosticArea Instructions { get; } =
        Opens("Instructions", "Mnemonics, operands, addressing modes and branch range.");

    internal static DiagnosticDescriptor InstructionNotOnCpu { get; } = Entry(
        "instruction-not-on-cpu",
        Severity.Error,
        "`{0}` is not available on the {1}{2}",
        "The program is built for one processor, and this instruction is not in its set. Where another processor "
            + "nt65 knows has it, the message says which.");

    internal static DiagnosticDescriptor OperandMissing { get; } = Entry(
        "operand-missing",
        Severity.Error,
        "`{0}` needs an operand",
        "This instruction has no form without an operand, so it needs one.");

    internal static DiagnosticDescriptor OperandNotTaken { get; } = Entry(
        "operand-not-taken",
        Severity.Error,
        "`{0}` does not take this operand on the {1}",
        "The instruction exists on this processor, but not with this addressing mode; for example, `lda (ptr)` "
            + "without `,y` needs a 65C02. Check the operand, or the processor the program is built for.");

    internal static DiagnosticDescriptor OperandIsText { get; } = Entry(
        "operand-is-text",
        Severity.Error,
        "`{0}` is a string, and an instruction's operand must be a number or an address",
        "An instruction's operand is a number or an address. A string is a sequence of bytes, which a data "
            + "directive writes into a `.data` declaration.");

    internal static DiagnosticDescriptor OperandHasNoNextByte { get; } = Entry(
        "operand-has-no-next-byte",
        Severity.Error,
        "{0} needs a later byte of `{1}`, and `{1}` is a `{2}` operand here, which has none",
        "In a macro body, `p + n` or `.byteof` on an operand parameter addresses a byte after the one the argument "
            + "names, which works only when the argument is a plain address, indexed or not. An immediate, the "
            + "accumulator, an indirect operand or a stack-relative one has no later byte to address. Pass a plain "
            + "address at this call, or change the macro body.");

    internal static DiagnosticDescriptor BranchOperandNotTaken { get; } = Entry(
        "branch-operand-not-taken",
        Severity.Error,
        "`{0}` takes only a target address as its operand",
        "A long branch such as `jeq` or `jne` takes one target address, as a short branch does, and has no other "
            + "addressing mode.");

    internal static DiagnosticDescriptor TransferPrefix { get; } = Entry(
        "transfer-prefix",
        Severity.Error,
        "`{0}` does not take an address-size prefix: a jump, branch or call is sized by its target",
        "An address-size prefix (`z:`, `a:`, `f:`) chooses how wide an operand is read. A jump, branch or call is "
            + "sized by its target and its mnemonic instead: `jmp` or `jml`, `jsr` or `jsl`. Remove the prefix, "
            + "and use the mnemonic for the distance you mean.");

    internal static DiagnosticDescriptor TargetTooFar { get; } = Entry(
        "target-too-far",
        Severity.Error,
        "`{0}` reaches only a near target, in the current bank, and this one is far",
        "`jmp`, `jsr`, the branches and `per` reach an address within the current bank, and this target is in "
            + "another bank. On the 65816 use the long form: `jml` instead of `jmp`, or `jsl` instead of `jsr` for "
            + "a routine that returns with `rtl`.");

    internal static DiagnosticDescriptor TargetTooNear { get; } = Entry(
        "target-too-near",
        Severity.Error,
        "`{0}` is for a far target, and this one is {1}: use `{2}`",
        "`jml` and `jsl` take a three-byte address and are for targets in another bank. This target is in reach of "
            + "the shorter form, which is smaller and faster. A constant address is taken as written, so `jml "
            + "$008000` is allowed.");

    internal static DiagnosticDescriptor BranchOutOfReach { get; } = Entry(
        "branch-out-of-reach",
        Severity.Error,
        "`{0}` would branch {1} bytes, and a branch reaches only -128 to 127{2}",
        "A branch stores its target as one signed byte counted from the instruction after it, so it reaches 128 "
            + "bytes back or 127 forward. Reverse the condition and branch over a `jmp`, or use the long branch "
            + "(`jeq`, `jne` and so on), which nt65 writes as the short branch where it reaches and as a branch "
            + "over a `jmp` where it does not.");

    internal static DiagnosticDescriptor AddressingModeMissing { get; } = Entry(
        "addressing-mode-missing",
        Severity.Error,
        "`{0}` has no {1} form of this operand on the {2}: remove the address-size prefix",
        "An address-size prefix (`z:`, `a:`, `f:`) asks for one particular form of the instruction, and on this "
            + "processor the instruction has no form of that width for this operand. nt65 does not silently drop a "
            + "prefix it cannot honour. Remove the prefix, or write the operand in a shape that has that form.");

    internal static DiagnosticDescriptor AddressingModeTooNarrow { get; } = Entry(
        "addressing-mode-too-narrow",
        Severity.Error,
        "`{0}` has only a {1} form of this operand, and `{2}` is {3}",
        "This operand shape exists only in a narrower form: `(ptr),y`, for example, takes a zero-page pointer. The "
            + "address named is wider, so it would be cut down to its low byte. Put what it names in a `zp` "
            + "segment, or use a different addressing mode.");

    internal static DiagnosticDescriptor AddressSizeUnreachable { get; } = Entry(
        "address-size-unreachable",
        Severity.Error,
        "`{0}` cannot reach a {1} address on the {2}",
        "No form of this instruction on this processor takes an address as wide as this operand, such as a far "
            + "address on a processor without long addressing. Move what it names to a segment the instruction can "
            + "reach, or reach it another way.");

    internal static DiagnosticDescriptor ImmediateTooWide { get; } = Entry(
        "immediate-too-wide",
        Severity.Error,
        "{1} does not fit: {0}",
        "An immediate is one byte, or, on the 65816, two bytes when the register it goes to is 16-bit at this "
            + "point, as the analysis tracks it from `rep`, `sep` and the routine's signature. Use a value that "
            + "fits, take one byte of it with `<` or `>`, or widen the register.");

    internal static DiagnosticDescriptor DirectPageNeeds65816 { get; } = Entry(
        "direct-page-needs-65816",
        Severity.Error,
        "`d:` needs the 65816, and this program is built for the {0}: use `z:` for a zero-page address",
        "`d:` marks an address reached through the 65816's direct page, which can be moved with the D register. "
            + "Earlier processors have a fixed zero page, which `z:` asks for.");

    internal static DiagnosticDescriptor DirectPagePrefixOnSymbol { get; } = Entry(
        "direct-page-prefix-on-symbol",
        Severity.Error,
        "`d:` takes only a constant address: to reach a symbol through the direct page, declare it in a `zp` segment",
        "Whether a symbol is reached through the direct page is decided by its segment: symbols in a `zp` segment "
            + "are, with the segment's `dp` as the base. That way, moving a declaration between segments does not "
            + "mean editing every line that uses it. `d:` is for a literal address, such as `d:$05`.");

    internal static DiagnosticDescriptor DirectPageFormMissing { get; } = Entry(
        "direct-page-form-missing",
        Severity.Error,
        "`{0}` has no direct-page form of this operand, which `d:` asks for",
        "`d:` asks for the one-byte direct-page form of the instruction, and the instruction has no such form with "
            + "this operand; `lda` has no `dp,y` form, for example. Remove the `d:`, or use an addressing mode "
            + "that has a direct-page form.");

    internal static DiagnosticDescriptor DirectPageOnly { get; } = Entry(
        "direct-page-only",
        Severity.Error,
        "`{0}` is in \"{1}\", reached through the direct page at {2}, so it works only as a direct-page operand: "
            + "as {3} operand it would address its offset in the data bank instead",
        "A symbol in a `zp` segment with a nonzero `dp` is an offset from the direct page. As a direct-page "
            + "operand it reaches the direct page plus that offset. As an absolute or long operand, whether forced "
            + "with a prefix or because the instruction has no direct-page form with this index (`lda dp,y`, for "
            + "example), it would reach the same offset in the data bank, a different address. Use a direct-page "
            + "addressing mode.");

    internal static DiagnosticDescriptor AssertionFailed { get; } = Entry(
        "assertion-failed",
        Severity.Error,
        "{0}",
        "The `.assert` condition is false. nt65 checks an assertion as soon as it can evaluate the condition; one "
            + "that depends on final addresses is passed on for the linker to check.");

    internal static DiagnosticDescriptor ConfigRefused { get; } = Entry(
        "config-refused",
        Severity.Error,
        "{0}",
        "The build reached an `.error` directive, usually inside an `.if` that rejects a configuration the file "
            + "does not support. The message is the file's own text.");

    internal static DiagnosticDescriptor ConfigWarned { get; } = Entry(
        "config-warned",
        Severity.Warning,
        "{0}",
        "The build reached a `.warning` directive. The message is the file's own text, and the build carries on.");

    private static DiagnosticArea ControlFlow { get; } =
        Opens("Control flow", "Where execution goes, and the annotations the analysis needs where it cannot see.");

    internal static DiagnosticDescriptor AnnotationAboutNothing { get; } = Entry(
        "annotation-about-nothing",
        Severity.Error,
        "`{0}` applies to the statement above it, and there is none",
        "An annotation such as `.next` or `.patch` is written directly after the statement it describes, so it "
            + "reads next to it. Here there is no statement above it to apply to. Move it to just below the "
            + "statement it is about.");

    internal static DiagnosticDescriptor CodeUnreachable { get; } = Entry(
        "code-unreachable",
        Severity.Warning,
        "this code is never reached: execution does not fall into a nested segment block, so start it with a label "
            + "that is jumped to, named by a `.next`, or declared by a `.state`",
        "A nested segment block's bytes are placed in another segment, away from the code around them, so "
            + "execution never falls into them from above. Code at the start of the block is reached only through "
            + "a label: one something branches, jumps or calls to, one a `.next` names, or one a `.state` declares "
            + "as an entry point. Without one, the code is assembled but never runs.");

    internal static DiagnosticDescriptor LabelUnreachable { get; } = Entry(
        "label-unreachable",
        Severity.Warning,
        "`{0}` is never reached: no code falls into it and nothing refers to it",
        "The code above the label does not fall through into it, and nothing branches, jumps or calls to it or "
            + "takes its address. If it is reached in a way nt65 cannot see, write a `.state` after the label to "
            + "declare it an entry point; otherwise the code is dead and can be removed.");

    internal static DiagnosticDescriptor RunsIntoData { get; } = Entry(
        "runs-into-data",
        Severity.Error,
        "the instruction above falls through into this data: add a `.next` after the data saying where flow goes "
            + "instead",
        "Execution falls from the instruction above into these bytes, so the processor would run them as code, as "
            + "in the `.byte $2c` skip trick or an opcode written out as bytes. nt65 cannot follow flow through "
            + "data, so write a `.next` after the data naming where flow really goes. If the instruction above is "
            + "a conditional branch that is always taken, put a `.next` naming its target under the branch instead.");

    internal static DiagnosticDescriptor RoutineRunsOffTheEnd { get; } = Entry(
        "routine-runs-off-the-end",
        Severity.Warning,
        "`{0}` runs off {1} into whatever {2}: {3}, or write `.next ?` to end the path",
        "The routine's last instruction does not return, jump or branch away, so execution carries on into "
            + "whatever the linker puts after it. Usually an `rts`, `rtl` or `jmp` is missing. Where running on "
            + "was meant, end the body with `.fallthrough NAME`, naming the routine it runs into; nt65 checks that "
            + "NAME starts where this routine ends, and checks it as a tail call. `.next ?` ends the path without "
            + "claiming anything.");

    internal static DiagnosticDescriptor NextTargetNotCode { get; } = Entry(
        "next-target-not-code",
        Severity.Error,
        "`{0}` is {1}, and `{2}` must name a code label, a routine, or a table of them",
        "`.next` and `.patch` name places in code: a label, a routine, or a list or table of addresses holding "
            + "them. What is named here is not somewhere flow can go.");

    internal static DiagnosticDescriptor NextTableHasNoLabels { get; } = Entry(
        "next-table-has-no-labels",
        Severity.Error,
        "table `{0}` holds no code labels for `.next` to follow",
        "A `.next` may name a table of addresses, and flow then goes to each code label the table holds. This "
            + "table holds none, so it says nothing about where flow goes. Name the labels directly, or fill the "
            + "table with code labels.");

    internal static DiagnosticDescriptor NextTargetNotATable { get; } = Entry(
        "next-target-not-a-table",
        Severity.Error,
        "`{0}` is not a table of addresses: `.next` can name data only when it is declared with `.addr` or "
            + "`.faraddr`",
        "A `.next` that names data follows the code labels the data holds, so the data must be a table of "
            + "addresses, declared with `.addr` or `.faraddr`.");

    internal static DiagnosticDescriptor NextSuccessorsKnown { get; } = Entry(
        "next-successors-known",
        Severity.Error,
        "`.next` is not allowed here: nt65 already knows that {0} {1}{2}",
        "`.next` tells nt65 where flow goes after a statement it cannot follow by itself: an indirect jump or "
            + "call, an `rts` or `rtl` used as a jump, a jump to a computed address, or data that execution falls "
            + "into. After a conditional branch it may name the branch's own target, to say the branch is always "
            + "taken. After any other statement nt65 already knows where flow goes, and a `.next` could only "
            + "contradict it. To say a routine runs into the one written after it, use `.fallthrough`; `.next ?` "
            + "may end a path after any statement.");

    internal static DiagnosticDescriptor NextNotTheBranchTarget { get; } = Entry(
        "next-not-the-branch-target",
        Severity.Error,
        "`.next` after {0} can name only the branch's own target, `{1}`, to say the branch is always taken",
        "Under a conditional branch, a `.next` says the branch is always taken, because the flags are known there, "
            + "so flow never continues past it. It must then name exactly the branch's own target; naming anything "
            + "else would claim the branch goes somewhere its operand does not. To end the path there instead, "
            + "write `.next ?`.");

    internal static DiagnosticDescriptor FallthroughMisplaced { get; } = Entry(
        "fallthrough-misplaced",
        Severity.Error,
        "`.fallthrough` must be the last line of a `.proc` body, once `.if` conditions are resolved",
        "`.fallthrough NAME` says that every path reaching the end of the routine runs on into routine NAME, so it "
            + "is about the end of the body, not the statement above it. It stands last in a `.proc` body, or last "
            + "in a branch of an `.if` chain that is itself last in the body, to any depth; conditions are settled "
            + "before analysis, so in each build at most one remains, and it is last. It may not appear in a macro "
            + "body, a block argument or a repetition, and nothing may follow it or the chain it ends.");

    internal static DiagnosticDescriptor FallthroughNotARoutine { get; } = Entry(
        "fallthrough-not-a-routine",
        Severity.Error,
        "`{0}` is {1}, not a routine: `.fallthrough` names the routine execution runs into",
        "`.fallthrough` says this routine runs on into the first byte of another routine, so it must name a `.proc`.");

    internal static DiagnosticDescriptor FallthroughNotAdjacent { get; } = Entry(
        "fallthrough-not-adjacent",
        Severity.Error,
        "`{0}` does not start where this routine ends: `.fallthrough` can only name the routine written directly "
            + "after it in the same segment",
        "A routine that reaches its end runs on into whatever comes next in its segment: the next thing the file "
            + "writes to that segment, even when regions of other segments come between in the text, as ca65 lays "
            + "the bytes out. `.fallthrough` must name that routine; naming any other would claim something the "
            + "bytes do not do. Move the routines so the named one comes directly after, or end this one with a "
            + "`jmp`.");

    internal static DiagnosticDescriptor FallthroughOtherSegment { get; } = Entry(
        "fallthrough-other-segment",
        Severity.Error,
        "`{0}` is in segment \"{2}\", and this routine ends in \"{1}\": a routine can only run into what comes "
            + "next in its own segment",
        "A segment's bytes are laid out in the order they are written, whatever other segments are written in "
            + "between, so the end of a routine is followed by the next thing in its own segment. A routine in "
            + "another segment cannot be what it runs into. Across a `.place`, what comes next is the placed "
            + "module's first routine in this segment, or what the placing file writes next in it.");

    internal static DiagnosticDescriptor FallthroughNotPlaced { get; } = Entry(
        "fallthrough-not-placed",
        Severity.Error,
        "`{0}` is in module `{1}`, and the two modules are separate translation units, whose order only the linker "
            + "knows: use `.place` to lay one out inside the other",
        "Across translation units the order of the bytes is decided at link time, which nt65 does not see, so it "
            + "cannot check that one routine runs into another. Where one module places the other with `.place` "
            + "(the other declared `placed`), both are laid out in one output file, and running into the other "
            + "module's routine is checked as it is within a file.");

    internal static DiagnosticDescriptor IndirectCallUnchecked { get; } = Entry(
        "indirect-call-unchecked",
        Severity.Error,
        "{0} is an indirect call, which nt65 cannot follow: add a `.next` naming the routines it may call",
        "The call goes to an address read at run time, so the analysis cannot tell which routine it reaches or "
            + "check the call. Write a `.next` after it naming the routines it may call, or the table that holds "
            + "them; each is then checked as a call.");

    internal static DiagnosticDescriptor IndirectJumpUnchecked { get; } = Entry(
        "indirect-jump-unchecked",
        Severity.Error,
        "{0} is an indirect jump, which nt65 cannot follow: add a `.next` naming the labels it may reach, or "
            + "`.next ?` to end the path",
        "The jump goes to an address read at run time. A `.next` after it names the labels it may reach, or the "
            + "jump table that holds them, and the analysis carries on at each. `.next ?` says the path ends here, "
            + "and nothing beyond it is checked.");

    internal static DiagnosticDescriptor ComputedJumpUnchecked { get; } = Entry(
        "computed-jump-unchecked",
        Severity.Error,
        "{0} jumps to a computed address, which nt65 cannot follow: add a `.next` naming the labels it may reach, "
            + "or `.next ?` to end the path",
        "The target is an expression rather than a label, so the analysis has no label at which to carry on. A "
            + "`.next` after the jump names the labels it may reach. Where the target is not the start of an "
            + "instruction, such as a jump into the middle of one, write `.next ?` to end the path there.");

    internal static DiagnosticDescriptor PushedReturnUnchecked { get; } = Entry(
        "pushed-return-unchecked",
        Severity.Error,
        "`{0}` returns to an address pushed in this block, so it is really a jump: add a `.next` naming where it "
            + "goes",
        "The block pushes an address and then returns to it, the RTS trick, so the return is a jump in disguise "
            + "and nt65 cannot tell where it goes. A `.next` after it names the labels it may reach.");

    internal static DiagnosticDescriptor JumpTargetNotALabel { get; } = Entry(
        "jump-target-not-a-label",
        Severity.Error,
        "{0} goes to `{1}`, which is {2}, not a label, so nt65 cannot follow it: add a `.next` naming the labels "
            + "it reaches, or `.next ?` to end the path",
        "A jump or branch normally names a label in code. This one names something else, such as a constant, so "
            + "the analysis cannot tell where flow goes. A `.next` after it names the labels it reaches, or `.next "
            + "?` ends the path.");

    internal static DiagnosticDescriptor JumpIntoData { get; } = Entry(
        "jump-into-data",
        Severity.Error,
        "`{0}` labels data, and this jumps to it: add a `.state` after the label and a `.next` after the data "
            + "saying where flow goes",
        "The label marks data, so the processor would run those bytes as code. A `.state` after the label declares "
            + "it an entry point and what the processor state is there, and a `.next` after the data says where "
            + "flow goes from there.");

    internal static DiagnosticDescriptor EntryNotDeclared { get; } = Entry(
        "entry-not-declared",
        Severity.Error,
        "`{0}` is inside routine `{1}`: to jump into another routine, declare the label an entry point with a "
            + "`.state` after it",
        "A jump into the middle of another routine arrives where that routine's own analysis never sees it. A "
            + "`.state` directly after the label declares it an entry point and what the processor state is there, "
            + "and both the jump and the routine are then checked against it.");

    internal static DiagnosticDescriptor ExportedEntryNotDeclared { get; } = Entry(
        "exported-entry-not-declared",
        Severity.Error,
        "`{0}` is inside routine `{1}`, and exporting it lets other modules jump into it: declare it an entry "
            + "point with a `.state` after the label",
        "An exported label inside a routine lets other modules jump into the middle of it, and this module's "
            + "analysis never sees those jumps arrive. A `.state` after the label declares it an entry point and "
            + "what the processor state is there, so the routine is checked from it.");

    internal static DiagnosticDescriptor CodeLabelAsData { get; } = Entry(
        "code-label-as-data",
        Severity.Error,
        "the address of code label `{0}` is taken here, so it may be jumped to where nt65 cannot see: add a "
            + "`.state` after the label, or name it in a `.next` in `{1}`",
        "Taking the address of an instruction, in a table, a `pea` or an immediate, means something may later jump "
            + "to it indirectly, where the analysis cannot follow. Declare the label an entry point with a "
            + "`.state` after it, or name it in a `.next` on the indirect jump in its own routine, so the analysis "
            + "follows flow to it.");

    internal static DiagnosticDescriptor SelfModifyingUnchecked { get; } = Entry(
        "self-modifying-unchecked",
        Severity.Error,
        "{0} modifies the instruction at `{1}`: add `.patch {2}` after it to mark the self-modifying code",
        "The store writes into an instruction's bytes, so that instruction does not do what its text says. A "
            + "`.patch` naming the instruction's label, written after the store, acknowledges it and tells a "
            + "reader that the instruction is changed at run time.");

    internal static DiagnosticDescriptor HandlerCalled { get; } = Entry(
        "handler-called",
        Severity.Error,
        "`{0}` is an interrupt handler and cannot be called: it returns with `rti`, which would not return to the "
            + "caller",
        "The processor enters an interrupt handler by pushing the return address and the status flags, and the "
            + "handler leaves with `rti`, which pulls both. A call pushes only the return address, so the `rti` "
            + "would pull the wrong bytes and not return to the caller.");

    internal static DiagnosticDescriptor HandlerReturnsNotRti { get; } = Entry(
        "handler-returns-not-rti",
        Severity.Error,
        "`{0}` is an interrupt handler and must return with `rti`, not `{1}`",
        "The processor pushed the status flags when it entered the handler, and only `rti` pulls them. An `rts` or "
            + "`rtl` would leave the flags on the stack and return to the wrong address.");

    internal static DiagnosticDescriptor NoreturnReturns { get; } = Entry(
        "noreturn-returns",
        Severity.Error,
        "`{0}` is declared `noreturn`, but `{1}` returns from it",
        "The routine's signature says it never returns, and its callers are checked on that promise: nothing after "
            + "a call to it is expected to run. Leave it some other way, such as a `jmp`, or remove `noreturn` "
            + "from its signature.");

    internal static DiagnosticDescriptor InlineDataMissing { get; } = Entry(
        "inline-data-missing",
        Severity.Error,
        "`{0}` expects {1} written after each call, and {2}",
        "The routine's `inline` signature item says each call is followed by data, which the routine reads and "
            + "returns past. This call is not followed by that data, so the routine would skip over the wrong "
            + "bytes. Write the data directly after the call, in the same segment and with no label between.");

    internal static DiagnosticDescriptor InlineCountNotConstant { get; } = Entry(
        "inline-count-not-constant",
        Severity.Error,
        "`{0}` has `{1}`, and the byte count it gives must be a constant",
        "How many bytes the routine skips after each call decides where flow carries on, so the count in `inline "
            + "N` must be a number nt65 knows while it builds, and not negative.");

    internal static DiagnosticDescriptor TailCallToHandler { get; } = Entry(
        "tail-call-to-handler",
        Severity.Error,
        "{0} is a tail call into interrupt handler `{1}`, whose `rti` would not return to this routine's caller: "
            + "only a `noreturn` routine or another interrupt handler may jump to one",
        "A tail call is a jump that leaves the callee to return to this routine's caller. An interrupt handler "
            + "returns with `rti`, which pulls the status flags as well as the address, and nothing pushed them, "
            + "so it would not return properly. Call the handler's code some other way, or jump to it only from "
            + "another handler or a `noreturn` routine.");

    internal static DiagnosticDescriptor TailCallDistanceMismatch { get; } = Entry(
        "tail-call-distance-mismatch",
        Severity.Error,
        "{0} is a tail call, but `{1}` is {2} and `{3}` is {4}, so `{1}` would return the wrong way to `{3}`'s "
            + "caller",
        "In a tail call the callee returns directly to this routine's caller. A near routine returns with `rts` "
            + "and a far one with `rtl`, so the callee must be the same distance as this routine, or the caller "
            + "would get the wrong kind of return. Call it with `jsr` or `jsl` and return normally instead.");

    internal static DiagnosticDescriptor KeepsBroken { get; } = Entry(
        "keeps-broken",
        Severity.Error,
        "`{0}` promises `keeps {1}`, but {2} {3} not the same as on entry here{4}",
        "The routine's signature says it keeps these registers: every path that leaves it returns them holding the "
            + "value they had on entry, and callers rely on that. On this path the analysis sees a register "
            + "changed and not restored. Save and restore it, for example with `pha` and `pla`, or, where it is "
            + "restored in a way the analysis cannot see, write `.state keeps REG` at that point.");

    internal static DiagnosticDescriptor KeepsRedundant { get; } = Entry(
        "keeps-redundant",
        Severity.Warning,
        "`{0}` is redundant here: {1} already holds its value from entry",
        "The analysis already knows the register holds the value it had when the routine was entered, so the "
            + "`.state keeps` adds nothing. Remove it.");

    private static DiagnosticArea ProcessorState { get; } =
        Opens(
            "Processor state",
            "65816 register widths, emulation mode, direct page, data bank, stack frames, calls and returns.");

    internal static DiagnosticDescriptor WidthUnknown { get; } = Entry(
        "width-unknown",
        Severity.Error,
        "`{0} #` needs the width of {1}, and {2}",
        "On the 65816 an immediate operand such as `lda #` or `ldx #` is one byte or two depending on the M or X "
            + "flag, so nt65 has to know how wide A, or X and Y, is on that line. Here it does not: the paths that "
            + "reach the line disagree, something the analysis cannot follow changed the flags, the routine's "
            + "signature says `a*` or `i*`, or no path reaches the line at all. Put `.ensure a8`, `a16`, `i8` or "
            + "`i16` before the line to set the width, write a `.state` after a label to declare it, or give the "
            + "width in the routine's signature.");

    internal static DiagnosticDescriptor ImmediateInEmulation { get; } = Entry(
        "immediate-in-emulation",
        Severity.Error,
        "`{0} #` has a 16-bit width here, but the processor is in emulation mode, where A, X and Y are always 8-bit",
        "In emulation mode the processor forces A, X and Y to 8 bits whatever the M and X flags say, so an "
            + "immediate is always one byte. The analysis finds a 16-bit width and emulation mode on the same "
            + "path, which cannot both be true. Check the signature or `.state` that declared the 16-bit width, or "
            + "the `xce` that entered emulation mode.");

    internal static DiagnosticDescriptor WidthInEmulation { get; } = Entry(
        "width-in-emulation",
        Severity.Error,
        "{0} is impossible in emulation mode, where A, X and Y are always 8-bit",
        "In emulation mode the processor forces A, X and Y to 8 bits whatever the M and X flags say, so a `.state` "
            + "or signature that declares `emu` together with `a16` or `i16` describes a state the processor "
            + "cannot be in. Declare `a8` and `i8`, or leave the widths out: `emu` already implies them.");

    internal static DiagnosticDescriptor EnsureNeedsNative { get; } = Entry(
        "ensure-needs-native",
        Severity.Error,
        "`.ensure {0}` needs native mode, and {1}",
        "`.ensure a16` and `.ensure i16` write a `rep`, and 16-bit widths exist only in native mode: in emulation "
            + "mode A, X and Y stay 8-bit whatever `rep` does. Switch to native mode (`clc` then `xce`) before the "
            + "`.ensure`, or, where the analysis cannot see the mode, declare it with `native` in the routine's "
            + "signature or a `.state native`.");

    internal static DiagnosticDescriptor EnsureItemNotAWidth { get; } = Entry(
        "ensure-item-not-a-width",
        Severity.Error,
        "`.ensure` takes only `a8`, `a16`, `i8` and `i16`, not `{0}`",
        "`.ensure` writes the `rep` or `sep` that makes a register width true, and widths are the only part of the "
            + "processor state it can set that way. To declare anything else at a point, such as the mode, D or B, "
            + "use `.state`.");

    internal static DiagnosticDescriptor StateItemNotAPoint { get; } = Entry(
        "state-item-not-a-point",
        Severity.Error,
        "`{0}` describes a whole routine, not one point in it: write it in the routine's signature, not in `.state`",
        "A `.state` declares the processor state at one line: register widths, mode, D and B. Items that describe "
            + "the routine as a whole, such as `near`, `far`, `args`, `inline`, `interrupt`, `noreturn`, a "
            + "signature set, or a `*` item meaning unchanged since entry, belong in the signature after the "
            + "routine's name.");

    internal static DiagnosticDescriptor StateOutsideARoutine { get; } = Entry(
        "state-outside-a-routine",
        Severity.Error,
        "`{0}` is only allowed inside a `.proc`: it describes a point in a routine's code",
        "`.state`, `.ensure` and `.frame` describe or change the processor state or the stack at one point in a "
            + "routine's code, and the analysis follows that state only inside a `.proc`. Move the directive into "
            + "the routine it belongs to.");

    internal static DiagnosticDescriptor StateModeMismatch { get; } = Entry(
        "state-mode-mismatch",
        Severity.Error,
        "`.state {0}` does not match: the processor is in {1} mode here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the processor is in the other mode. Fix the code on that path, "
            + "or correct the `.state`.");

    internal static DiagnosticDescriptor StateWidthMismatch { get; } = Entry(
        "state-width-mismatch",
        Severity.Error,
        "`.state {0}` does not match: {1} {2} {3} here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the register has the other width. Fix the code on that path, "
            + "for example with `.ensure`, or correct the `.state`.");

    internal static DiagnosticDescriptor StateValueMismatch { get; } = Entry(
        "state-value-mismatch",
        Severity.Error,
        "`.state {0}` does not match: {1} is {2} here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the direct page register D, or the data bank register B, holds "
            + "a different value. Fix the code on that path, or correct the `.state`.");

    internal static DiagnosticDescriptor StateValueNotConstant { get; } = Entry(
        "state-value-not-constant",
        Severity.Error,
        "`.state {0}` needs a constant, because the analysis tracks {1} as an exact value",
        "The analysis tracks the direct page register D and the data bank register B as exact values, to check "
            + "each direct-page and absolute operand against them. So the value in `.state dp = ...` or `.state "
            + "dbr = ...` must be a constant expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor StateValueOutOfRange { get; } = Entry(
        "state-value-out-of-range",
        Severity.Error,
        "`.state {0}` is out of range: {1}",
        "The direct page register D holds a 16-bit address, $0000 to $ffff, and the data bank register B holds one "
            + "byte, $00 to $ff.");

    internal static DiagnosticDescriptor CallStateMismatch { get; } = Entry(
        "call-state-mismatch",
        Severity.Error,
        "{0} needs `{1}`, but {2}",
        "Every call is checked against the entry state in the called routine's signature, not against its body. "
            + "Here the state at the call differs from what that signature requires. Change the state before the "
            + "call, for example with `.ensure a16`, or correct the callee's signature if it is wrong.");

    internal static DiagnosticDescriptor ReturnStateMismatch { get; } = Entry(
        "return-state-mismatch",
        Severity.Error,
        "{0}`{1}` declares it returns {2}, but {3}",
        "A routine's signature says what state it returns in: the state after `->`, or its entry state when there "
            + "is no `->`. Callers rely on it. On this path the state at the return, or for a tail call the state "
            + "when the routine jumped to returns, is different. Restore the state before returning, or correct "
            + "the signature.");

    internal static DiagnosticDescriptor AssertedItemNotRestored { get; } = Entry(
        "asserted-item-not-restored",
        Severity.Error,
        "{0}`{1}` says `{2}`, so {3} must be {4}, but {5} it may not be",
        "A `*` item in a signature, such as `a*` or `dp*`, says the routine returns that part of the processor "
            + "state exactly as it found it on entry, whatever it was. On this path the routine may have changed "
            + "it without restoring it. Restore it before returning, for example with `php` and `plp` around a "
            + "width change, or by saving and restoring D or B.");

    internal static DiagnosticDescriptor CallTargetUnknown { get; } = Entry(
        "call-target-unknown",
        Severity.Error,
        "`{0}` must call a named routine on the 65816: a `.proc`, an extern proc or a `proc(...)` import, whose "
            + "signature is checked",
        "On the 65816 every call is checked against the called routine's signature, which says what register "
            + "widths, mode, D and B it expects. A call to a bare address, or to anything else without a "
            + "signature, cannot be checked, so the target has to be a `.proc`, an extern proc or a `proc(...)` "
            + "import.");

    internal static DiagnosticDescriptor CallTargetNotARoutine { get; } = Entry(
        "call-target-not-a-routine",
        Severity.Error,
        "`{0}` is not a routine: on the 65816 a call must target a `.proc`, an extern proc or a `proc(...)` "
            + "import, whose signature is checked",
        "On the 65816 every call is checked against the called routine's signature. What this call names has no "
            + "signature, so there is nothing to check it against. Call a `.proc`, an extern proc or a `proc(...)` "
            + "import instead.");

    internal static DiagnosticDescriptor CallDistanceMismatch { get; } = Entry(
        "call-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, so call it with `{2}`",
        "A near routine is called with `jsr`, which pushes a two-byte return address, and returns with `rts`; a "
            + "far routine is called with `jsl`, which also pushes the program bank, and returns with `rtl`. "
            + "Calling it the other way leaves the stack unbalanced when it returns. Use the instruction the "
            + "message names, or change `near` or `far` in the routine's signature.");

    internal static DiagnosticDescriptor ReturnDistanceMismatch { get; } = Entry(
        "return-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, so it returns with `{2}`",
        "A far routine is called with `jsl` and returns with `rtl`; a near one is called with `jsr` and returns "
            + "with `rts`. Returning the other way pulls the wrong number of bytes off the stack. Use the "
            + "instruction the message names, or change `near` or `far` in the routine's signature.");

    internal static DiagnosticDescriptor JumpDistanceMismatch { get; } = Entry(
        "jump-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}: jump to it with `{2} {3}`",
        "`jmp` stays in the current bank, and `jml` also sets the program bank. A far routine is reached with "
            + "`jml`, and a near one in the same bank with `jmp`.");

    internal static DiagnosticDescriptor JumpAcrossBanks { get; } = Entry(
        "jump-across-banks",
        Severity.Error,
        "`{0}` is near and in another bank: after this `jml` its `rts` would return inside its own bank, not to "
            + "`{1}`'s caller",
        "A `jml` to a near routine in another bank changes the program bank, and the routine's `rts` pulls only a "
            + "two-byte address, so it returns within the bank it is in, not to the caller of the routine that "
            + "jumped. This is valid only when nothing returns through the jump: the jumping routine is `noreturn` "
            + "or an interrupt handler, or the target is `noreturn`. Otherwise make the target `far` and end it "
            + "with `rtl`, or place it in this bank.");

    internal static DiagnosticDescriptor JumpLeavesBank { get; } = Entry(
        "jump-leaves-bank",
        Severity.Error,
        "`{0}` cannot leave bank {1}, and `{2}` is in segment \"{3}\" in bank {4}: {5}",
        "`jsr`, `jmp` and the branches change only the 16-bit address and keep the program bank, so they cannot "
            + "reach code in a segment that the project places in another bank. Use `jsl` or `jml`, which set the "
            + "bank too; a conditional branch has no long form, so branch on the opposite condition around a `jml` "
            + "to the target.");

    internal static DiagnosticDescriptor RelativeCallNeedsPhk { get; } = Entry(
        "relative-call-needs-phk",
        Severity.Error,
        "`{0}` is far: push the bank with `phk` before the `per` of this relative call",
        "A relative call is written as a `per` that pushes the return address, then a branch to the routine. `per` "
            + "pushes only 16 bits, but a far routine returns with `rtl`, which pulls three bytes: the address and "
            + "the bank. Put a `phk` before the `per` so the bank is on the stack.");

    internal static DiagnosticDescriptor RelativeCallExtraPhk { get; } = Entry(
        "relative-call-extra-phk",
        Severity.Error,
        "`{0}` is near: remove the `phk` from this relative call",
        "A near routine returns with `rts`, which pulls only the two-byte return address that `per` pushed. The "
            + "byte `phk` pushed would be left on the stack. Remove the `phk`, or make the routine `far`.");

    internal static DiagnosticDescriptor ArgsNotPushed { get; } = Entry(
        "args-not-pushed",
        Severity.Error,
        "`{0}` declares `args {1}`, bytes the caller pushes before the call, but {2}",
        "`args n` in a routine's signature says the caller pushes n bytes of arguments before calling it, and the "
            + "routine reads or pulls exactly that many. At this call fewer bytes are on the stack than that, "
            + "counting what the calling routine has pushed since its entry. Push the arguments before the call.");

    internal static DiagnosticDescriptor DirectPageMismatch { get; } = Entry(
        "direct-page-mismatch",
        Severity.Error,
        "`{0}` is in segment \"{1}\", which expects the direct page at {2}, but D is {3} here",
        "A segment in the project file can declare `dp`, the value D must hold for its variables to be reached "
            + "with one-byte direct-page operands. Here D holds another value, so the operand would reach a "
            + "different address. Set D before this line, or declare its value with `.state dp = ...` or `dp = "
            + "...` in the routine's signature.");

    internal static DiagnosticDescriptor DirectPageUnknown { get; } = Entry(
        "direct-page-unknown",
        Severity.Error,
        "{0}, and {1}",
        "A `d:` operand is an offset from the direct page register D, so nt65 needs D's value to know which "
            + "address it reaches. Declare it with `.state dp = ...` at this point, or with `dp = ...` in the "
            + "routine's signature.");

    internal static DiagnosticDescriptor DirectPageOutOfReach { get; } = Entry(
        "direct-page-out-of-reach",
        Severity.Error,
        "{0} at {1}, which covers only {2} to {3}",
        "A direct-page operand is a one-byte offset from D, so it reaches only the 256 bytes from D to D+$ff, and "
            + "this address is outside them. Change D, or use an absolute operand.");

    internal static DiagnosticDescriptor BankMismatch { get; } = Entry(
        "bank-mismatch",
        Severity.Error,
        "`{0}` is in segment \"{1}\", which is {2}, but B is {3} here",
        "A segment in the project file can declare the bank its contents are in. An absolute operand is read from "
            + "the bank in the data bank register B, so with B holding another bank it would reach the same "
            + "address in the wrong bank. Set B first (for example with `plb`), declare it with `.state dbr = "
            + "...`, or use a long operand such as `f:`.");

    internal static DiagnosticDescriptor RangeBankMismatch { get; } = Entry(
        "range-bank-mismatch",
        Severity.Error,
        "{0} is reachable only from banks {1}, but B is {2} here",
        "The project file's `ranges` say which banks each absolute address can be reached from, for hardware that "
            + "is mirrored only in some banks. Here the data bank register B may hold a bank outside that set, so "
            + "the operand would reach something else. Set B to one of the listed banks, or use a long operand.");

    internal static DiagnosticDescriptor MirrorBankMismatch { get; } = Entry(
        "mirror-bank-mismatch",
        Severity.Error,
        "`{0}` is in segment \"{1}\", {2}, but this reaches it through bank {3}",
        "The segment's `bank` and `mirrors` in the project file say which banks its contents appear in. This long "
            + "address names the routine in a bank that is neither, so it would land somewhere else. Use an "
            + "address in the segment's bank or in one of its mirrors.");

    internal static DiagnosticDescriptor FrameNotARecord { get; } = Entry(
        "frame-not-a-record",
        Severity.Error,
        "`.frame {0}` needs a struct or union type: its size says how many stack bytes the frame covers",
        "`.frame name: T` lays out the top of the stack as the struct or union T, and T's size says how many bytes "
            + "the frame covers. Anything else has no members or size to lay out. Give the frame a struct or union "
            + "type.");

    internal static DiagnosticDescriptor FramePastTheStack { get; } = Entry(
        "frame-past-the-stack",
        Severity.Error,
        "frame `{0}` is {1} bytes, but only {2} bytes are pushed here",
        "A `.frame` names bytes on top of the stack, so it can cover at most what this routine has pushed so far. "
            + "This one covers more, so its last members would name the return address or the caller's bytes. Push "
            + "the frame's bytes before the `.frame`, or use a smaller type.");

    internal static DiagnosticDescriptor FrameDepthUnknown { get; } = Entry(
        "frame-depth-unknown",
        Severity.Error,
        "`{0}` is an offset from the stack pointer, but how many bytes are pushed here is not known{1}",
        "A frame member becomes an `n,s` operand whose offset counts every byte pushed since the `.frame`. Where "
            + "the paths reaching this line push different amounts, or something the analysis cannot follow moved "
            + "the stack pointer, the offset cannot be worked out. Make every path push the same bytes, or write "
            + "the `.frame` again after the point where the stack changes.");

    internal static DiagnosticDescriptor FrameGone { get; } = Entry(
        "frame-gone",
        Severity.Error,
        "`{0}` is in frame `{1}`, which has been pulled off the stack here",
        "The bytes the frame covered have been pulled off the stack by this point, so its members no longer name "
            + "anything. Use the member before those pulls, or write a new `.frame` for what is on the stack now.");

    internal static DiagnosticDescriptor FrameMemberNotStackRelative { get; } = Entry(
        "frame-member-not-stack-relative",
        Severity.Error,
        "`{0}` is a stack slot, usable only on its own as a stack-relative operand: `{1},s`",
        "A frame member is an offset from the stack pointer, not an address, so it can only be the whole operand "
            + "of a stack-relative instruction, as in `lda locals::count,s` or `lda (locals::ptr,s),y`. It cannot "
            + "be used in an expression or with another addressing mode.");

    private static DiagnosticArea Output { get; } =
        Opens("Output", "Names that clash in the ca65 output, and the C header.");

    internal static DiagnosticDescriptor OutputNameCollision { get; } = Entry(
        "output-name-collision",
        Severity.Error,
        "`{0}` and {1} both become `{2}` in the ca65 output",
        "nt65 writes ca65 source, and flattens its scoped names into ca65 symbols. These two different names "
            + "flatten to the same ca65 symbol, so the output would define it twice. Rename one of them.");

    internal static DiagnosticDescriptor CHeaderUntyped { get; } = Entry(
        "c-header-untyped",
        Severity.Warning,
        "`{0}` has type `{1}`, which is not exported, so the C header declares `{0}` as bytes",
        "The C header declares only exported types. This exported data has a struct or union type that is not "
            + "exported, so the header cannot name it and declares the data as an `unsigned char` array of the "
            + "same size. Export the type to give the data its C type.");

    internal static DiagnosticDescriptor CHeaderNameLeftOut { get; } = Entry(
        "c-header-name-left-out",
        Severity.Warning,
        "the C header leaves out {2} `{0}`: its linker name `{1}` has no leading `_`, so C cannot name it; export "
            + "it `as \"_{3}\"`",
        "cc65 puts an underscore in front of every C name, so C code can refer to an assembly symbol only when its "
            + "linker name starts with `_`. Export it under a name that does, with `as \"_name\"`, or turn this "
            + "warning off in the project's `diagnostics` if C does not need it.");

    internal static DiagnosticDescriptor ExportNameTaken { get; } = Entry(
        "export-name-taken",
        Severity.Error,
        "`{0}` and `{1}` are both exported to the linker as `{2}`",
        "Exports share one flat namespace with everything else the linker sees, so two exports may not reach it "
            + "under one name. `as` gives one of them a name of its own.");

    /// <summary>
    /// The one entry the compiler itself never reports: an editor uses it to mark the lines the
    /// build configuration leaves out, and nothing else reports on those lines.
    /// </summary>
    public static DiagnosticDescriptor OmittedBranch { get; } = Entry(
        "omitted-branch",
        Severity.Info,
        "the build configuration leaves this branch out",
        "The build configuration does not take this branch, so its lines are parsed and nothing else. The editor "
            + "shows them faded.");

    internal static DiagnosticDescriptor CannotBeWritten { get; } = Entry(
        "cannot-be-written",
        Severity.Error,
        "`{0}` cannot be translated to ca65, and no other error explains why: this is a bug in nt65",
        "Everything nt65 accepts should have a ca65 translation, and anything it cannot translate should have been "
            + "reported as an error first. Reaching this means neither happened, which is a bug in nt65 rather "
            + "than in the program. Please report it with the line that triggers it.");

    private static DiagnosticArea TheProjectFile { get; } =
        Opens("The project file", "`nt65.json` and the command line that adds to it.");

    internal static DiagnosticDescriptor ProjectJsonInvalid { get; } = Entry(
        "project-json-invalid",
        Severity.Error,
        "{0}",
        "The project file is not valid JSON. Comments and trailing commas are allowed; otherwise it follows "
            + "standard JSON syntax. The message gives the parser's reason, and the position points at where it "
            + "stopped.");

    internal static DiagnosticDescriptor ProjectNotAnObject { get; } = Entry(
        "project-not-an-object",
        Severity.Error,
        "{0} must hold one JSON object",
        "The top level of the project file is one JSON object, whose keys say what the program is built from and "
            + "how.");

    internal static DiagnosticDescriptor ProjectKeyUnknown { get; } = Entry(
        "project-key-unknown",
        Severity.Error,
        "`{0}` is not a key of {1}{2}",
        "The project file accepts a fixed set of keys: `cpu`, `files`, `out`, `defines`, `diagnostics`, `spaces`, "
            + "`segments`, `ranges` and `configurations`, plus `$schema`, which is accepted and ignored. An "
            + "unknown key is an error, so that a misspelt setting does not silently do nothing.");

    internal static DiagnosticDescriptor ProjectCpuUnknown { get; } = Entry(
        "project-cpu-unknown",
        Severity.Error,
        "`{0}` is not a supported `cpu`: use {1}",
        "`cpu` names the processor the program is written for, spelt exactly as one of the listed names.");

    internal static DiagnosticDescriptor ProjectNotAList { get; } = Entry(
        "project-not-a-list",
        Severity.Error,
        "`{0}` must be a list of strings",
        "`files` takes a JSON array of strings: glob patterns, relative to the project root, that name the "
            + "program's source files.");

    internal static DiagnosticDescriptor ProjectNotAString { get; } = Entry(
        "project-not-a-string",
        Severity.Error,
        "`{0}` must be a string",
        "This key takes a JSON string: `cpu` a processor name, `out` the directory the build writes its output into.");

    internal static DiagnosticDescriptor ProjectValueNotAnObject { get; } = Entry(
        "project-value-not-an-object",
        Severity.Error,
        "`{0}` must be an object",
        "This key takes a JSON object whose own keys are its entries: define names, diagnostic names, "
            + "configuration names, segment names, space names or address ranges.");

    internal static DiagnosticDescriptor DefineNameInvalid { get; } = Entry(
        "define-name-invalid",
        Severity.Error,
        "`{0}` is not a valid define name: use letters, digits and `_`, optionally after a module path such as "
            + "`hw::`",
        "A define is named as a constant is: a letter or `_` followed by letters, digits and `_`. It may have a "
            + "module's path in front of it, as in `hw::SOUND`, to set that module's `.config`.");

    internal static DiagnosticDescriptor DefineNotANumber { get; } = Entry(
        "define-not-a-number",
        Severity.Error,
        "`{0}`: a define's value must be a number",
        "A define is a constant visible in every file, and constants are numbers. In the project file give a JSON "
            + "number or a string in nt65 number syntax, such as \"$20\"; on the command line, `-D NAME=value` "
            + "takes the same syntax, and `-D NAME` alone sets it to 1.");

    internal static DiagnosticDescriptor ConfigurationNameInvalid { get; } = Entry(
        "configuration-name-invalid",
        Severity.Error,
        "`{0}` is not a valid configuration name: use only letters, digits, `_` and `-`",
        "A configuration is chosen by name with `--config` on the command line, so its name is limited to "
            + "characters that need no quoting.");

    internal static DiagnosticDescriptor ConfigurationNotAnObject { get; } = Entry(
        "configuration-not-an-object",
        Severity.Error,
        "configuration `{0}` must be an object, with any of `defines`, `diagnostics` and `out`",
        "A named configuration is a JSON object that changes the project's `defines`, `diagnostics` and `out` for "
            + "builds that choose it with `--config`: its defines and diagnostic severities take precedence over "
            + "the project's, and its `out` replaces the project's.");

    internal static DiagnosticDescriptor ConfigurationKeyUnknown { get; } = Entry(
        "configuration-key-unknown",
        Severity.Error,
        "configuration `{0}` cannot set `{1}`: a configuration may set only `defines`, `diagnostics` and `out`",
        "A named configuration changes only a few things about a build: its defines, its diagnostic severities and "
            + "its output directory. Everything else, such as `cpu`, `files` and `segments`, is set once for the "
            + "whole project.");

    internal static DiagnosticDescriptor ConfigurationUnknown { get; } = Entry(
        "configuration-unknown",
        Severity.Error,
        "`{0}` is not a configuration: {1}",
        "The name given to `--config` has to be one of the configurations declared under `configurations` in the "
            + "project file. Check the spelling against the names listed.");

    internal static DiagnosticDescriptor ProjectSegmentNotAnObject { get; } = Entry(
        "project-segment-not-an-object",
        Severity.Error,
        "segment \"{0}\" must be an object with a `size`",
        "Each entry under `segments` is an object describing that segment: its address `size`, which is required, "
            + "and optionally `dp`, `bank`, `mirrors` and `space`.");

    internal static DiagnosticDescriptor ProjectSegmentSizeMissing { get; } = Entry(
        "project-segment-size-missing",
        Severity.Error,
        "segment \"{0}\" needs a `size` of \"zp\", \"abs\" or \"far\"",
        "A segment's `size` is how wide addresses in it are: \"zp\" for one-byte zero-page addresses, \"abs\" for "
            + "two-byte absolute ones, and \"far\" for three-byte long ones. It decides how every reference to the "
            + "segment's contents is assembled, so every segment has to give one.");

    internal static DiagnosticDescriptor ProjectSpaceHoldsUnknown { get; } = Entry(
        "project-space-holds-unknown",
        Severity.Error,
        "space `{0}` must be \"code\" or \"data\"",
        "Each entry under `spaces` names an address space and says what it holds. \"code\" means the space runs "
            + "this program's processor, and nt65 checks its code; \"data\" means anything else, including code "
            + "for another processor, which nt65 treats as data and macro calls.");

    internal static DiagnosticDescriptor ProjectSegmentKeyUnknown { get; } = Entry(
        "project-segment-key-unknown",
        Severity.Error,
        "segment \"{0}\" cannot set `{1}`: a segment may set only `size`, `dp`, `bank`, `mirrors` and `space`",
        "A segment's entry says how wide its addresses are (`size`), which direct page and banks its contents are "
            + "reached through (`dp`, `bank`, `mirrors`), and which address space it is in (`space`). Any other "
            + "key is an error, so that a misspelt one does not silently do nothing.");

    internal static DiagnosticDescriptor DiagnosticNameUnknown { get; } = Entry(
        "diagnostic-name-unknown",
        Severity.Error,
        "`{0}` is not a diagnostic nt65 reports{1}",
        "The names are a fixed set, so a typo is a mistake rather than a line that quietly does nothing. "
            + "`nt65 explain` lists them.");

    internal static DiagnosticDescriptor DiagnosticSeverityUnknown { get; } = Entry(
        "diagnostic-severity-unknown",
        Severity.Error,
        "the severity of `{0}` must be \"off\", \"warning\" or \"error\"",
        "Under `diagnostics`, each diagnostic's name maps to one of three severities: \"off\" to silence it, "
            + "\"warning\" to report it without failing the build, or \"error\" to fail the build.");

    internal static DiagnosticDescriptor DiagnosticNotTurnedDown { get; } = Entry(
        "diagnostic-not-turned-down",
        Severity.Error,
        "`{0}` is an error, and a project cannot turn an error into a warning or off",
        "A warning points at something that may be intended; an error means nt65 cannot produce correct output for "
            + "the program. Lowering an error would not make the output right, only hide the problem, so "
            + "`diagnostics` can change only warnings.");

    internal static DiagnosticDescriptor RangeInvalid { get; } = Entry(
        "range-invalid",
        Severity.Error,
        "`{0}` is not a range of absolute addresses, such as \"$2100-$21ff\"",
        "A key under `ranges` is one absolute address, or two with a `-` between them, lowest first, each from "
            + "$0000 to $ffff.");

    internal static DiagnosticDescriptor RangesOverlap { get; } = Entry(
        "ranges-overlap",
        Severity.Error,
        "`{0}` overlaps `{1}-{2}`: an address can be in only one range",
        "Each range says which banks its addresses can be reached from, so an address in two ranges would have two "
            + "answers. Change the ranges so they do not overlap.");

    internal static DiagnosticDescriptor BanksNotAList { get; } = Entry(
        "banks-not-a-list",
        Severity.Error,
        "`{0}` must be a list of banks, such as [\"$00-$3f\", \"$80-$bf\"]",
        "Banks are written as a JSON array. Each item is one bank, as a number or a string, or a string holding a "
            + "range of banks such as \"$00-$3f\".");

    internal static DiagnosticDescriptor BankInvalid { get; } = Entry(
        "bank-invalid",
        Severity.Error,
        "`{0}`: {1} is not a bank or a range of banks",
        "A bank is one byte, $00 to $ff, and a range of banks is two of them with a `-` between them, lowest first.");

    private static DiagnosticArea Signatures { get; } =
        Opens("Signatures", "What a routine or macro signature may declare, and what a signature set may hold.");

    internal static DiagnosticDescriptor SignatureSetSelfReference { get; } = Entry(
        "signature-set-self-reference",
        Severity.Error,
        "signature set `{0}` names `{1}`, which leads back to `{2}`: a set cannot include itself",
        "A signature set is replaced by the items it names. If it names itself, directly or through other sets, "
            + "that replacement never ends. Remove the reference that closes the loop.");

    internal static DiagnosticDescriptor AliasDistanceMismatch { get; } = Entry(
        "alias-distance-mismatch",
        Severity.Error,
        "`{0}` is declared {1}, but `{2}`, the routine it names, is {3}",
        "`.proc name = routine: ...` declares another name for an existing routine. It is the same code, so it is "
            + "called and returns the same way: a near routine with `jsr` and `rts`, a far one with `jsl` and "
            + "`rtl`. Give the alias the routine's own `near` or `far`, or leave its signature out to take the "
            + "routine's.");

    internal static DiagnosticDescriptor AliasSignatureMismatch { get; } = Entry(
        "alias-signature-mismatch",
        Severity.Error,
        "`{0}` is declared `{1}`, but `{2}`, the routine it names, is `{3}`: another name for a routine must "
            + "declare the same signature",
        "Calls through another name for a routine are checked against the signature on that name, so a name that "
            + "declares something different from the routine would check the same code two different ways. Give it "
            + "the routine's signature, or leave its signature out to take the routine's.");

    internal static DiagnosticDescriptor ArgsNotConstant { get; } = Entry(
        "args-not-constant",
        Severity.Error,
        "`{0}` needs a constant: the byte count has to be known while nt65 builds",
        "`args n` says how many bytes the caller pushes before the call. The analysis uses it to check every call "
            + "and to work out where the routine finds its arguments on the stack, so n has to be a constant "
            + "expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor ArgsOutOfRange { get; } = Entry(
        "args-out-of-range",
        Severity.Error,
        "`{0}` is out of range: the byte count must be from 0 to $ffff",
        "`args n` counts bytes on the stack, so n has to be from 0 to $ffff, the most the 65816's stack can hold.");

    internal static DiagnosticDescriptor NoreturnDeclaresAnExit { get; } = Entry(
        "noreturn-declares-an-exit",
        Severity.Error,
        "a `noreturn` routine never returns, so it cannot declare a state after `->`",
        "What follows `->` in a signature is the state a routine returns in, for the caller to rely on after the "
            + "call. A `noreturn` routine never returns, so there is nothing to declare. Remove the `->` and what "
            + "follows it.");

    internal static DiagnosticDescriptor HandlerAssumesState { get; } = Entry(
        "handler-assumes-state",
        Severity.Error,
        "`{0}`: an interrupt handler can be entered at any instruction, so its signature can give only the mode: "
            + "`interrupt, native` or `interrupt, emu`",
        "An interrupt can arrive between any two instructions, so a handler cannot assume anything about the "
            + "register widths, D or B; it has to set what it needs itself. Only the mode is known on entry, "
            + "because the processor takes native-mode and emulation-mode interrupts through different vectors. "
            + "Remove the item and set the state inside the handler.");

    internal static DiagnosticDescriptor HandlerDistance { get; } = Entry(
        "handler-distance",
        Severity.Error,
        "`{0}` describes how a routine is called, and an interrupt handler is never called: the processor enters "
            + "it and `rti` leaves it",
        "`near`, `far`, `inline` and `args` describe how a caller calls a routine and how it returns. An interrupt "
            + "handler has no caller: the processor enters it through a vector, and `rti` leaves it. Remove the "
            + "item.");

    internal static DiagnosticDescriptor HandlerNoreturn { get; } = Entry(
        "handler-noreturn",
        Severity.Error,
        "`{0}` does not apply to an interrupt handler, which leaves with `rti` and has no caller to return to",
        "`noreturn` says a routine never comes back to its caller. An interrupt handler has no caller: the "
            + "processor enters it, and `rti` resumes the interrupted code. Remove `noreturn`.");

    internal static DiagnosticDescriptor HandlerKeeps { get; } = Entry(
        "handler-keeps",
        Severity.Error,
        "`{0}` does not apply to an interrupt handler: give the mode it is entered in, `native` or `emu`, or nothing",
        "A `*` item says a routine hands a part of the state back to its caller as it found it. An interrupt "
            + "handler has no caller: the processor enters it and `rti` leaves it. Give the mode the handler is "
            + "entered in with `native` or `emu`, or leave the mode out.");

    internal static DiagnosticDescriptor HandlerDeclaresAnExit { get; } = Entry(
        "handler-declares-an-exit",
        Severity.Error,
        "an interrupt handler cannot declare a state after `->`: it leaves with `rti`, which restores the "
            + "interrupted state",
        "What follows `->` is the state a routine returns in, for its caller to rely on. An interrupt handler "
            + "leaves with `rti`, which restores the status register and return address the interrupt pushed, so "
            + "nothing reads the state the handler leaves. Remove the `->` and what follows it.");

    internal static DiagnosticDescriptor SignatureValueNotConstant { get; } = Entry(
        "signature-value-not-constant",
        Severity.Error,
        "`{0}` needs a constant, because the analysis tracks D and B as exact values",
        "The analysis tracks the direct page register D and the data bank register B as exact values, and checks "
            + "every call against the values in the callee's signature. So `dp = ...` or `dbr = ...` in a "
            + "signature has to be a constant expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor SignatureValueOutOfRange { get; } = Entry(
        "signature-value-out-of-range",
        Severity.Error,
        "`{0}` is out of range: {1}",
        "The direct page register D holds a 16-bit address, $0000 to $ffff, and the data bank register B holds one "
            + "byte, $00 to $ff.");

    internal static DiagnosticDescriptor StateBanksNotDbr { get; } = Entry(
        "state-banks-not-dbr",
        Severity.Error,
        "`{0}`: only `dbr` can be given a set of banks; `dp` takes one address",
        "A routine that does not set B itself can run with any of several data banks that reach the same memory, "
            + "so `dbr = [...]` may list a set of banks. D decides where every direct-page operand lands, so `dp` "
            + "has to be a single value.");

    internal static DiagnosticDescriptor StateBanksInvalid { get; } = Entry(
        "state-banks-invalid",
        Severity.Error,
        "`{0}` is not a valid set of banks: each item is a constant bank, or a range written low to high, such as "
            + "`$00..$3f`",
        "A set of banks is written in square brackets, as in `dbr = [$00..$3f, $80..$bf]`. Each item is a constant "
            + "bank number or a range `low..high`, and the set names at least one bank.");

    internal static DiagnosticDescriptor SignatureSetNotFirst { get; } = Entry(
        "signature-set-not-first",
        Severity.Error,
        "signature set `{0}` must come first in its list: the items after it change what it gives",
        "A signature set supplies a group of items, and the items written after it adjust or override them. So the "
            + "set comes first in its list, and the rest read as changes to it.");

    internal static DiagnosticDescriptor SignatureSetNotASet { get; } = Entry(
        "signature-set-not-a-set",
        Severity.Error,
        "`{0}` is {1}, not a signature set: a bare name among a signature's items has to name a `.signature` set",
        "A bare name among a signature's items refers to a set declared with `.signature`, whose items it brings "
            + "in. Anything else in a signature is written as an item, such as `a16` or `dbr = $7e`.");

    internal static DiagnosticDescriptor MacroKeeps { get; } = Entry(
        "macro-keeps",
        Severity.Error,
        "`{0}` does not apply to a macro: its body becomes part of the routine it is expanded into, whose "
            + "signature says what it keeps",
        "A macro's body is expanded into a routine, so what it saves and restores is part of what that routine "
            + "keeps, and `keeps` belongs in the routine's signature. Remove it from the macro's.");

    internal static DiagnosticDescriptor MacroNoreturn { get; } = Entry(
        "macro-noreturn",
        Severity.Error,
        "`noreturn` does not apply to a macro: a macro is expanded in place, not called",
        "A macro is expanded where it is used, and whatever follows the expansion belongs to the routine it is in. "
            + "`noreturn` describes routines that never return to a caller. Remove it from the macro's signature.");

    internal static DiagnosticDescriptor MacroDistance { get; } = Entry(
        "macro-distance",
        Severity.Error,
        "`{0}` does not apply to a macro: it describes how a routine is called, and a macro is expanded in place",
        "A macro is not called and does not return: its body is written out where it is used. `near`, `far`, "
            + "`inline`, `args` and `interrupt` describe how a routine is called, entered or left, so they do not "
            + "apply to it. Remove the item.");

    internal static DiagnosticDescriptor DistanceDisagrees { get; } = Entry(
        "distance-disagrees",
        Severity.Error,
        "`{0}` contradicts the earlier `{1}`: a routine is either near or far",
        "A near routine is called with `jsr` and returns with `rts`; a far one is called with `jsl` and returns "
            + "with `rtl`. A routine is one or the other, so its signature says `near` or `far` once. Remove one "
            + "of them.");

    internal static DiagnosticDescriptor SignatureItemTwice { get; } = Entry(
        "signature-item-twice",
        Severity.Error,
        "`{0}` and `{1}` both describe the same part of the state",
        "Each part of the processor state is declared once in a signature, so what it says does not depend on the "
            + "order its items are read in. Keep one of the two items.");

    internal static DiagnosticDescriptor UnchangedNeedsEntry { get; } = Entry(
        "unchanged-needs-entry",
        Severity.Error,
        "`{0}` after `->` needs `{1}` before it too: a routine can promise to return a part unchanged only if it "
            + "assumes nothing about it on entry",
        "A `*` after the arrow promises that the routine returns that part of the state as its caller left it. "
            + "That promise only means something when the entry also says `*`, accepting whatever the caller has. "
            + "If the entry gives a value, write that value after the arrow instead.");

    internal static DiagnosticDescriptor ItemBelongsAtEntry { get; } = Entry(
        "item-belongs-at-entry",
        Severity.Error,
        "`{0}` {1}, and belongs before `->`",
        "What a routine is and how it is called are true of it from entry to exit, so they are written once, "
            + "before the arrow. What comes after the arrow is what the routine leaves.");

    // Found by reflecting over the class rather than listed by hand, so that a new entry above is
    // included automatically. It is built on first use rather than alongside the entries:
    // reflecting on a type while its own static initializer is still running can deadlock two
    // threads that ask for it at the same time.
    private static readonly Lazy<IReadOnlyList<DiagnosticDescriptor>> all = new(() =>
        [.. typeof(Catalogue).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DiagnosticDescriptor))
            .Select(property => (DiagnosticDescriptor)property.GetValue(null)!)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)]);

    /// <summary>Every descriptor, in name order.</summary>
    public static IReadOnlyList<DiagnosticDescriptor> All => all.Value;

    /// <summary>The descriptor named <paramref name="id"/>, or null when no diagnostic has that name.</summary>
    public static DiagnosticDescriptor? Find(string id) =>
        All.FirstOrDefault(descriptor => descriptor.Id == id);

    /// <summary>Opens an area: the entries written below it, down to the next heading, belong to it.</summary>
    private static DiagnosticArea Opens(string name, string about) => opening = new(name, about);

    /// <summary>One entry, under the heading above it.</summary>
    private static DiagnosticDescriptor Entry(string id, Severity severity, string format, string explanation) =>
        new(
            id,
            opening ?? throw new InvalidOperationException($"`{id}` is written above the first heading"),
            severity,
            format,
            explanation);
}
