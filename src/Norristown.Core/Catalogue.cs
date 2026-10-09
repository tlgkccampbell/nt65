using System.Collections.Frozen;
using System.Reflection;

namespace Norristown;

/// <summary>
/// Lists every diagnostic nt65 reports, by name. A name says what is wrong rather than which
/// pass found it, is kebab-case, and is stable once released. A project file switches
/// diagnostics by name, the editor shows the name beside the message, and CI matches on it.
/// <para>
/// A reporting site names one of these entries and supplies the arguments for its message. The
/// explanation is separate from the message. It holds what a one-line message has no room for,
/// and <c>nt65 explain</c> prints it.
/// </para>
/// </summary>
public static class Catalogue
{
    /// <summary>Gets every area, in the order the areas are declared here and printed.</summary>
    public static IReadOnlyList<DiagnosticArea> Areas =>
    [
        Area.ReadingALine, Area.Names, Area.Values, Area.Macros, Area.Data, Area.Placement,
        Area.Instructions, Area.ControlFlow, Area.ProcessorState, Area.Output, Area.TheProjectFile, Area.Signatures,
        Area.Allowing, Area.Suggestions,
    ];

    /// <summary>
    /// Gets the one entry the compiler itself never reports. An editor uses it to mark the lines
    /// the build configuration leaves out, and nothing else reports on those lines.
    /// </summary>
    public static DiagnosticDescriptor OmittedBranch { get; } = Entry(
        Area.Output,
        "omitted-branch",
        Severity.Info,
        "the build configuration leaves this branch out",
        "The build configuration does not take this branch, so its lines are parsed and nothing else. The editor "
            + "shows them faded.");

    /// <summary>
    /// Gets the suggestion the editor shows on a line longer than its setting allows, where an
    /// expression could be laid out across lines or a list directive's items written as a block.
    /// Like <see cref="OmittedBranch"/>, it is the editor's own, and no build reports it.
    /// </summary>
    public static DiagnosticDescriptor LongLine { get; } = Entry(
        Area.Output,
        "long-line",
        Severity.Info,
        "this line is longer than {0} columns",
        "The formatter keeps the line breaks a file has and adds none, so a long line stays long until it is "
            + "broken. Where an expression's calls and sets could go one item to a line, the editor suggests it, "
            + "and the refactoring there lays the expression out. Where the items of an `.export`, `.import`, "
            + "`.use` or `.next` run past the limit, the editor suggests writing them as an item block, one to a "
            + "line. The limit is the editor's `nt65.lineLength` setting, and 0 turns the suggestion off.");

    // Reading a line

    internal static DiagnosticDescriptor NumberInvalid { get; } = Entry(
        Area.ReadingALine,
        "number-invalid",
        Severity.Error,
        "invalid {0} number `{1}`",
        "A number is read to the end of the word, so every character in it must be a digit of its base: `0`-`9` "
            + "for decimal, `0`-`9` and `a`-`f` after `$`, and `0` and `1` after `%`, with `_` allowed between "
            + "digits. A character outside the base makes the whole number invalid rather than starting something "
            + "new. Check for a typo or a missing `$`.");

    internal static DiagnosticDescriptor NumberSeparator { get; } = Entry(
        Area.ReadingALine,
        "number-separator",
        Severity.Error,
        "`{0}` has a misplaced `_`: a digit separator needs a digit on each side",
        "A `_` may group the digits of a number in any base, as in `$7f_ff`, `%1010_1010` or `1_000`. It must have "
            + "a digit on each side, so it cannot start or end the number or stand next to another `_`.");

    internal static DiagnosticDescriptor DigitsMissing { get; } = Entry(
        Area.ReadingALine,
        "digits-missing",
        Severity.Error,
        "expected {0} digits after `{1}`{2}",
        "`$` starts a hexadecimal number and `%` a binary one, so digits must follow directly, as in `$ff` or "
            + "`%1010`. In nt65 `%` is only a binary prefix; the remainder operator is `.mod`.");

    internal static DiagnosticDescriptor NameAfterAt { get; } = Entry(
        Area.ReadingALine,
        "name-after-at",
        Severity.Error,
        "expected a name after `@`",
        "A `@` starts a cheap local, a label private to the routine around it, and the name must follow it "
            + "directly, as in `@loop`.");

    internal static DiagnosticDescriptor StrayDot { get; } = Entry(
        Area.ReadingALine,
        "stray-dot",
        Severity.Error,
        "unexpected `.`",
        "A `.` begins a directive or a built-in function, and a `..` a range. On its own it is neither.");

    internal static DiagnosticDescriptor UnexpectedCharacter { get; } = Entry(
        Area.ReadingALine,
        "unexpected-character",
        Severity.Error,
        "unexpected character `{0}`",
        "This character cannot start anything in nt65. Any character may appear inside a string, a character "
            + "literal or a comment, but nowhere else.");

    internal static DiagnosticDescriptor EscapeHexDigits { get; } = Entry(
        Area.ReadingALine,
        "escape-hex-digits",
        Severity.Error,
        "`\\x` must be followed by two hexadecimal digits",
        "A `\\xHH` escape gives one byte as exactly two hexadecimal digits, as in `\\x0d` or `\\xff`. Add a "
            + "leading zero for a value below `$10`.");

    internal static DiagnosticDescriptor EscapeUnknown { get; } = Entry(
        Area.ReadingALine,
        "escape-unknown",
        Severity.Error,
        "unknown escape `\\{0}`",
        "The escapes are `\\n`, `\\r`, `\\t`, `\\0`, `\\\\`, `\\\"`, `\\'` and `\\xHH`, the same in a character "
            + "literal and in a string. For a backslash itself, use `\\\\`. Each unknown escape is reported "
            + "separately, since each needs its own correction.");

    internal static DiagnosticDescriptor TextUnterminated { get; } = Entry(
        Area.ReadingALine,
        "text-unterminated",
        Severity.Error,
        "unterminated {0}",
        "A string or character literal must close on the line where it opens; nt65 has no literals that span "
            + "lines. Add the closing quote.");

    internal static DiagnosticDescriptor CharacterEmpty { get; } = Entry(
        Area.ReadingALine,
        "character-empty",
        Severity.Error,
        "empty character literal",
        "A character literal is one character, which is one value. Text goes in double quotes.");

    internal static DiagnosticDescriptor CharacterTooLong { get; } = Entry(
        Area.ReadingALine,
        "character-too-long",
        Severity.Error,
        "a character literal holds exactly one character",
        "A character literal is one value. Several characters are text, which goes in double quotes, and a data "
            + "declaration emits their bytes.");

    internal static DiagnosticDescriptor BlockNotClosed { get; } = Entry(
        Area.ReadingALine,
        "block-not-closed",
        Severity.Error,
        "missing `}}` to close this block",
        "Blocks are read from the braces before anything else in a file is read, so an unclosed one is reported "
            + "at the line that opens it rather than at the end of the file.");

    internal static DiagnosticDescriptor UnmatchedBrace { get; } = Entry(
        Area.ReadingALine,
        "unmatched-brace",
        Severity.Error,
        "unmatched `}}`",
        "A `}` closes a block, and there is no open one here.");

    internal static DiagnosticDescriptor UnexpectedToken { get; } = Entry(
        Area.ReadingALine,
        "unexpected-token",
        Severity.Error,
        "unexpected {0}",
        "The statement was already complete before this token, so nt65 cannot tell what the rest of the line is "
            + "for. Common causes are a missing comma or operator, or a comment without its `;`. Only the first "
            + "unexpected token on a line is reported.");

    internal static DiagnosticDescriptor BlockBraceEndsTheLine { get; } = Entry(
        Area.ReadingALine,
        "block-brace-ends-the-line",
        Severity.Error,
        "a block's `{{` must end its line: move {0} and the rest to the next line, and put `}}` on a line of its own",
        "A block always spans several lines: its opening `{` is the last thing on its line, its contents follow on "
            + "lines of their own, and its closing `}` stands alone on its line. This keeps the structure of a "
            + "file readable from its indentation and braces alone.");

    internal static DiagnosticDescriptor ItemsOnLineAndInBlock { get; } = Entry(
        Area.ReadingALine,
        "items-on-line-and-in-block",
        Severity.Error,
        "`{0}` gives its items on its line or in the block its `{{` opens, not both",
        "A directive whose operand is a list, such as `.export`, `.import`, `.use` or `.next`, gives the list on "
            + "its own line, or ends its line with `{` and gives the items on the lines of the block, one or more "
            + "to a line. A line that does both splits one list over two places. Move the items on the line into "
            + "the block, or remove the `{` and the block.");

    internal static DiagnosticDescriptor ItemBlockEmpty { get; } = Entry(
        Area.ReadingALine,
        "item-block-empty",
        Severity.Error,
        "`{0}` opens a block of items and lists none in it",
        "A block of items holds what the directive would otherwise list on its own line, one or more items to a "
            + "line. A block with no items says nothing. Give the items, or remove the directive and its block.");

    internal static DiagnosticDescriptor ItemBlockHoldsABlock { get; } = Entry(
        Area.ReadingALine,
        "item-block-holds-a-block",
        Severity.Error,
        "a block of `{0}` items holds only items, not a block of its own",
        "Each line of a block of items lists one or more of the directive's items, separated by commas, as the "
            + "directive's own line would. A line that opens a block, such as an `.if`, belongs outside it.");

    internal static DiagnosticDescriptor Ca65Spelling { get; } = Entry(
        Area.ReadingALine,
        "ca65-spelling",
        Severity.Error,
        "ca65's `{0}` is `{1}` in nt65",
        "nt65 has this directive under a different name. The fix replaces it with the nt65 spelling.");

    internal static DiagnosticDescriptor Ca65Tag { get; } = Entry(
        Area.ReadingALine,
        "ca65-tag",
        Severity.Error,
        "`.tag T` is `.type T` in nt65, and `.tag T, n` is `.type T[n]`",
        "A record type has the same form wherever it is used, and a count goes in brackets after it, "
            + "as every other count does.");

    internal static DiagnosticDescriptor Ca65BlockEnd { get; } = Entry(
        Area.ReadingALine,
        "ca65-block-end",
        Severity.Error,
        "nt65 closes blocks with `}}`, not `{0}`",
        "nt65 closes every block with `}` rather than with an end directive such as `.endproc`, `.endscope` or "
            + "`.endif`. Replace the directive with `}`; the fix does this.");

    internal static DiagnosticDescriptor DataNeedsAName { get; } = Entry(
        Area.ReadingALine,
        "data-needs-a-name",
        Severity.Error,
        "`.data` needs a name; to switch segments, write `.segment DATA`",
        "`.data` is a declaration, and a declaration has a name. The segment called DATA is named on a "
            + "`.segment` line.");

    internal static DiagnosticDescriptor DataValuesNeedBraces { get; } = Entry(
        Area.ReadingALine,
        "data-values-need-braces",
        Severity.Error,
        "{0}",
        "The values of an array (a declaration with a count, `[n]`) or of a record (`.type T`) go in "
            + "braces, as in `{ 1, 2 }`, so that where the values end is stated rather than worked out from "
            + "the count.");

    internal static DiagnosticDescriptor ExpectedName { get; } = Entry(
        Area.ReadingALine,
        "expected-name",
        Severity.Error,
        "expected {0}",
        "A declaration, a parameter, a path or a `.use` item needs a name at this point, and the line does not "
            + "have one. The message shows which name is expected; add it. nt65 never makes up a name, so nothing "
            + "after the missing one on the line is read.");

    internal static DiagnosticDescriptor ExpectedStatement { get; } = Entry(
        Area.ReadingALine,
        "expected-statement",
        Severity.Error,
        "expected {0}",
        "A line must be a label, a constant, an instruction, a directive or a macro call, and this one starts as "
            + "none of them. After a label, the rest of the line may only be an instruction, a data directive or a "
            + "macro call.");

    internal static DiagnosticDescriptor ExpectedExpression { get; } = Entry(
        Area.ReadingALine,
        "expected-expression",
        Severity.Error,
        "expected an expression",
        "An expression belongs here and the line has something that starts none: a stray operator, a closing "
            + "bracket, or nothing at all.");

    internal static DiagnosticDescriptor ExpectedElementIndex { get; } = Entry(
        Area.ReadingALine,
        "expected-element-index",
        Severity.Error,
        "expected an index between the brackets: `name[i]` is element `i` of `name`",
        "The brackets after a name pick one element of what it declares, and the brackets here are empty. A count "
            + "goes on the declaration; an index goes on a use of it.");

    internal static DiagnosticDescriptor ExpectedParenthesis { get; } = Entry(
        Area.ReadingALine,
        "expected-parenthesis",
        Severity.Error,
        "expected {0}",
        "A parameter list, an argument list or a parenthesised expression is unbalanced or unopened.");

    internal static DiagnosticDescriptor ExpectedBrace { get; } = Entry(
        Area.ReadingALine,
        "expected-brace",
        Severity.Error,
        "expected {0}",
        "A block opens with a `{` at the end of its first line and closes with a `}` on a line of its own; a "
            + "braced value such as `{ 1, 2 }` opens and closes on one line. One of the two braces is missing here.");

    internal static DiagnosticDescriptor ExpectedBracket { get; } = Entry(
        Area.ReadingALine,
        "expected-bracket",
        Severity.Error,
        "expected {0}",
        "A count, an index, a long indirect operand or a list of banks goes in brackets, and one of them is "
            + "not closed or not opened.");

    internal static DiagnosticDescriptor ExpectedDotDot { get; } = Entry(
        Area.ReadingALine,
        "expected-dot-dot",
        Severity.Error,
        "expected {0}",
        "A range gives its lower end first and its higher end second, with `..` between them.");

    internal static DiagnosticDescriptor ConstMissing { get; } = Entry(
        Area.ReadingALine,
        "const-missing",
        Severity.Error,
        "`.const` is missing before `{0}`",
        "Every declaration starts with its keyword, so a constant is `.const NAME = value` and a setting is "
            + "`.const NAME ?= value`. A bare `name = value` sets a record member or names an argument, and declares "
            + "nothing.");

    internal static DiagnosticDescriptor ExpectedEquals { get; } = Entry(
        Area.ReadingALine,
        "expected-equals",
        Severity.Error,
        "expected {0}",
        "This construct gives its value after an `=`: a record member's value, a `.charmap` entry, a segment "
            + "attribute, a `.const`, a signature set's items or a `.func` body. The `=` is missing here; "
            + "the message shows the expected form.");

    internal static DiagnosticDescriptor ExpectedColon { get; } = Entry(
        Area.ReadingALine,
        "expected-colon",
        Severity.Error,
        "expected {0}",
        "A `:` separates a declaration from what it is: a segment from its address size, data from its element "
            + "type, a frame from the record it is laid out as.");

    internal static DiagnosticDescriptor ExpectedComma { get; } = Entry(
        Area.ReadingALine,
        "expected-comma",
        Severity.Error,
        "expected {0}",
        "A `.multiproc` names the enum it walks and then the name each routine is named from, with a comma "
            + "between them.");

    internal static DiagnosticDescriptor ExpectedText { get; } = Entry(
        Area.ReadingALine,
        "expected-text",
        Severity.Error,
        "expected {0}",
        "A message, or a linker name, goes in double quotes. nt65 has no bare-word text.");

    internal static DiagnosticDescriptor ExpectedDataType { get; } = Entry(
        Area.ReadingALine,
        "expected-data-type",
        Severity.Error,
        "expected {0}",
        "A `.data` declaration or a typed import states what its bytes are: a number type such as `.byte` or "
            + "`.word`, an address type such as `.addr`, or a record type, `.type T`; a `.data` declaration may "
            + "also include a file with `.incbin`. What is here is none of those.");

    internal static DiagnosticDescriptor ExpectedMemberValue { get; } = Entry(
        Area.ReadingALine,
        "expected-member-value",
        Severity.Error,
        "expected `member = value`",
        "Each line of a multi-line record initializer gives one member a value.");

    internal static DiagnosticDescriptor ExpectedCpu { get; } = Entry(
        Area.ReadingALine,
        "expected-cpu",
        Severity.Error,
        "expected {0}",
        "`.cpu` names one of the processors nt65 knows, spelled as in the project file.");

    internal static DiagnosticDescriptor ExpectedAddressSize { get; } = Entry(
        Area.ReadingALine,
        "expected-address-size",
        Severity.Error,
        "expected {0}",
        "An address size is one of `zp` (one byte), `abs` (two bytes) or `far` (three bytes), and has no other "
            + "spellings. An import may give a routine signature, `proc(...)`, or an element type such as `.byte`, "
            + "in place of a size.");

    internal static DiagnosticDescriptor ImportNeedsAnElementType { get; } = Entry(
        Area.ReadingALine,
        "import-needs-an-element-type",
        Severity.Error,
        "`{0}` is not an element type: an import states what its bytes are as `.byte`, `.word`, `.addr` or `.type T`",
        "A typed import describes storage another object holds, so it gives an element type and a count. A "
            + "directive that includes a file or emits text describes bytes this program would emit, and an import "
            + "emits none.");

    internal static DiagnosticDescriptor ImportHoldsNoValues { get; } = Entry(
        Area.ReadingALine,
        "import-holds-no-values",
        Severity.Error,
        "an import cannot give values: its bytes are defined in another object file",
        "An import declares what a symbol another object defines looks like, so that nt65 can size it and reach "
            + "its members. The bytes themselves belong to whoever defines it.");

    internal static DiagnosticDescriptor ExpectedSegmentAttribute { get; } = Entry(
        Area.ReadingALine,
        "expected-segment-attribute",
        Severity.Error,
        "expected {0}",
        "After its address size, a segment declaration may give only these attributes: `dp`, the direct page it is "
            + "reached through; `bank`, the bank it is in; `mirrors`, the banks it is mirrored in; and `space`, "
            + "the address space it is in.");

    internal static DiagnosticDescriptor ExpectedPlacement { get; } = Entry(
        Area.ReadingALine,
        "expected-placement",
        Severity.Error,
        "expected {0}",
        "A `.module` line may mark the module, after a `:`, as `placed` (another module always includes it with "
            + "`.place`) or `placeable` (at most one module may place it; if none does, it gets its own output). "
            + "Nothing else may go there.");

    internal static DiagnosticDescriptor ExpectedParameterKind { get; } = Entry(
        Area.ReadingALine,
        "expected-parameter-kind",
        Severity.Error,
        "expected {0}",
        "A macro parameter may declare what kind of argument it takes, with one of a fixed set of words: `expr`, "
            + "`const`, `ident`, `operand`, `one(...)`, `list(...)` or `block`, or the name of an enum. A "
            + "parameter that declares no kind takes an expression.");

    internal static DiagnosticDescriptor FunctionParameterKind { get; } = Entry(
        Area.ReadingALine,
        "function-parameter-kind",
        Severity.Error,
        "a `.func` parameter takes no kind; only `.macro` parameters have kinds",
        "A macro parameter may say what kind of argument it takes, such as a name, an operand or a block, because a "
            + "macro writes its arguments into code. A function's arguments are only ever values, so its parameters "
            + "are bare names, with a default if they need one: `.func scaled(value, factor = 2) = value * factor`.");

    internal static DiagnosticDescriptor ExpectedStateItem { get; } = Entry(
        Area.ReadingALine,
        "expected-state-item",
        Severity.Error,
        "expected {0}",
        "A signature, a `.state` and an `.ensure` are made of processor-state items, and this is not one.");

    internal static DiagnosticDescriptor ExpectedKeptRegisters { get; } = Entry(
        Area.ReadingALine,
        "expected-kept-registers",
        Severity.Error,
        "expected {0}",
        "`keeps` lists the registers a routine returns with the same values they had on entry, for example `keeps "
            + "x, y`, and `reads` lists those whose values from its caller it uses, for example `reads a, c`. "
            + "`saves x`, in a `.state`, names the register the store above it saves. Each must list at least one "
            + "register, except that `reads none` declares a routine that reads nothing. The registers are `a`, `x` "
            + "and `y`, and the flags `c`, `z`, `n` and `v`.");

    internal static DiagnosticDescriptor ExpectedLabel { get; } = Entry(
        Area.ReadingALine,
        "expected-label",
        Severity.Error,
        "expected {0}",
        "`.next` names where execution continues, `.fallthrough` the routine that execution runs into, and "
            + "`.patch` the instruction whose bytes are overwritten. Each takes a label; `.next` also takes `?`, "
            + "which says execution continues somewhere nt65 is not told about. After `as`, `.patch` lists the "
            + "mnemonics of the instructions the store can write, as in `.patch @op as dex`.");

    internal static DiagnosticDescriptor NestingTooDeep { get; } = Entry(
        Area.ReadingALine,
        "nesting-too-deep",
        Severity.Error,
        "this expression nests more than {0} levels deep",
        "nt65 stops reading an expression nested deeper than this limit. The limit is far beyond anything typed "
            + "by hand; it is there so that a half-typed line of brackets cannot overflow the stack and crash the "
            + "assembler or the editor's language server.");

    internal static DiagnosticDescriptor UnnamedLabel { get; } = Entry(
        Area.ReadingALine,
        "unnamed-label",
        Severity.Error,
        "unnamed labels (`:`, `:+`, `:-`) are not supported: use a cheap local instead, `@name:`",
        "ca65's unnamed labels are found by counting, so `:+` means the next `:`, and adding or removing one "
            + "changes what the others refer to. nt65 has no unnamed labels. Use a cheap local, such as `@loop:`, "
            + "and branch to `@loop`; it is private to the routine around it and costs nothing more in the output.");

    internal static DiagnosticDescriptor DirectiveUnknown { get; } = Entry(
        Area.ReadingALine,
        "directive-unknown",
        Severity.Error,
        "unknown directive `{0}`",
        "The word begins with a `.` and is no directive nt65 has. Directives are a fixed set: nothing declares "
            + "one.");

    internal static DiagnosticDescriptor DirectiveAfterLabel { get; } = Entry(
        Area.ReadingALine,
        "directive-after-label",
        Severity.Error,
        "`{0}` may not follow a label",
        "A label is a position in code, so what may follow it on the line is what takes bytes there: an "
            + "instruction, a data directive or a macro call.");

    internal static DiagnosticDescriptor ElseIfMisplaced { get; } = Entry(
        Area.ReadingALine,
        "elseif-misplaced",
        Severity.Error,
        "`{0}` must follow the `}}` that closes the previous branch, on the same line",
        "Each branch of an `.if` is continued on the line that closes the one before it, `} .elseif condition {` "
            + "or `} .else {`, so that the shape of the block is readable without matching braces by eye.");

    internal static DiagnosticDescriptor AssertLevel { get; } = Entry(
        Area.ReadingALine,
        "assert-level",
        Severity.Error,
        "`.assert` takes no level: remove `{0}`, since a failed assertion is always an error",
        "ca65's `.assert` takes a level such as `warning` or `error`, because ca65 cannot always decide the "
            + "condition itself. nt65 always reports a failed assertion as an error, checking it as soon as the "
            + "value is known and otherwise leaving it for the linker. Use `.assert condition, \"message\"`; the "
            + "fix removes the level.");

    internal static DiagnosticDescriptor SegmentNameQuoted { get; } = Entry(
        Area.ReadingALine,
        "segment-name-quoted",
        Severity.Error,
        "a segment name takes no quotes: `.segment {0}`",
        "Segments are a table of their own and share no namespace with symbols, so a segment name is a plain "
            + "word. The quotes are ca65's habit.");

    internal static DiagnosticDescriptor ModuleNameQuoted { get; } = Entry(
        Area.ReadingALine,
        "module-name-quoted",
        Severity.Error,
        "a module name takes no quotes: `.module hw::vic`",
        "A module name is a path of plain names, such as `hw::vic`, with no quotes.");

    internal static DiagnosticDescriptor ExportDeclaresNothing { get; } = Entry(
        Area.ReadingALine,
        "export-declares-nothing",
        Severity.Error,
        "`{0}` declares nothing, so there is nothing to export",
        "`.export` before a declaration exports what that declaration declares, so the directive after it has to "
            + "be one that declares something.");

    internal static DiagnosticDescriptor DataBodyHoldsValues { get; } = Entry(
        Area.ReadingALine,
        "data-body-holds-values",
        Severity.Error,
        "`{0}` cannot appear in an array body, which holds only values: the element type goes on the declaration "
            + "line",
        "The lines between an array's braces hold only its values, separated by commas. The element type, such as "
            + "`.byte` or `.word`, is given once on the declaration line, so a directive in the body would give it "
            + "a second time. Remove the directive and keep the values.");

    internal static DiagnosticDescriptor DataBodyNeedsACount { get; } = Entry(
        Area.ReadingALine,
        "data-body-needs-a-count",
        Severity.Error,
        "values in a body need a count: write `{0}[] {{`",
        "A body of values belongs to an array, and an array states how many elements it holds. `[]` counts the "
            + "values given, which is what a body without a count usually meant.");

    internal static DiagnosticDescriptor StateItemUnknown { get; } = Entry(
        Area.ReadingALine,
        "state-item-unknown",
        Severity.Error,
        "`{0}` is not a processor-state item",
        "The items a signature, a `.state` or an `.ensure` may hold are a fixed set, such as `a8`, `i16`, `dp = 0`, "
            + "`c = 0`, `cz = 0` or `keeps x`. This word looks like an item, or has an item's form, but is not one; check "
            + "its spelling. A plain word that is not an item is read as the name of a signature set.");

    internal static DiagnosticDescriptor StateItemIsAWidth { get; } = Entry(
        Area.ReadingALine,
        "state-item-is-a-width",
        Severity.Error,
        "`{0}` is not an item: {1}",
        "The 65816's m, x and e flags are part of the state nt65 follows, but they are written as what they "
            + "mean: `a8` or `a16` for m, `i8` or `i16` for x, and `emu` or `native` for e. The other flags are "
            + "written by name, as in `c = 0`.");

    internal static DiagnosticDescriptor OperatorsNeedParentheses { get; } = Entry(
        Area.ReadingALine,
        "operators-need-parentheses",
        Severity.Error,
        "`{0}` and `{1}` need parentheses to show which applies first",
        "nt65 gives every operator a precedence, but refuses two combinations that readers often misjudge: a shift "
            + "or bitwise operator with a different operator as its operand, and different logical operators "
            + "mixed. Add parentheses to show which applies first; the fix can add them.");

    internal static DiagnosticDescriptor ContinuationOutsideExpression { get; } = Entry(
        Area.ReadingALine,
        "continuation-outside-expression",
        Severity.Error,
        "a line can continue only inside brackets, a macro call's arguments or a parameter list",
        "A line whose `(` or `[` is still open at its end continues onto the next, so a long expression, macro "
            + "call or list of parameters can be written across lines. Only an expression's own brackets, a macro "
            + "call's arguments and a macro's or a function's parameters may hold a line break: a group's "
            + "parentheses, a call's or a macro call's arguments, a set, an index, and the parentheses after the "
            + "name that `.macro` or `.func` declares. The parentheses of an operand such as `(ptr),y`, written in "
            + "an instruction or in a braced argument like `{(ptr),y}`, and a data declaration's count stay on one "
            + "line.");

    internal static DiagnosticDescriptor ByteOperatorNeedsParentheses { get; } = Entry(
        Area.ReadingALine,
        "byte-operator-needs-parentheses",
        Severity.Error,
        "unary `{0}` binds tighter than `{1}`: use `({0}x) {1} y` or `{2}(x {1} y)` to show which is meant",
        "The byte operators bind tighter than any binary operator, so `<label + 1` means `(<label) + 1`, the low "
            + "byte plus one, not the low byte of `label + 1`. Because that is easy to misread, nt65 asks for "
            + "parentheses rather than guessing.");

    internal static DiagnosticDescriptor NotAFunction { get; } = Entry(
        Area.ReadingALine,
        "not-a-function",
        Severity.Error,
        "`{0}` is not a function",
        "The built-in functions are a fixed set. A function the program declares is a `.func`, and its name "
            + "has no leading `.`.");

    internal static DiagnosticDescriptor BuiltinArgumentNamed { get; } = Entry(
        Area.ReadingALine,
        "builtin-argument-named",
        Severity.Error,
        "`{0}` is built in and takes no named arguments",
        "A call to a `.func` may give an argument by its parameter's name, as `scaled(3, factor = 4)`. A built-in "
            + "function's parameters have no names, so each of its arguments is given in its place.");

    // Names

    internal static DiagnosticDescriptor NotDeclared { get; } = Entry(
        Area.Names,
        "not-declared",
        Severity.Error,
        "`{0}` is not declared{1}",
        "Nothing in scope here declares the name. Where a name one letter away is declared, or another module "
            + "exports it, the message reports it, because that is nearly always what was meant.");

    internal static DiagnosticDescriptor NotDeclaredIn { get; } = Entry(
        Area.Names,
        "not-declared-in",
        Severity.Error,
        "`{0}` is not declared in {1}{2}",
        "The part before `::` names a module, a scope, a type or a routine, and it does not declare the name after "
            + "it. Check the spelling, and whether the name is in a different scope. Order does not matter: every "
            + "name a scope declares can be reached, wherever in the scope it is declared.");

    internal static DiagnosticDescriptor NotExported { get; } = Entry(
        Area.Names,
        "not-exported",
        Severity.Error,
        "`{0}` is not exported by module `{1}`",
        "A module names what other modules may see, and this is not among it. The fix is an `.export` on the "
            + "declaration in the module that owns it.");

    internal static DiagnosticDescriptor DeclaredInAnotherModule { get; } = Entry(
        Area.Names,
        "declared-in-another-module",
        Severity.Error,
        "`{0}` is not declared here; module `{1}` exports it, so write `{2}::{3}` or `.use {4}::{5}`",
        "The name is not in scope in this file, and exactly one module in the program exports it, which is almost "
            + "always the one meant.");

    internal static DiagnosticDescriptor ModuleNotInTheBuild { get; } = Entry(
        Area.Names,
        "module-not-in-the-build",
        Severity.Error,
        "`{0}` is not declared, and no module `{1}` is in this build",
        "The first part of a path names a module or something in scope, and is neither. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ModuleUnknown { get; } = Entry(
        Area.Names,
        "module-unknown",
        Severity.Error,
        "no module `{0}` is in this build",
        "A path that starts from the root of the modules names a module the program has. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ExportAmbiguous { get; } = Entry(
        Area.Names,
        "export-ambiguous",
        Severity.Error,
        "`{0}` could be `{1}::{0}` or `{2}::{0}`: write the one you mean",
        "Two modules brought in with `.use module::*` export the same name, so which one is meant would depend on "
            + "the order the `.use` lines are read in. Use the full path, or bring in the one you mean by name "
            + "with `.use module::name`, which takes precedence over `::*`.");

    internal static DiagnosticDescriptor NotAScope { get; } = Entry(
        Area.Names,
        "not-a-scope",
        Severity.Error,
        "`{0}` is {1}, not a scope",
        "A `::` reaches into a module, a scope, a routine or a named type. The symbol before it is none of those, "
            + "so nothing can be reached through it.");

    internal static DiagnosticDescriptor FieldsNeedAStatedType { get; } = Entry(
        Area.Names,
        "fields-need-a-stated-type",
        Severity.Error,
        "`{0}` states no type, so `::` cannot reach its fields; give it one on its declaration, such as `: .type T`",
        "The names after `::` are resolved while nt65 reads the declarations, before it works out where an offset "
            + "lands. So the fields of data found elsewhere can be named when its declaration states a type, or when "
            + "its address is the plain name of data, whose type is known. For any other address, state the type.");

    internal static DiagnosticDescriptor NameAlreadyDeclared { get; } = Entry(
        Area.Names,
        "name-already-declared",
        Severity.Error,
        "`{0}` is already declared in this scope",
        "Declarations are a set, and a scope declares each name once. Where the other declaration is in this file "
            + "it is shown beside this one.");

    internal static DiagnosticDescriptor IdentParameterDeclared { get; } = Entry(
        Area.Names,
        "ident-parameter-declared",
        Severity.Error,
        "`{0}` is an `ident` parameter, so the macro body may not declare it: that would declare the caller's name",
        "An `ident` parameter is replaced by a name the caller passes in, so declaring it in the macro body would "
            + "declare that name in the caller's scope, deciding for the caller what its own argument means. "
            + "Declare the name in the caller, or give the body's declaration a different name, such as a cheap "
            + "local.");

    internal static DiagnosticDescriptor RegisterName { get; } = Entry(
        Area.Names,
        "register-name",
        Severity.Error,
        "`{0}` is a register and cannot be a name",
        "Register names are reserved everywhere, because a symbol with the same name could not be told apart from "
            + "the register: `asl a` could mean the accumulator or a symbol called `a`. Mnemonics are not "
            + "reserved; register names are. Rename the symbol.");

    internal static DiagnosticDescriptor MnemonicName { get; } = Entry(
        Area.Names,
        "mnemonic-name",
        Severity.Warning,
        "`{0}` is legal as a name but reads as the {1} instruction",
        "No mnemonic is reserved: when a line's first word is followed by `:` or `=`, it declares that word as a "
            + "name, even if it is spelled like an instruction. ca65 cannot define a bare symbol spelled like an "
            + "instruction, so nt65 writes a top-level one with its module in front, `main__lda`, as it already "
            + "does for exported names. The program and its output are correct; the warning is there because a "
            + "reader sees an instruction. Rename the symbol, or set `\"mnemonic-name\"` to `\"off\"` or "
            + "`\"error\"` under `diagnostics` in nt65.json.");

    internal static DiagnosticDescriptor CheapLocalOutsideAScope { get; } = Entry(
        Area.Names,
        "cheap-local-outside-a-scope",
        Severity.Error,
        "`{0}` is a cheap local, which needs an enclosing `.proc` or `.scope`",
        "A `@name` is private to the routine or scope around it, so there has to be one for it to be private to.");

    internal static DiagnosticDescriptor CheapLocalInAPath { get; } = Entry(
        Area.Names,
        "cheap-local-in-a-path",
        Severity.Error,
        "`{0}` is a cheap local and cannot be reached with `::`",
        "A cheap local is private to the routine or scope that declares it, so no path can reach it from outside. "
            + "Use an ordinary label if other code needs to refer to it.");

    internal static DiagnosticDescriptor ModuleUsedAsAName { get; } = Entry(
        Area.Names,
        "module-used-as-a-name",
        Severity.Error,
        "`{0}` is a module, not a name: write `{1}::name` for a name in it",
        "A module is not a value and has no address. What is wanted is a name inside it.");

    internal static DiagnosticDescriptor NameAloneOnALine { get; } = Entry(
        Area.Names,
        "name-alone-on-a-line",
        Severity.Error,
        "`{0}` is {1}, and only a `block` parameter may stand alone on a line",
        "A name on a line by itself inserts a macro's `block` parameter at that point. Any other name needs more "
            + "on the line: `name:` declares a label, `name = value` a constant, and `name!` calls a macro.");

    internal static DiagnosticDescriptor NotAMacro { get; } = Entry(
        Area.Names,
        "not-a-macro",
        Severity.Error,
        "`{0}` is {1}, not a macro, so it cannot be called with `!`",
        "`!` after a name calls a macro, and marks the line as one whose code comes from somewhere else. Nothing "
            + "else is called that way: a routine is called with `jsr` or `jsl`.");

    internal static DiagnosticDescriptor DeclarationInABlockArgument { get; } = Entry(
        Area.Names,
        "declaration-in-a-block-argument",
        Severity.Error,
        "`{0}` is declared in a block argument, which may declare only cheap locals: the macro may insert the "
            + "block more than once",
        "A block argument may be spliced into a macro body in more than one place, and a declaration in it would "
            + "then be declared more than once. A cheap local is renamed per expansion, so it is safe.");

    internal static DiagnosticDescriptor ModuleMissing { get; } = Entry(
        Area.Names,
        "module-missing",
        Severity.Error,
        "this file has no `.module` line: start it with `.module name`",
        "Every file is one module and names it before anything else, with `.module name`, so that everything the "
            + "file declares has a path such as `name::symbol`.");

    internal static DiagnosticDescriptor MultiprocMisplaced { get; } = Entry(
        Area.Names,
        "multiproc-misplaced",
        Severity.Error,
        "`.multiproc` must be at file level or in a `.scope`, not inside {0}",
        "`.multiproc` declares one routine per member of an enum, so it follows the same placement rule as "
            + "`.proc`: at file level or inside a `.scope`, not inside a routine, a `.data` block or a type.");

    internal static DiagnosticDescriptor ModuleDeclaredTwice { get; } = Entry(
        Area.Names,
        "module-declared-twice",
        Severity.Error,
        "this file already has a `.module` line",
        "A file is one module. A large module is split into submodules, each its own file.");

    internal static DiagnosticDescriptor ModuleNotFirst { get; } = Entry(
        Area.Names,
        "module-not-first",
        Severity.Error,
        "`.module` must come before everything else in the file",
        "Everything a file declares belongs to the module it names, so the name comes before the declarations.");

    internal static DiagnosticDescriptor ModuleNameTaken { get; } = Entry(
        Area.Names,
        "module-name-taken",
        Severity.Error,
        "module `{0}` is already declared in `{1}`",
        "A module name is the path everything in it is reached by, and the name its output file is named after, so "
            + "two files may not share one. A large module is split into submodules, each its own file.");

    internal static DiagnosticDescriptor ModuleNameReserved { get; } = Entry(
        Area.Names,
        "module-name-reserved",
        Severity.Error,
        "module `{0}` is under `nt65`, which is reserved for the modules that come with nt65",
        "The modules under `nt65`, such as `nt65::cbm`, come with nt65 and join any program that names them. "
            + "A module of the program's own under that root could collide with one, now or in a later version.");

    internal static DiagnosticDescriptor ModuleNamesDifferInCase { get; } = Entry(
        Area.Names,
        "module-names-differ-in-case",
        Severity.Error,
        "modules `{0}` and `{1}` differ only in case, so a case-insensitive file system would write both to one file",
        "Output is named after the module, and on a file system that ignores case two such names are one file. "
            + "The build would depend on which ran last.");

    internal static DiagnosticDescriptor PlaceMisplaced { get; } = Entry(
        Area.Names,
        "place-misplaced",
        Severity.Error,
        "`.place` must be at file level, outside every block: which modules are assembled together cannot depend "
            + "on a condition",
        "Which modules make up one translation unit, and so one `.s` output file, is fixed by the files "
            + "themselves. A `.place` goes at file level, in a `.segment` region or before any, and never inside "
            + "an `.if`, a scope, a routine, a segment block, a repetition or a macro body. For code that only one "
            + "configuration needs, place the module unconditionally and put its contents under an `.if`.");

    internal static DiagnosticDescriptor PlaceNotPlaceable { get; } = Entry(
        Area.Names,
        "place-not-placeable",
        Severity.Error,
        "module `{0}` cannot be placed: declare it `.module {0}: placed`",
        "A module whose `.module` line has no marker is assembled on its own, into an output file of its own. A "
            + "module that another module places has to state that on its own `.module` line, so that its file can "
            + "be read correctly by itself: `placed` means another module always places it, and `placeable` means "
            + "at most one module may place it, and if none does, it gets its own output. The fix marks it `placed`.");

    internal static DiagnosticDescriptor PlacedTwice { get; } = Entry(
        Area.Names,
        "placed-twice",
        Severity.Error,
        "module `{0}` is already placed by `{1}`: a module can be placed only once",
        "A placed module's bytes are written where its `.place` is, so a module can have only one. Code that two "
            + "programs share is a module that each program places once.");

    internal static DiagnosticDescriptor PlacementCycle { get; } = Entry(
        Area.Names,
        "placement-cycle",
        Severity.Error,
        "this `.place` would make a module place itself: {0}",
        "A module and everything it places are laid out as one translation unit, in the order of the `.place` "
            + "lines, which a cycle cannot be.");

    internal static DiagnosticDescriptor PlacedNowhere { get; } = Entry(
        Area.Names,
        "placed-nowhere",
        Severity.Error,
        "module `{0}` is declared `placed`, but nothing places it: add `.place {0}` where its bytes belong, or declare it `placeable`",
        "A module declared `placed` has no output of its own: its bytes are written where another module places "
            + "it. One that nothing places would be written nowhere.");

    internal static DiagnosticDescriptor NameIsAModulePath { get; } = Entry(
        Area.Names,
        "name-is-a-module-path",
        Severity.Error,
        "`{1}` cannot declare `{2}`: `{0}` is already a module",
        "A path reaches either a module or a name in one, so a module may not declare a name that is already the "
            + "start of another module's path.");

    internal static DiagnosticDescriptor UseMisplaced { get; } = Entry(
        Area.Names,
        "use-misplaced",
        Severity.Error,
        "`.use` belongs at the top level of a module",
        "What a module brings in is part of its interface, so a `.use` belongs where the interface is: at the top "
            + "level, not under a block.");

    internal static DiagnosticDescriptor UseBringsInTwice { get; } = Entry(
        Area.Names,
        "use-brings-in-twice",
        Severity.Error,
        "a `.use` already brings in `{0}`",
        "Two `.use` items that bring in the same name would leave which one meant undecided. `as` gives one of "
            + "them a name of its own.");

    internal static DiagnosticDescriptor UseCollidesWithDeclaration { get; } = Entry(
        Area.Names,
        "use-collides-with-declaration",
        Severity.Error,
        "`.use` cannot bring in `{0}`: this module already declares it, so rename it with `as`",
        "Names brought in with `.use` share the file's namespace with its own declarations, so one name cannot "
            + "mean both. Use `.use module::name as other` to bring it in under a different name.");

    internal static DiagnosticDescriptor UseStarNotAModule { get; } = Entry(
        Area.Names,
        "use-star-not-a-module",
        Severity.Error,
        "`.use {0}::*` brings in what a module exports, and `{1}` is {2}",
        "`::*` brings in everything a module exports, so what is before it has to be a whole module name.");

    internal static DiagnosticDescriptor ReexportStar { get; } = Entry(
        Area.Names,
        "reexport-star",
        Severity.Error,
        "`.export .use` cannot re-export `*`: name each item",
        "A `.export .use` makes another module's names part of this module's interface. A `*` would make that "
            + "interface whatever the other module exports later, which is not a promise this module can keep.");

    internal static DiagnosticDescriptor ReexportModule { get; } = Entry(
        Area.Names,
        "reexport-module",
        Severity.Error,
        "`{0}` is a module: `.export .use` re-exports names in a module, not the module itself",
        "A path cannot end at a module, so there is nothing to re-export there. Re-export the names you want one "
            + "by one, `.export .use module::name`.");

    internal static DiagnosticDescriptor ReexportNeeded { get; } = Entry(
        Area.Names,
        "reexport-needed",
        Severity.Error,
        "`{0}` is declared in module `{1}`, not this one: re-export it with `.export .use {2}`",
        "A module can export only what it declares itself. A name brought in with `.use` still belongs to the "
            + "module that declares it; to make it part of this module's interface as well, re-export it with "
            + "`.export .use`, which records where it came from.");

    internal static DiagnosticDescriptor LabelOutsideARoutine { get; } = Entry(
        Area.Names,
        "label-outside-a-routine",
        Severity.Error,
        "`{0}` is a label outside a `.proc`{1}",
        "Labels belong inside a `.proc`, where they mark positions in code. Outside a routine, data is named by a "
            + "`.data` declaration, such as `.data name: .byte 1, 2`; the fix can rewrite the line as one.");

    internal static DiagnosticDescriptor LabelInData { get; } = Entry(
        Area.Names,
        "label-in-data",
        Severity.Error,
        "`{0}` is a label in `.data`: a named member is `.data {1}: ...`, and a position is `@{2}:`",
        "Inside mixed data (`.data name {`), a named member is declared with `.data member: ...`, which gives it a "
            + "type and a size, and a position is marked with a cheap local, `@here:`. A plain label is not "
            + "allowed there.");

    internal static DiagnosticDescriptor SegmentRegionMisplaced { get; } = Entry(
        Area.Names,
        "segment-region-misplaced",
        Severity.Error,
        "a `.segment NAME` line must be at file level; inside a block, use `.segment NAME {{ }}`",
        "A `.segment NAME` region sets where everything after it in the file goes, which only makes sense at file "
            + "level. Inside a block, the block form places what it holds.");

    internal static DiagnosticDescriptor SignatureSetNameIsAnItem { get; } = Entry(
        Area.Names,
        "signature-set-name-is-an-item",
        Severity.Error,
        "`{0}` is a signature item, and cannot name a signature set",
        "A signature set is a name for a list of processor-state items, and it is used in the same places as those "
            + "items. Its own name therefore cannot be an item such as `a8`, or a signature that used it would be "
            + "ambiguous. Choose another name.");

    internal static DiagnosticDescriptor SignatureItemNeeds65816 { get; } = Entry(
        Area.Names,
        "signature-item-needs-65816",
        Severity.Error,
        "`{0}` describes 65816 state, which the {1} does not have",
        "Widths, the emulation flag, the direct page and the data bank are the 65816's. Earlier processors have "
            + "eight-bit registers and none of that state.");

    internal static DiagnosticDescriptor FamilyMisplaced { get; } = Entry(
        Area.Names,
        "family-misplaced",
        Severity.Error,
        "{0}",
        "A family, a `.proc` or `.data` named after an `.each` binding, declares one name per enum member into the "
            + "scope around the `.each`. So the `.each` must be where those declarations could appear "
            + "directly, at file level or in a `.scope`, and must walk a named enum, whose member names become the "
            + "declared names.");

    internal static DiagnosticDescriptor FamilyNotOverAnEnum { get; } = Entry(
        Area.Names,
        "family-not-over-an-enum",
        Severity.Error,
        "a family needs a named enum to walk, and `{0}` {1}",
        "A family declares one routine or one data declaration per member of a named enum, and is named after the "
            + "members, so the enum has to be one whose members have names.");

    internal static DiagnosticDescriptor FamilyDeclaresTooMuch { get; } = Entry(
        Area.Names,
        "family-declares-too-much",
        Severity.Error,
        "`{0}` would declare one {1} per member: a family declares only routines and data, so declare one family per role, such as `note::{2}` and `stop::{3}`",
        "A family declares one routine or data declaration per enum member. A scope or `.data` block named after "
            + "the binding would repeat everything inside it once per member, which is really several families "
            + "in one. Declare one family per role instead, each inside the scope for that role.");

    internal static DiagnosticDescriptor FamilyMemberCollides { get; } = Entry(
        Area.Names,
        "family-member-collides",
        Severity.Error,
        "`{0}` is a member of `{1}` and is already declared in this scope",
        "Each instance of a family takes the member's name, so a name already declared in the scope would be "
            + "declared twice.");

    internal static DiagnosticDescriptor MacroMisplaced { get; } = Entry(
        Area.Names,
        "macro-misplaced",
        Severity.Error,
        "a `.macro` belongs at file level or in a `.scope`, not inside {0}",
        "A macro declared inside a routine could refer to that routine's cheap locals, and an expansion in another "
            + "routine would then branch into them, out of sight of the flow analysis. Declare macros at file "
            + "level or in a `.scope`.");

    internal static DiagnosticDescriptor ExportNarrowsAddressSize { get; } = Entry(
        Area.Names,
        "export-narrows-address-size",
        Severity.Error,
        "`{0}` is `{1}`, and an export cannot narrow it: write `{2}: {3}` or wider",
        "Other modules reach the name at the size the export gives, and reaching a two-byte address as if it were "
            + "one byte reads the wrong place. Widening is safe; narrowing is not.");

    internal static DiagnosticDescriptor LinkerNameIsAnInstruction { get; } = Entry(
        Area.Names,
        "linker-name-is-an-instruction",
        Severity.Error,
        "cannot export as `{0}`: ca65 would read it as an instruction",
        "An `as` name is written into the output exactly as given, with no module in front, and ca65 reads a line "
            + "that starts with an instruction name, for any CPU it supports, as an instruction, so it could never "
            + "define this symbol. Choose another linker name. This is an error, where `mnemonic-name` is only a "
            + "warning, because there is no other spelling to fall back on.");

    internal static DiagnosticDescriptor UnusedSymbol { get; } = Entry(
        Area.Names,
        "unused-symbol",
        Severity.Warning,
        "`{0}` is never used or exported",
        "Nothing in the program names the declaration and the file does not export it, so nothing reads it. Data "
            + "that holds values may be there for where it lands, so only a declaration that reserves storage is "
            + "reported. A declaration that another module names although it is not exported is "
            + "reported there instead, as an error.");

    internal static DiagnosticDescriptor UnusedUseItem { get; } = Entry(
        Area.Names,
        "unused-use-item",
        Severity.Warning,
        "`{0}` is brought in by `.use` and never used",
        "The `.use` brings the name in and the file never uses it. A `.export .use` re-exports rather than "
            + "uses, and is not reported.");

    // Values

    internal static DiagnosticDescriptor DefinedInTermsOfItself { get; } = Entry(
        Area.Values,
        "defined-in-terms-of-itself",
        Severity.Error,
        "`{0}` is defined in terms of itself",
        "The value depends on itself, directly or through other names, so it can never be worked out. Break the "
            + "cycle by defining one of the names without referring back to the others. The cycle is reported "
            + "once, at one of its declarations, and the other names in it are shown as related locations.");

    internal static DiagnosticDescriptor DefinedTooDeep { get; } = Entry(
        Area.Values,
        "defined-too-deep",
        Severity.Error,
        "`{0}` is defined through more than {1} other names",
        "nt65 works out a name's value by working out the names it uses first, and each of those in turn. It stops "
            + "at this depth, far beyond what a program needs, so that a generated chain of definitions cannot "
            + "overflow the stack and crash the assembler or the editor's language server. Define the name from a "
            + "value nearer the start of the chain.");

    internal static DiagnosticDescriptor NumberTooWide { get; } = Entry(
        Area.Values,
        "number-too-wide",
        Severity.Error,
        "{0} does not fit in 32 bits; ca65 takes -$80000000 to $ffffffff",
        "nt65 computes in 64-bit signed arithmetic, but ca65 computes in 32 bits, so every value that reaches the "
            + "output must fit ca65's range. The output writes a declaration, an operand or a data value as the "
            + "source has it, so each step of that expression is checked too. A name is checked where it is "
            + "declared, not again at every place it is used.");

    internal static DiagnosticDescriptor ArithmeticOverflow { get; } = Entry(
        Area.Values,
        "arithmetic-overflow",
        Severity.Error,
        "{0} overflows nt65's 64-bit signed arithmetic",
        "nt65 computes in 64-bit signed arithmetic. An operation whose result does not fit in 64 bits is an error "
            + "rather than wrapping around, because a wrapped result is almost never what was meant.");

    internal static DiagnosticDescriptor ShiftCountOutOfRange { get; } = Entry(
        Area.Values,
        "shift-count-out-of-range",
        Severity.Error,
        "shift count {0} is out of range: it must be 0 to 63",
        "Values are 64 bits wide, so `<<` and `>>` shift by 0 to 63 places. A count outside that range, including "
            + "a negative one, is an error rather than being reduced or clamped.");

    internal static DiagnosticDescriptor SqrtOfANegative { get; } = Entry(
        Area.Values,
        "sqrt-of-a-negative",
        Severity.Error,
        "`.sqrt({0})` has no result, because its argument is negative",
        "`.sqrt(n)` is the largest whole number whose square is at most n. No number has a negative square, so n "
            + "must be 0 or more.");

    internal static DiagnosticDescriptor StrsubOutOfRange { get; } = Entry(
        Area.Values,
        "strsub-out-of-range",
        Severity.Error,
        "`.strsub` reaches outside the text: it asks for {0}, and the text is {1}",
        "`.strsub(s, start, count)` is the `count` bytes of `s` starting at byte `start`, counting from 0. The "
            + "whole range must lie inside the text; nt65 reports an error rather than returning a shorter result.");

    internal static DiagnosticDescriptor StrcatNotAByte { get; } = Entry(
        Area.Values,
        "strcat-not-a-byte",
        Severity.Error,
        "{0} is not a byte: `.strcat` adds a number as one byte, 0 to 255",
        "`.strcat` joins texts, and a number among its arguments is added as one byte, as in `.strcat(\"A\", $80 | "
            + "'X')`. A number outside 0 to 255 does not fit in a byte, and nt65 does not truncate it. Mask it "
            + "with `& $ff`, or use `<` for its low byte, if that is what was meant.");

    internal static DiagnosticDescriptor TurnOrScaleOutOfRange { get; } = Entry(
        Area.Values,
        "turn-or-scale-out-of-range",
        Severity.Error,
        "`{0}` needs a turn from 1 to {1} and a scale from -{1} to {1}",
        "In `.sin(angle, turn, scale)` and `.cos(angle, turn, scale)`, `turn` is how many angle units make a full "
            + "circle and `scale` is the value the result has at 1.0. Both are limited to 32-bit values, because "
            + "anything written to the ca65 output has to fit in 32 bits anyway.");

    internal static DiagnosticDescriptor CyclesNeedsAPosition { get; } = Entry(
        Area.Values,
        "cycles-needs-a-position",
        Severity.Error,
        "`{0}` is {1}: a cycle count needs a label or routine name",
        "`.mincycles(from, to)` and `.maxcycles(from, to)` count the cycles one pass takes from one point in the "
            + "code to another, so each argument must be a label or a routine's name.");

    internal static DiagnosticDescriptor CyclesSpanHasNoBound { get; } = Entry(
        Area.Values,
        "cycles-span-has-no-bound",
        Severity.Error,
        "`{0}` cannot count these cycles: {1}",
        "`.mincycles` and `.maxcycles` follow every path from one position to another, which gives a true count "
            + "only when each path runs once. A call takes as long as the routine it calls and a loop repeats its body "
            + "an unknown number of times, so no path that arrives may make a call or loop. A jump nt65 cannot follow "
            + "could go anywhere, so no pass may reach one. Both positions must also be in the same routine and the "
            + "same segment block, and some path from the start must arrive at the end.");

    internal static DiagnosticDescriptor BuiltinArguments { get; } = Entry(
        Area.Values,
        "builtin-arguments",
        Severity.Error,
        "`{0}` takes {1}",
        "The built-in function was given the wrong number or kind of arguments. The message states what it takes.");

    internal static DiagnosticDescriptor DivisionByZero { get; } = Entry(
        Area.Values,
        "division-by-zero",
        Severity.Error,
        "division by zero",
        "nt65 evaluates constant expressions while it builds, and a division by zero has no value to write to the "
            + "output. Check the divisor.");

    internal static DiagnosticDescriptor FuncNotLinkable { get; } = Entry(
        Area.Values,
        "func-not-linkable",
        Severity.Error,
        "`{0}` is written for ld65 to work out, because its value depends on an address, and its body uses "
            + "`{1}`: {2}",
        "A `.func` called with an address, such as a label or a routine, has no value nt65 knows, so the output "
            + "writes the function's body with the argument in place of the parameter and leaves ld65 to work it "
            + "out once the address is known. ld65 works out the arithmetic, bitwise, shift, comparison and "
            + "logical operators and `<`, `>` and `^`, so `.func digit(n, place) = '0' + (n / place) .mod 10` can "
            + "be given a routine's address. A built-in function, `.in`, a charmap or text in the body is worked out "
            + "by nt65 before the output is written, and so needs every parameter it uses to be given a constant. "
            + "The body is written into the output of the file that calls it, so an address it names has to be "
            + "declared in that file.");

    internal static DiagnosticDescriptor OperatorOnText { get; } = Entry(
        Area.Values,
        "operator-on-text",
        Severity.Error,
        "`{0}` cannot be used on a string",
        "Text is a sequence of bytes for a data declaration to emit. The arithmetic and bitwise operators are on "
            + "numbers: `.strcat` joins texts, `.strsub` takes part of one, and `.strat` reads one of its bytes.");

    internal static DiagnosticDescriptor ScopeHasNoAddress { get; } = Entry(
        Area.Values,
        "scope-has-no-address",
        Severity.Error,
        "`{0}` is a scope and has no address: name a routine or data inside it instead",
        "A `.scope` only groups names and takes no bytes of its own, so there is no address to use. Name a routine "
            + "or a declaration inside it, such as `scope::name`.");

    internal static DiagnosticDescriptor EnumMemberIsNotAnAddress { get; } = Entry(
        Area.Values,
        "enum-member-is-not-an-address",
        Severity.Error,
        "`{0}` is an enum member, so its value must be a constant, not an address",
        "An enum member is a number, and a member with no value counts on from the one before, so a member cannot "
            + "be set to an address. Where an address was meant, use a constant or an address alias outside the "
            + "enum, or refer to the routine or data declared for that member.");

    internal static DiagnosticDescriptor NotIndexable { get; } = Entry(
        Area.Values,
        "not-indexable",
        Severity.Error,
        "`{0}` is {1}, so it cannot be indexed",
        "`[i]` selects the i-th element of a data declaration that holds a counted list of elements, such as "
            + "`.byte[8]` or `.type Point[4]`. Anything else, including mixed data, has no elements to select.");

    internal static DiagnosticDescriptor ElementIndexNotConstant { get; } = Entry(
        Area.Values,
        "element-index-not-constant",
        Severity.Error,
        "an element index must be a constant: to index at run time, use indexed addressing such as `{0},x`",
        "Which element `[i]` selects is decided while nt65 builds, because it becomes a fixed address in the "
            + "output. To choose an element while the program runs, use indexed addressing with the X or Y "
            + "register.");

    internal static DiagnosticDescriptor ElementIndexOutOfRange { get; } = Entry(
        Area.Values,
        "element-index-out-of-range",
        Severity.Error,
        "{0}",
        "The declaration states how many elements it holds, and this index is not one of them.");

    internal static DiagnosticDescriptor NothingToMeasure { get; } = Entry(
        Area.Values,
        "nothing-to-measure",
        Severity.Error,
        "`{0}` is {1} and takes no bytes of its own, so `{2}` has nothing to measure",
        "`.endof` and `.spanof` measure something that takes bytes in the output, such as a data declaration or a "
            + "routine. The name given refers to something that takes none, such as a constant, so there is "
            + "nothing to measure.");

    internal static DiagnosticDescriptor BankHasNoSegment { get; } = Entry(
        Area.Values,
        "bank-has-no-segment",
        Severity.Error,
        "{0}, so `.bankof` cannot find its bank",
        "`.bankof(name)` is the `bank` attribute that the linker configuration gives the memory area the name's "
            + "segment runs in, which is the area ca65's `.bank` reads, not the one it is loaded from. ld65 finds "
            + "the area from the segment, so the argument must be a routine, a label or data in a segment, not a "
            + "constant, a declaration at a constant address, a scope, an element or an expression.");

    internal static DiagnosticDescriptor CountofHasNoElements { get; } = Entry(
        Area.Values,
        "countof-has-no-elements",
        Severity.Error,
        "`{0}` is {1} and has no elements: use `.sizeof({2})` for its size in bytes",
        "`.countof` answers how many elements a counted declaration holds. Something that is bytes and not "
            + "elements is measured with `.sizeof`.");

    internal static DiagnosticDescriptor SizeofDependsOnExpansion { get; } = Entry(
        Area.Values,
        "sizeof-depends-on-expansion",
        Severity.Error,
        "cannot compute `.sizeof({0})`: it contains a macro call, which expands after constants are known; use `.spanof({1})` for its size at link time",
        "Constants and data sizes are worked out before any macro is expanded, and how many bytes a macro call in "
            + "a data declaration emits is known only once it is expanded. So the declaration's size is not a "
            + "constant. `.spanof` asks the linker for the size instead, which makes it a link-time value.");

    internal static DiagnosticDescriptor SizeofDependsOnAlignment { get; } = Entry(
        Area.Values,
        "sizeof-depends-on-alignment",
        Severity.Error,
        "cannot compute `.sizeof({0})`: its `.align` padding depends on where it is placed; use `.spanof({1})` for its size at link time",
        "An `.align` inside a declaration pads to the next boundary, and how much padding that takes depends on "
            + "where the linker places it. So the declaration's size is not a constant. `.spanof` asks the linker "
            + "for the size instead, which makes it a link-time value.");

    internal static DiagnosticDescriptor MeasuresADeclaration { get; } = Entry(
        Area.Values,
        "measures-a-declaration",
        Severity.Error,
        "`{0}` takes a whole declaration, not an element of one like `{1}`",
        "The measuring functions (`.sizeof`, `.countof`, `.endof`, `.spanof`) take the name of a whole "
            + "declaration. An indexed element such as `table[2]` is a position inside one and has no size of its "
            + "own; measure the element type instead, or the whole declaration.");

    internal static DiagnosticDescriptor NotMeasurable { get; } = Entry(
        Area.Values,
        "not-measurable",
        Severity.Error,
        "`{2}` cannot measure `{0}`, {1}: it measures data, routines and types",
        "The measuring functions take something that occupies bytes, such as a `.data` declaration or a routine, "
            + "or a type, which states how many bytes it takes. A label is only a position and a scope only groups "
            + "names, so neither has a size. An import can be measured once it states its type, as in `.import "
            + "name: .byte[n]`.");

    internal static DiagnosticDescriptor CharmapArgumentNamed { get; } = Entry(
        Area.Values,
        "charmap-argument-named",
        Severity.Error,
        "charmap `{0}` takes its text by position, not by name",
        "A charmap is applied as a call, `screen(\"HI\")`, but it is not a `.func` and has no named parameters, so "
            + "its one argument is given without a name.");

    internal static DiagnosticDescriptor ConditionIsText { get; } = Entry(
        Area.Values,
        "condition-is-text",
        Severity.Error,
        "an `.if` condition must be a number, not text",
        "An `.if` or `.elseif` condition is true when its value is nonzero, so it must evaluate to a number.");

    internal static DiagnosticDescriptor ConstantNamesAnAddress { get; } = Entry(
        Area.Values,
        "constant-names-an-address",
        Severity.Error,
        "`{0}` is an address, so it cannot be a `.const`; declare it with `.data`, or with `.proc` for a routine",
        "A `.const` is a number or a text. A name for an address is declared as what is at the address: "
            + "`.data name = expr` for data, which takes the element type at the address or states its own, and "
            + "`.proc name = expr` for a routine. The distance between two addresses is a number, so a `.const` may "
            + "hold it.");

    internal static DiagnosticDescriptor DataHasNoElementType { get; } = Entry(
        Area.Values,
        "data-has-no-element-type",
        Severity.Error,
        "`{0}` has no element type; give it one on its declaration, such as `: .byte`",
        "Data found elsewhere that states no element type takes the one at its address. The name of data gives "
            + "that data's element type, and an offset that lands on the start of an element, or on a field, gives "
            + "that element's or field's. Any other address, such as code, a number or an offset into the middle of "
            + "an element, gives none. Such a name is still an address that instructions and data can use, but its "
            + "size, its count, its elements and its fields are unknown until its declaration states an element "
            + "type.");

    internal static DiagnosticDescriptor MmioNeedsAnAddress { get; } = Entry(
        Area.Values,
        "mmio-needs-an-address",
        Severity.Error,
        "`.mmio {0}` needs an address: write `.mmio {0}: .byte = $address`",
        "A memory-mapped register is not laid out by the program. It is found at the address the hardware gives it, "
            + "so `.mmio` takes only the form of data found elsewhere, `.mmio name: element = address`. Everything "
            + "else about it is as `.data` at that address would be. The difference is that what it holds is set by "
            + "the hardware, so the editor does not trace its value back to the program's stores.");

    internal static DiagnosticDescriptor DataElsewhereOverruns { get; } = Entry(
        Area.Values,
        "data-elsewhere-overruns",
        Severity.Warning,
        "`{0}` {1}",
        "Data found elsewhere may state an element type different from the one at its address, to read the same "
            + "bytes another way. When the data at the address has fewer bytes left than that element type takes, "
            + "the name reaches into whatever follows it. State an element type that fits, or leave it out if the "
            + "name is only used as an address.");

    internal static DiagnosticDescriptor ConditionUsesAMeasurement { get; } = Entry(
        Area.Values,
        "condition-uses-a-measurement",
        Severity.Error,
        "an `.if` cannot test {0}, which measures a declaration; check it with `.assert`",
        "An `.if` decides which declarations exist, so nt65 answers it before it reads any declaration. It can test "
            + "only what the configuration decides: literals, settings, built-ins such as `.target`, and the constants, "
            + "functions and enum members declared at file level from those. A size, an offset, a count or a distance "
            + "inside data is known only once the declarations are read. The notes lead from the value tested to the "
            + "measurement. Check such a value with `.assert`, or choose with it using `.select` or `.switch`.");

    internal static DiagnosticDescriptor ConditionUsesAConditionalDeclaration { get; } = Entry(
        Area.Values,
        "condition-uses-a-conditional-declaration",
        Severity.Error,
        "an `.if` cannot test {0}, which is declared {1}",
        "An `.if` can test a constant only when the constant is declared once, at file level and outside every "
            + "block, from values the configuration decides. A constant under an `.if` exists only in the builds that "
            + "take that branch, so testing it would make one condition depend on another. Declare the value once at "
            + "file level, and choose between its values with `.select` where they differ between builds.");

    internal static DiagnosticDescriptor ConditionNamesTheProgram { get; } = Entry(
        Area.Values,
        "condition-names-the-program",
        Severity.Error,
        "an `.if` cannot test {0}, which is part of the program; check it with `.assert`",
        "An `.if` decides which declarations exist, so nt65 answers it before it reads any declaration. An address "
            + "is known only once the linker places the program, and a routine, data, a macro and a type are not values. "
            + "Check something about the program with `.assert`, which is evaluated once the program is complete.");

    internal static DiagnosticDescriptor SelectArguments { get; } = Entry(
        Area.Values,
        "select-arguments",
        Severity.Error,
        "`.select` takes a condition and the two values it chooses between: `.select(c, a, b)`",
        "`.select(c, a, b)` is `a` when `c` is nonzero and `b` otherwise, so it takes exactly three arguments.");

    internal static DiagnosticDescriptor SelectConditionIsText { get; } = Entry(
        Area.Values,
        "select-condition-is-text",
        Severity.Error,
        "a `.select` condition must be a number, not text",
        "`.select(c, a, b)` chooses `a` when `c` is nonzero and `b` otherwise, so `c` must evaluate to a number.");

    internal static DiagnosticDescriptor SelectConditionNotConstant { get; } = Entry(
        Area.Values,
        "select-condition-not-constant",
        Severity.Error,
        "a `.select` condition must be a constant",
        "Which of the two values a `.select` gives depends on its condition, so the condition has to be known "
            + "while nt65 builds, not only at link time or at run time.");

    internal static DiagnosticDescriptor SwitchArguments { get; } = Entry(
        Area.Values,
        "switch-arguments",
        Severity.Error,
        "`.switch` takes a value, a set and a result for each arm, and an optional default: `.switch(v, [a, b], x, [c], y, otherwise)`",
        "`.switch(v, [a, b], x, [c], y, otherwise)` is `x` when `v` is `a` or `b`, `y` when it is `c`, and `otherwise` "
            + "when it is none of them. It takes a value and at least one arm.");

    internal static DiagnosticDescriptor SwitchValueNotConstant { get; } = Entry(
        Area.Values,
        "switch-value-not-constant",
        Severity.Error,
        "the value a `.switch` tests must be a constant",
        "Which result a `.switch` gives depends on the value it tests, so that value has to be known while nt65 "
            + "builds, not only at link time or at run time.");

    internal static DiagnosticDescriptor SwitchSetNotConstant { get; } = Entry(
        Area.Values,
        "switch-set-not-constant",
        Severity.Error,
        "the values in the sets of a `.switch` must be constants",
        "A `.switch` chooses its result while nt65 builds, so every value its sets are compared with, up to the arm "
            + "that holds the value it tests, has to be known then.");

    internal static DiagnosticDescriptor SwitchNoArm { get; } = Entry(
        Area.Values,
        "switch-no-arm",
        Severity.Error,
        "no arm of this `.switch` holds {0}, and it has no default result",
        "A `.switch` without a last result for otherwise must have an arm for every value it is given. Add the "
            + "value to an arm's set, or end the `.switch` with a result for the values no arm holds.");

    internal static DiagnosticDescriptor SetExpected { get; } = Entry(
        Area.Values,
        "set-expected",
        Severity.Error,
        "{0} takes a set in brackets, such as `[a, b, c..d]`, or the name of a `.list`",
        "A set is written as values and ranges in brackets, `[Mode::zpx, $00..$3f]`, or is a `.list` whose items "
            + "are its values.");

    internal static DiagnosticDescriptor SetOutOfPlace { get; } = Entry(
        Area.Values,
        "set-out-of-place",
        Severity.Error,
        "a bracketed set can appear only after `.in` or as a `.switch` arm",
        "A set has no value of its own. `v .in [a, b]` asks whether it holds `v`, and `.switch` chooses a result by "
            + "the first of its sets that holds a value.");

    internal static DiagnosticDescriptor SetItemIsText { get; } = Entry(
        Area.Values,
        "set-item-is-text",
        Severity.Error,
        "a set holds numbers and names, not text",
        "`.in` and `.switch` compare numbers, and the words a `one(...)` parameter holds. Text is not compared.");

    internal static DiagnosticDescriptor CpuUnderACondition { get; } = Entry(
        Area.Values,
        "cpu-under-a-condition",
        Severity.Error,
        "`.cpu` cannot be inside an `.if`: conditions can test the processor, so it must be set first",
        "Conditions can test the processor with `.target` and `.has`, so the processor has to be known before any "
            + "condition is evaluated. Put `.cpu` outside every `.if`, or set the processor in the project file "
            + "or on the command line.");

    internal static DiagnosticDescriptor CpuDisagrees { get; } = Entry(
        Area.Values,
        "cpu-disagrees",
        Severity.Error,
        "`.cpu {1}` conflicts with this build's processor, the {0}",
        "A program is built for one processor. When the project file, the command line or another file sets it, a "
            + "`.cpu` in a source file must name the same processor. Change or remove the `.cpu`.");

    internal static DiagnosticDescriptor ElseWithoutIf { get; } = Entry(
        Area.Values,
        "else-without-if",
        Severity.Error,
        "`{0}` has no `.if` before it",
        "An `.elseif` or an `.else` follows the block of an `.if` or `.elseif`, and there is none open here. Check "
            + "for a missing `.if` or a misplaced closing brace.");

    internal static DiagnosticDescriptor SettingMisplaced { get; } = Entry(
        Area.Values,
        "setting-misplaced",
        Severity.Error,
        "a setting must be at file level, outside every block",
        "The build names a setting from outside the program, by its module's path, so a setting must be one "
            + "declaration that exists in every build. Which settings a module has cannot depend on a condition or "
            + "on another declaration around it, so a setting goes at the top level of the file. A value the build "
            + "is not meant to set is declared with `=`.");

    internal static DiagnosticDescriptor SettingIsText { get; } = Entry(
        Area.Values,
        "setting-is-text",
        Severity.Error,
        "a setting must be a number, not text",
        "A setting is a number, so that the project file or the command line can set it.");

    internal static DiagnosticDescriptor SettingDefaultUndecided { get; } = Entry(
        Area.Values,
        "setting-default-undecided",
        Severity.Error,
        "the default of `{0}` must be known before any declaration is read",
        "Any condition may test a setting, and conditions are answered before any declaration is read. So a "
            + "setting's default may use only literals, built-ins, other settings, and the constants and functions "
            + "declared at file level from those. The notes lead to the value the configuration does not decide.");

    internal static DiagnosticDescriptor SettingUnknown { get; } = Entry(
        Area.Values,
        "setting-unknown",
        Severity.Error,
        "`{0}` is not a setting of any module",
        "A name under `settings` in the project file, or after `-D` on the command line, sets a setting a module "
            + "declares with `.const NAME ?= value`. It is the setting's path, as in `hw::SOUND`, or its name alone "
            + "where only one module declares a setting of that name. No module declares a setting by this name; "
            + "check the spelling and the module path.");

    internal static DiagnosticDescriptor SettingAmbiguous { get; } = Entry(
        Area.Values,
        "setting-ambiguous",
        Severity.Error,
        "more than one module has a setting `{0}`; name it by its path, as {1}",
        "A setting given by its name alone must be the only setting of that name in the program. Where two modules "
            + "declare one, the build names the one it means by the module's path, as in `hw::PAL`.");

    internal static DiagnosticDescriptor DeclarationInARepetition { get; } = Entry(
        Area.Values,
        "declaration-in-a-repetition",
        Severity.Error,
        "{0} cannot be inside a `.repeat` or `.each` body: {1}",
        "A `.repeat` or `.each` body is expanded once per iteration. Exports, imports, routines, segments and "
            + "definitions each name one thing for the whole program, so declaring them again on every iteration "
            + "makes no sense. Move the statement outside the body.");

    internal static DiagnosticDescriptor RepeatCountNotConstant { get; } = Entry(
        Area.Values,
        "repeat-count-not-constant",
        Severity.Error,
        "a `.repeat` count must be a constant",
        "How many times the body is expanded decides what the program contains, so the count has to be known "
            + "while nt65 builds.");

    internal static DiagnosticDescriptor RepeatCountNegative { get; } = Entry(
        Area.Values,
        "repeat-count-negative",
        Severity.Error,
        "a `.repeat` count cannot be negative, and this one is {0}",
        "A repetition runs its body a whole number of times, and that number cannot be negative.");

    internal static DiagnosticDescriptor EachNotOverAList { get; } = Entry(
        Area.Values,
        "each-not-over-a-list",
        Severity.Error,
        "`.each` walks a list or an enum, and this is neither",
        "`.each` walks something with items in a fixed order: a `.list`, or the members of an enum.");

    internal static DiagnosticDescriptor BindingNotOverAnEnum { get; } = Entry(
        Area.Values,
        "binding-not-over-an-enum",
        Severity.Error,
        "`{0}` cannot end a path: only the binding of an `.each` over an enum can",
        "Inside `.each Enum, e`, a path such as `handlers::e` names the member of `handlers` that has the same "
            + "name as the current enum member. When `.each` iterates over a `.list` or a list parameter, its "
            + "variable stands for a value rather than a name, so it cannot be used at the end of a path.");

    internal static DiagnosticDescriptor FamilyMemberMissing { get; } = Entry(
        Area.Values,
        "family-member-missing",
        Severity.Error,
        "`{0}` has no member `{1}`, which `{2}` names on this iteration",
        "Inside `.each Enum, e`, a path `container::e` names the member of `container` with the same name as the "
            + "current enum member. `container` has no member of that name; every enum member needs a matching "
            + "declaration inside it.");

    internal static DiagnosticDescriptor IncbinNotConstant { get; } = Entry(
        Area.Values,
        "incbin-not-constant",
        Severity.Error,
        "the {0} of an `.incbin` must be a constant",
        "How many bytes an `.incbin` takes decides where everything after it goes, so where it starts in the "
            + "file and how much of it it takes are decided while nt65 builds.");

    internal static DiagnosticDescriptor IncbinOutOfRange { get; } = Entry(
        Area.Values,
        "incbin-out-of-range",
        Severity.Error,
        "`.incbin` reaches outside `{0}`: it asks for {1}, and the file is {2} bytes long",
        "An `.incbin` offset starts at 0 and may be as large as the file, and a length may not run past the file's "
            + "end. ca65 refuses both, and so does nt65, where it can say which line asked.");

    internal static DiagnosticDescriptor IncbinUnreadable { get; } = Entry(
        Area.Values,
        "incbin-unreadable",
        Severity.Error,
        "cannot read `{0}` for `.incbin`",
        "nt65 reads an `.incbin` file to learn how many bytes it takes. The path is relative to the source file "
            + "that names it, and there the file is missing or cannot be read. ca65 reads the file again when it "
            + "assembles the output.");

    internal static DiagnosticDescriptor CharmapHasNoEntry { get; } = Entry(
        Area.Values,
        "charmap-has-no-entry",
        Severity.Error,
        "charmap `{0}` has no entry for `{1}`",
        "A charmap applied to text must map every character in it; nt65 does not fall back to ASCII for a "
            + "character the charmap leaves out. Add an entry for the character, or remove it from the text.");

    internal static DiagnosticDescriptor MemberHasNoValue { get; } = Entry(
        Area.Values,
        "member-has-no-value",
        Severity.Error,
        "`{0}` is a struct member and cannot have a value: for several `{1}` elements, use `{1}[n]`",
        "A `.struct` or `.union` member only reserves room at an offset and holds no data, so a value "
            + "after its type is an error. `colors: .word 16` would reserve one word, not sixteen; several "
            + "elements of one type take a count, `colors: .word[16]`.");

    internal static DiagnosticDescriptor MemberCountNotANumber { get; } = Entry(
        Area.Values,
        "member-count-not-a-number",
        Severity.Error,
        "`{0}` needs an element count: use `{1}[n]`",
        "How many elements a member holds is part of its type, so the count must be given as a number. `[]`, "
            + "which counts the values given, has nothing to count in a `.struct`.");

    internal static DiagnosticDescriptor MemberReservesNothing { get; } = Entry(
        Area.Values,
        "member-reserves-nothing",
        Severity.Error,
        "`{0}` reserves no room: declare a member with a type such as `.byte`, `.word[n]` or `.res n`",
        "A `.struct` member only reserves room at its offset, so it is declared with an element type or with "
            + "`.res`. A directive that emits data, such as `.strz`, reserves nothing a member can use.");

    internal static DiagnosticDescriptor TargetArgument { get; } = Entry(
        Area.Values,
        "target-argument",
        Severity.Error,
        "`.target` takes {0}",
        "`.target` asks whether the program is built for one named processor, spelled as in the project "
            + "file.");

    internal static DiagnosticDescriptor HasArgument { get; } = Entry(
        Area.Values,
        "has-argument",
        Severity.Error,
        "`.has` takes a mnemonic, such as `.has(phx)`",
        "`.has(mnemonic)` asks whether the processor the program is built for has an instruction. Testing for the "
            + "instruction, rather than listing the processors that have it, keeps code that targets several "
            + "processors correct.");

    internal static DiagnosticDescriptor OpcodeArgument { get; } = Entry(
        Area.Values,
        "opcode-argument",
        Severity.Error,
        "`.opcode` takes a mnemonic and, where it has several forms, a mode, such as `.opcode(dex)` or `.opcode(lda, absx)`",
        "`.opcode(mnemonic, mode)` is the byte an instruction is written as on the processor the program is built for. "
            + "The mode is one of `imm`, `acc`, `zp`, `zpx`, `zpy`, `abs`, `absx`, `absy`, `ind`, `indx`, `indy`, `sr`, "
            + "`sry`, `long`, `longy`, `far` and `farx`. `long` is the `[ptr]` form, as `.mode` names it, and `far` the "
            + "`f:` address. The mode may be left out where the instruction has one form, or a form with no operand "
            + "or with `a`, which is then the form it names.");

    internal static DiagnosticDescriptor OpcodeForm { get; } = Entry(
        Area.Values,
        "opcode-form",
        Severity.Error,
        "`.opcode({0})` names no instruction the {1} has: {2}",
        "`.opcode` gives the byte of an instruction form the processor has. A form the processor lacks has no byte, "
            + "and an instruction with several forms needs the mode that picks one.");

    // Macros

    internal static DiagnosticDescriptor DeclarationInAMacroBody { get; } = Entry(
        Area.Macros,
        "declaration-in-a-macro-body",
        Severity.Error,
        "{0} cannot be inside a macro body: {1}",
        "A macro body is expanded at each call, in the module that calls it. These statements would either "
            + "declare a name in the caller or make something that exists once for the whole program depend on how "
            + "many times the macro is called. Move the statement outside the macro.");

    internal static DiagnosticDescriptor MacroRecursive { get; } = Entry(
        Area.Macros,
        "macro-recursive",
        Severity.Error,
        "`{0}` calls itself{1}: a macro cannot be recursive",
        "Every macro expansion must be finite, and nt65 does not use a depth limit to stop one, so a macro may not "
            + "call itself, directly or through other macros. To handle a variable number of arguments, use a "
            + "`list` parameter with `.each` instead of recursion.");

    internal static DiagnosticDescriptor MacroNamesUnexported { get; } = Entry(
        Area.Macros,
        "macro-names-unexported",
        Severity.Error,
        "exported macro `{0}!` uses `{1}`, which is not exported, so callers in other modules cannot reach it",
        "An exported macro expands in the module that calls it, so every name its body uses must be reachable from "
            + "there. Export the name it uses, or stop exporting the macro.");

    internal static DiagnosticDescriptor ParameterUnknown { get; } = Entry(
        Area.Macros,
        "parameter-unknown",
        Severity.Error,
        "`{0}` has no parameter called `{1}`",
        "A named argument names a parameter that the macro or the `.func` declares.");

    internal static DiagnosticDescriptor BlockParameterUnknown { get; } = Entry(
        Area.Macros,
        "block-parameter-unknown",
        Severity.Error,
        "`{0}` has no `block` parameter called `{1}`",
        "A block argument after the parentheses names a `block` parameter the macro declares.");

    internal static DiagnosticDescriptor ArgumentGivenTwice { get; } = Entry(
        Area.Macros,
        "argument-given-twice",
        Severity.Error,
        "parameter `{0}` is given twice",
        "Each parameter takes one argument, either by position or by name, not both.");

    internal static DiagnosticDescriptor ArgumentMissing { get; } = Entry(
        Area.Macros,
        "argument-missing",
        Severity.Error,
        "`{0}` needs an argument for {1}",
        "A parameter with no default must be given an argument at every call, by position or by name. This holds "
            + "for a macro's parameters and a `.func`'s alike.");

    internal static DiagnosticDescriptor ArgumentAfterANamedOne { get; } = Entry(
        Area.Macros,
        "argument-after-a-named-one",
        Severity.Error,
        "a positional argument cannot follow a named one",
        "Positional arguments are matched to parameters in order, so they must all come before any named argument. "
            + "Move this argument before the named ones, or give it by name.");

    internal static DiagnosticDescriptor ArgumentCount { get; } = Entry(
        Area.Macros,
        "argument-count",
        Severity.Error,
        "`{0}` takes {1}, and this call gives {2}",
        "The call gives more positional arguments than the macro or the `.func` has parameters to bind them to.");

    internal static DiagnosticDescriptor BlockArgumentInParentheses { get; } = Entry(
        Area.Macros,
        "block-argument-in-parentheses",
        Severity.Error,
        "`{0}` is a `block` parameter: put its block after the parentheses, not inside them",
        "A block argument goes after the call's parentheses, as `name!(...) { ... }`, where it reads like "
            + "any other block of code.");

    internal static DiagnosticDescriptor BlockArgumentUnexpected { get; } = Entry(
        Area.Macros,
        "block-argument-unexpected",
        Severity.Error,
        "`{0}` takes no block, and this call gives it one",
        "The macro declares no `block` parameter, so there is nowhere for the block to go.");

    internal static DiagnosticDescriptor BlockContinuesNothing { get; } = Entry(
        Area.Macros,
        "block-continues-nothing",
        Severity.Error,
        "this block argument does not follow a macro call",
        "`} name {` closes one block argument of a macro call and opens the next. There is no macro call before it "
            + "for the block to belong to.");

    internal static DiagnosticDescriptor BlockChangesState { get; } = Entry(
        Area.Macros,
        "block-changes-state",
        Severity.Error,
        "the block given to `{0}!` must leave the processor state as it found it: it starts with `{1}` and ends "
            + "with `{2}`",
        "A macro that takes a block emits its own code around the block, and that code assumes the register "
            + "widths and mode are the same after the block as before it. Restore them at the end of the block.");

    internal static DiagnosticDescriptor ParameterAfterBlock { get; } = Entry(
        Area.Macros,
        "parameter-after-block",
        Severity.Error,
        "`{0}` comes after the `block` parameter `{1}`, and a block goes after the parentheses",
        "A block goes after the parentheses, so a `block` parameter is the last one.");

    internal static DiagnosticDescriptor ParameterAfterList { get; } = Entry(
        Area.Macros,
        "parameter-after-list",
        Severity.Error,
        "`{0}` comes after the `list` parameter `{1}`, which takes every remaining argument",
        "A `list` parameter takes every remaining argument, so nothing after it could ever be given one.");

    internal static DiagnosticDescriptor OperandArgumentParenthesized { get; } = Entry(
        Area.Macros,
        "operand-argument-parenthesized",
        Severity.Error,
        "`{0}` takes an operand, and `{1}` is read as an expression in parentheses: put it in braces to pass "
            + "indirect addressing",
        "Without braces, `(ptr)` is an expression in parentheses, as it is everywhere else. To pass a whole "
            + "operand, including indirection and an index register, put it in braces, as in `load!({(ptr),y})`.");

    internal static DiagnosticDescriptor WordArgumentNotListed { get; } = Entry(
        Area.Macros,
        "word-argument-not-listed",
        Severity.Error,
        "`{0}` takes one of {1}, and this is {2}",
        "A `one(...)` parameter takes one of the words it lists, and the words are never looked up.");

    internal static DiagnosticDescriptor WordArgumentAmbiguous { get; } = Entry(
        Area.Macros,
        "word-argument-ambiguous",
        Severity.Error,
        "`{0}` takes only {1}, but `{2}` can also be {3}",
        "The argument is a `one(...)` parameter of the enclosing macro, passed on. Which word it holds is known "
            + "only when that macro is called, so every word it allows must be one this parameter accepts. Narrow "
            + "the enclosing parameter's list, or add the missing words to this one.");

    internal static DiagnosticDescriptor IdentArgumentNotAName { get; } = Entry(
        Area.Macros,
        "ident-argument-not-a-name",
        Severity.Error,
        "`{0}` takes a name, and this is not one",
        "An `ident` parameter stands for a name the body declares or uses, so the argument has to be one.");

    internal static DiagnosticDescriptor ExpressionArgumentBraced { get; } = Entry(
        Area.Macros,
        "expression-argument-braced",
        Severity.Error,
        "`{0}` takes an expression, so its argument cannot be in braces: braces pass a whole operand",
        "Braces pass a whole operand, with its addressing mode, to an `operand` parameter. A parameter that takes "
            + "an expression takes it without braces.");

    internal static DiagnosticDescriptor ConstArgumentOutOfRange { get; } = Entry(
        Area.Macros,
        "const-argument-out-of-range",
        Severity.Error,
        "`{0}` takes a constant from {1} to {2}, and {3}",
        "A `const(low..high)` parameter states the range it takes, so a call outside it is reported at the call, with "
            + "the limits, rather than by an `.assert` in the body.");

    internal static DiagnosticDescriptor EnumArgumentNotAMember { get; } = Entry(
        Area.Macros,
        "enum-argument-not-a-member",
        Severity.Error,
        "`{0}` takes a member of `{1}`, and {2}",
        "A parameter whose kind is an enum takes one of its members, by its bare name or its path, and nothing "
            + "else of the same value.");

    internal static DiagnosticDescriptor OperandArgumentMode { get; } = Entry(
        Area.Macros,
        "operand-argument-mode",
        Severity.Error,
        "`{0}` takes {1}, and this is `{2}`",
        "An `operand(...)` parameter lists the addressing modes it takes, as `.mode` names them, with `zp`, `zpx` "
            + "and `zpy` for a direct-page address, so a call in another mode is reported at the call.");

    internal static DiagnosticDescriptor ParameterRangeInvalid { get; } = Entry(
        Area.Macros,
        "parameter-range-invalid",
        Severity.Error,
        "`{0}` is not a range: a `const` range is two constants, the lower first",
        "The range a `const(low..high)` parameter takes is worked out once, where the macro is declared.");

    internal static DiagnosticDescriptor ParameterKindNotAnEnum { get; } = Entry(
        Area.Macros,
        "parameter-kind-not-an-enum",
        Severity.Error,
        "`{0}` is {1}, not a parameter kind: use a kind word or an enum",
        "A name after a parameter's `:` that is not one of the kinds' words names the enum whose members the "
            + "parameter takes.");

    internal static DiagnosticDescriptor OperandModeUnknown { get; } = Entry(
        Area.Macros,
        "operand-mode-unknown",
        Severity.Error,
        "`{0}` is not an addressing mode: use {1}",
        "The modes are the words `.mode` gives, and `zp`, `zpx` and `zpy` for the direct-page addresses among "
            + "`abs`, `absx` and `absy`.");

    internal static DiagnosticDescriptor ComparisonNeverHolds { get; } = Entry(
        Area.Macros,
        "comparison-never-holds",
        Severity.Warning,
        "`{0}` is never `{1}`, so this comparison {2}: {3}",
        "A word compared with `.mode(p)` or with a `one(...)` parameter is not looked up, so a misspelt word, or "
            + "one the parameter can never be, is not an error by itself: the comparison just always gives the "
            + "same answer, and the branch is silently never or always taken. The words `.mode` can give are "
            + "listed in the message; for an `operand(...)` parameter that lists its modes, only those are "
            + "possible, with `zp`, `zpx` and `zpy` reported as `abs`, `absx` and `absy`.");

    internal static DiagnosticDescriptor ExpansionLimit { get; } = Entry(
        Area.Macros,
        "expansion-limit",
        Severity.Error,
        "macro expansions in this file exceed the limit of {0} statements",
        "Expansion is bounded, and the bound is far beyond any program typed by hand. Reaching it means a "
            + "repetition or a nest of macros is multiplying out further than was meant.");

    internal static DiagnosticDescriptor RepeatTooMany { get; } = Entry(
        Area.Macros,
        "repeat-too-many",
        Severity.Error,
        "this repetition runs {0} times, more than the limit of {1}",
        "nt65 limits how many times a `.repeat` or `.each` body can be expanded, which bounds how much one line "
            + "of source can expand to. The limit is far beyond anything typed by hand; reaching it usually "
            + "means the count is wrong.");

    // Data

    internal static DiagnosticDescriptor ElementCountEmpty { get; } = Entry(
        Area.Data,
        "element-count-empty",
        Severity.Error,
        "`[]` counts the values given, and there are none: use `{0}[n]` to reserve n elements",
        "`[]` means as many elements as there are values, and no values are given. To reserve room without giving "
            + "values, give the count, as in `.byte[16]`; the elements are filled with zeros.");

    internal static DiagnosticDescriptor ElementCountNotConstant { get; } = Entry(
        Area.Data,
        "element-count-not-constant",
        Severity.Error,
        "an array's element count must be a constant",
        "How many elements a declaration holds decides how many bytes it takes, so the count has to be known while "
            + "nt65 builds.");

    internal static DiagnosticDescriptor ElementCountNegative { get; } = Entry(
        Area.Data,
        "element-count-negative",
        Severity.Error,
        "an array's count cannot be negative, and this one is {0}",
        "A count is how many elements the declaration holds, and a negative number of them is not one.");

    internal static DiagnosticDescriptor ElementCountMismatch { get; } = Entry(
        Area.Data,
        "element-count-mismatch",
        Severity.Error,
        "this array declares {0} {1}, but {2} {3} given",
        "A declaration with a count holds exactly that many elements: nt65 neither pads missing values nor drops "
            + "extra ones. Correct the count, or use `[]` to take the count from the values.");

    internal static DiagnosticDescriptor ElementNotAValue { get; } = Entry(
        Area.Data,
        "element-not-a-value",
        Severity.Error,
        "a `{0}` element is a single value, not a braced record or list",
        "Braces hold a record or a list, and an element of this type is one plain value. Remove the braces.");

    internal static DiagnosticDescriptor ElementNotARecord { get; } = Entry(
        Area.Data,
        "element-not-a-record",
        Severity.Error,
        "each element of {0} is {1}, given as `{{ member = value }}`",
        "Each element of an array of records is given as a record, so that which member each value goes to is "
            + "stated.");

    internal static DiagnosticDescriptor ElementIsOneValue { get; } = Entry(
        Area.Data,
        "element-is-one-value",
        Severity.Error,
        "each element of `{0}` is a single `{1}` value, not a braced record or list",
        "The member holds elements of a plain type, and each of them is one value. Remove the braces around the "
            + "element.");

    internal static DiagnosticDescriptor MemberUnknown { get; } = Entry(
        Area.Data,
        "member-unknown",
        Severity.Error,
        "`{0}` has no member `{1}`",
        "A record initializer names the members of the type it initializes.");

    internal static DiagnosticDescriptor MemberGivenTwice { get; } = Entry(
        Area.Data,
        "member-given-twice",
        Severity.Error,
        "`{0}` is given a value twice",
        "A record initializer gives each member at most one value. Remove the duplicate.");

    internal static DiagnosticDescriptor UnionManyMembersGiven { get; } = Entry(
        Area.Data,
        "union-many-members-given",
        Severity.Error,
        "`{0}` is a union, so it takes a value for only one member",
        "A union's members share the same bytes, so giving two of them values would write each over the other.");

    internal static DiagnosticDescriptor MemberNeedsARecord { get; } = Entry(
        Area.Data,
        "member-needs-a-record",
        Severity.Error,
        "`{0}` is a `{1}` record, so its value goes in braces: `{{ member = value }}`",
        "The member is itself a record, so its value is given as one, naming its members.");

    internal static DiagnosticDescriptor MemberNeedsAList { get; } = Entry(
        Area.Data,
        "member-needs-a-list",
        Severity.Error,
        "`{0}` is an array, which takes a braced list: `{1} = {{ … }}`",
        "The member holds several elements, so its value is a braced list of them.");

    internal static DiagnosticDescriptor MemberTakesOneValue { get; } = Entry(
        Area.Data,
        "member-takes-one-value",
        Severity.Error,
        "`{0}` holds a single value, so it cannot be given a braced record or list",
        "The member is one value of a plain type. Braces would make its value a record or a list, which it is not; "
            + "remove them.");

    internal static DiagnosticDescriptor MemberCountMismatch { get; } = Entry(
        Area.Data,
        "member-count-mismatch",
        Severity.Error,
        "`{0}` holds {1} {2}, and this list gives {3}",
        "A member holds exactly as many elements as its type states.");

    internal static DiagnosticDescriptor MemberTextTooLong { get; } = Entry(
        Area.Data,
        "member-text-too-long",
        Severity.Error,
        "`{0}` has room for {1} bytes, and this text is {2} bytes",
        "Text given to a member must fit in the room the member reserves. Shorten the text or reserve more room.");

    internal static DiagnosticDescriptor MemberNotText { get; } = Entry(
        Area.Data,
        "member-not-text",
        Severity.Error,
        "`{0}` is one `{1}` and cannot hold text: reserve a text member with `.res`",
        "A member of a plain type holds one value. Room for text is reserved with `.res`, which states how much.");

    internal static DiagnosticDescriptor StrzNotText { get; } = Entry(
        Area.Data,
        "strz-not-text",
        Severity.Error,
        "`.strz` takes one text: a string, a string constant, a call that returns text, or a charmap applied to one",
        "`.strz` writes text and the zero that ends it, so it takes exactly one text.");

    internal static DiagnosticDescriptor StrzZeroInText { get; } = Entry(
        Area.Data,
        "strz-zero-in-text",
        Severity.Error,
        "{0}; `.strz` adds the terminating zero itself",
        "A zero byte is what ends the text, so a charmap that maps a character to zero would end it in the "
            + "middle.");

    internal static DiagnosticDescriptor TextNotAscii { get; } = Entry(
        Area.Data,
        "text-not-ascii",
        Severity.Error,
        "this character is not ASCII: outside a charmap, write a byte above $7f as `\\xHH`",
        "Which byte a character above $7f becomes depends on an encoding nt65 does not choose. A charmap states "
            + "what the bytes are, and `\\xHH` gives one directly.");

    internal static DiagnosticDescriptor CharmapValueNotAByte { get; } = Entry(
        Area.Data,
        "charmap-value-not-a-byte",
        Severity.Error,
        "a charmap maps a character of this text to {0}, which is not a byte: a charmap value must be 0 to 255",
        "A charmap maps each character to one byte, so every value it gives must be 0 to 255. Correct the charmap "
            + "entry.");

    internal static DiagnosticDescriptor FarAddressInWord { get; } = Entry(
        Area.Data,
        "far-address-in-word",
        Severity.Error,
        "`{0}` is a far address and `{1}` holds 16 bits: use `.faraddr`, or `.loword({2})` for the low 16 bits",
        "A far address is a bank and a sixteen-bit offset. Writing it into two bytes would drop the bank "
            + "silently, so nt65 asks which was meant.");

    internal static DiagnosticDescriptor AddressDoesNotFit { get; } = Entry(
        Area.Data,
        "address-does-not-fit",
        Severity.Error,
        "`{0}` is {1} address, and {2}: {3}",
        "The address is wider than the place it is written into, and ca65 would stop with a range error. Use the "
            + "part of the address that fits, as the message suggests.");

    internal static DiagnosticDescriptor LinkedValueMayNotFit { get; } = Entry(
        Area.Data,
        "linked-value-may-not-fit",
        Severity.Error,
        "`{0}` is worked out by the linker from an address, and {1}, which nt65 cannot show it fits: use `<({0})` "
            + "for its low byte",
        "A value that names an absolute or far address is written for ld65 to work out, and ca65 refuses it in a "
            + "one-byte slot whatever it comes to, unless a byte operator such as `<` takes one byte of the address. "
            + "Where nt65 can show the value always fits a byte, as it can for `main / 256` or "
            + "`'0' + (main / 10) .mod 10`, the output keeps only the low byte, which loses nothing. Where it cannot, "
            + "`<` says that the low byte is what is meant.");

    internal static DiagnosticDescriptor AddressNegative { get; } = Entry(
        Area.Data,
        "address-negative",
        Severity.Error,
        "an address cannot be negative, but this one is {0}",
        "`.addr` and `.faraddr` hold addresses, which are never negative.");

    internal static DiagnosticDescriptor ValueTooWide { get; } = Entry(
        Area.Data,
        "value-too-wide",
        Severity.Error,
        "{0} does not fit in {1}",
        "The value does not fit the bytes the declaration reserves for it. A wider type, or one of the byte "
            + "operators, shows which part was meant.");

    internal static DiagnosticDescriptor ResCountNotConstant { get; } = Entry(
        Area.Data,
        "res-count-not-constant",
        Severity.Error,
        "a `.res` count must be a constant",
        "How much room is reserved decides where everything after it goes, so it is decided while nt65 builds.");

    internal static DiagnosticDescriptor FillNotConstant { get; } = Entry(
        Area.Data,
        "fill-not-constant",
        Severity.Error,
        "the fill of {0} must be a constant",
        "ca65 fills the room a `.res` or an `.align` leaves with a byte it has to know when it reaches the line. A "
            + "byte of an address, such as `<main`, is known only to the linker.");

    internal static DiagnosticDescriptor ResCountOutOfRange { get; } = Entry(
        Area.Data,
        "res-count-out-of-range",
        Severity.Error,
        "a `.res` count must be between 0 and $ffff, not {0}",
        "`.res` is passed straight to ca65, which reserves at most $ffff bytes in one directive. To reserve more, "
            + "use several `.res`, or a declaration with a type, such as `.byte[n]`, which nt65 writes out as "
            + "several directives.");

    internal static DiagnosticDescriptor AlignBoundaryNotConstant { get; } = Entry(
        Area.Data,
        "align-boundary-not-constant",
        Severity.Error,
        "an `.align` boundary must be a constant",
        "Where the padding ends is worked out from the boundary, so the boundary is known while nt65 builds.");

    internal static DiagnosticDescriptor AlignBoundaryNotPowerOfTwo { get; } = Entry(
        Area.Data,
        "align-boundary-not-power-of-two",
        Severity.Error,
        "an `.align` boundary must be a power of two from 1 to $10000, not {0}",
        "ld65 aligns only on powers of two, and nt65 accepts boundaries up to $10000.");

    internal static DiagnosticDescriptor ResNotADeclaration { get; } = Entry(
        Area.Data,
        "res-not-a-declaration",
        Severity.Error,
        "a named declaration cannot use `.res`: use a type such as `.byte[n]`, which is zero-filled",
        "In nt65, `.res` is only padding inside a declaration. A named declaration states what its bytes are with a "
            + "type: `buf: .res 16` in ca65 becomes `.data buf: .byte[16]`, which reserves the same room.");

    internal static DiagnosticDescriptor AlignNotADeclaration { get; } = Entry(
        Area.Data,
        "align-not-a-declaration",
        Severity.Error,
        "`.align` cannot be a named declaration: put it between declarations",
        "An `.align` is padding that positions the next declaration. It has no content of its own to name, so it "
            + "stands on its own between declarations.");

    // Placement

    internal static DiagnosticDescriptor SegmentUndeclared { get; } = Entry(
        Area.Placement,
        "segment-undeclared",
        Severity.Error,
        "segment `{0}` is not declared",
        "Every segment is declared once, with the address size it is reached at: in a source file "
            + "(`.segment NAME: abs`), in the project file's `segments`, or in a linked ld65 config. nt65 does not "
            + "create a segment just because a line names it. Declare the segment, or check the spelling of its "
            + "name.");

    internal static DiagnosticDescriptor SegmentDeclaredTwice { get; } = Entry(
        Area.Placement,
        "segment-declared-twice",
        Severity.Error,
        "segment `{0}` is already declared",
        "A segment is declared, with its size, once for the whole program, in one file or in the project, so that "
            + "every file reaches it the same way. After that, `.segment NAME` with no size just switches to it. "
            + "Remove the size from this line, or remove one of the declarations.");

    internal static DiagnosticDescriptor SegmentNotLinked { get; } = Entry(
        Area.Placement,
        "segment-not-linked",
        Severity.Error,
        "segment `{0}` is not in any linked config, so ld65 has nowhere to put it",
        "A project with `links` in its project file takes its segments from the `SEGMENTS` block of each linked "
            + "ld65 config. A segment none of them places would stop the link with \"missing memory area "
            + "assignment\", so nt65 reports it where it is named. Add the segment to a config, or check the "
            + "spelling of its name.");

    internal static DiagnosticDescriptor LinkedSegmentsDisagree { get; } = Entry(
        Area.Placement,
        "linked-segments-disagree",
        Severity.Error,
        "segment `{0}` {1} in `{2}`",
        "nt65 analyzes each module once, whichever links it goes into, so a segment that two linked configs both "
            + "place must be the same segment to nt65 in each: the same size, space, bank and mirrors. Make the "
            + "configs agree, or give one of the segments another name.");

    internal static DiagnosticDescriptor SegmentNotDefined { get; } = Entry(
        Area.Placement,
        "segment-not-defined",
        Severity.Error,
        "`{0}` needs `define = yes` on segment `{1}` in `{2}`: ld65 defines `{3}` only for such a segment",
        "`.loadof`, `.runof` and `.spanof` stand for the symbols ld65 defines for a segment whose config entry has "
            + "`define = yes`. Without it the link fails with an unresolved import, so nt65 reports it here. Add "
            + "`define = yes` to the segment's line in the config.");

    internal static DiagnosticDescriptor CodeInADataSpace { get; } = Entry(
        Area.Placement,
        "code-in-a-data-space",
        Severity.Error,
        "segment `{0}` is in data space `{1}` and cannot hold this processor's code",
        "An address space declared as `\"data\"` in the project's `spaces` is another processor's memory, such as "
            + "a sound CPU's RAM. nt65 does not assemble that processor's instructions, so its code is given as "
            + "data, or through macros that emit the bytes. A space whose code this program's processor runs is "
            + "declared as `\"code\"`.");

    internal static DiagnosticDescriptor TransferToAnotherSpace { get; } = Entry(
        Area.Placement,
        "transfer-to-another-space",
        Severity.Error,
        "{0} is code for another processor, in {1}, so `{2}` cannot reach it",
        "Each address space is a different processor's memory. To this code, a name in another space is only a "
            + "number, such as the address the other processor starts at. A jump, branch or call to it would go to "
            + "that number in this processor's memory, which is something else. Use the value as an immediate or "
            + "in data instead, for example to send it to the other processor.");

    internal static DiagnosticDescriptor OperandInAnotherSpace { get; } = Entry(
        Area.Placement,
        "operand-in-another-space",
        Severity.Error,
        "{0} is in segment `{1}` of {2}, and this code runs in {3}: here it can only be used as an immediate "
            + "value or in data",
        "An operand that reads or writes memory reaches this processor's memory, where a name from another address "
            + "space is only a number. Take its value as an immediate (`#<name`) or put it in data. To reach the "
            + "bytes as this processor holds them, before they are sent across, use `.loadof`.");

    internal static DiagnosticDescriptor SegmentNotVisible { get; } = Entry(
        Area.Placement,
        "segment-not-visible",
        Severity.Error,
        "{0} in segment `{1}`, which is never mapped at the same time as segment `{2}`: `{3}` gives memory areas `{4}` and `{5}` the same addresses",
        "A linked configuration that runs two segments in different memory areas covering the same addresses, "
            + "such as the switchable banks of a cartridge mapper or disk overlays that share a load address, means "
            + "that only one of them is mapped at a time. Code in one can never jump to, call, branch to, read or write "
            + "anything in the other, because whatever it reaches at that address is its own area. Go through "
            + "code that both can see, such as a routine in a fixed bank that maps the other bank and calls it. "
            + "Taking the address as an immediate (`#<name`, `#>name`) or holding it in data is allowed, because "
            + "that is how such a trampoline is told where to go. nt65 decides this only from where the "
            + "configuration places segments. Areas that only partly overlap are not taken to be alternatives, and "
            + "nothing is reported where nt65 cannot tell where a segment runs.");

    internal static DiagnosticDescriptor SpaceUndeclared { get; } = Entry(
        Area.Placement,
        "space-undeclared",
        Severity.Error,
        "space `{0}` is not declared",
        "A segment's `space` names an address space declared in the project file's `spaces`, where each space is "
            + "`\"code\"` (this program's processor runs it) or `\"data\"` (another processor's memory). Declare "
            + "the space there, or check the spelling.");

    internal static DiagnosticDescriptor SpaceNotAName { get; } = Entry(
        Area.Placement,
        "space-not-a-name",
        Severity.Error,
        "`space` takes the name of an address space",
        "`space = spc` puts the segment in the address space `spc`, which the project's `spaces` declares. It is a "
            + "plain name, not a number or an expression; in the project file it is a non-empty string.");

    internal static DiagnosticDescriptor SegmentStandardSize { get; } = Entry(
        Area.Placement,
        "segment-standard-size",
        Severity.Error,
        "`{0}` is a standard segment and is always `{1}`",
        "The standard segments `CODE`, `RODATA`, `DATA`, `BSS` and `ZEROPAGE` are predeclared with the size ca65 "
            + "gives them in every object file. One may be declared once more to give it a direct page, a bank or "
            + "mirrors, but only at that size. Use a segment of your own for a different size.");

    internal static DiagnosticDescriptor SegmentAttributeTwice { get; } = Entry(
        Area.Placement,
        "segment-attribute-twice",
        Severity.Error,
        "segment `{0}` already has a `{1}`",
        "Each of a segment's attributes (`dp`, `bank`, `mirrors`, `space`) takes one value, so it is given once. "
            + "List every mirrored bank in a single `mirrors = [...]`.");

    internal static DiagnosticDescriptor SegmentDpNotZp { get; } = Entry(
        Area.Placement,
        "segment-dp-not-zp",
        Severity.Error,
        "segment `{0}` is not `zp`, and only a `zp` segment takes a `dp`",
        "`dp` gives the direct-page base (the 65816's D register) that a `zp` segment's symbols are reached "
            + "through. Symbols in any other segment are reached by absolute or long addresses, so `dp` means "
            + "nothing there. Declare the segment `zp`, or remove the `dp`.");

    internal static DiagnosticDescriptor SegmentAttributeNotConstant { get; } = Entry(
        Area.Placement,
        "segment-attribute-not-constant",
        Severity.Error,
        "`{0}` must be a constant",
        "A segment's `dp` and `bank` decide how every reference into the segment is checked and encoded, so they "
            + "must be numbers nt65 can work out while it builds, not addresses the linker decides.");

    internal static DiagnosticDescriptor SegmentAttributeOutOfRange { get; } = Entry(
        Area.Placement,
        "segment-attribute-out-of-range",
        Severity.Error,
        "{0}",
        "The direct page is a 16-bit address, from $0000 to $ffff, and a bank is one byte, from $00 to $ff.");

    internal static DiagnosticDescriptor SegmentMirrorInvalid { get; } = Entry(
        Area.Placement,
        "segment-mirror-invalid",
        Severity.Error,
        "each mirror must be a constant bank, or a range of banks given lower first, such as `$00..$3f`",
        "`mirrors = [...]` lists the other banks in which the segment's home bank also appears. Each item is a "
            + "constant bank from $00 to $ff, or a range of them given with the lower bank first, such as "
            + "`$00..$3f`.");

    internal static DiagnosticDescriptor SegmentMirrorsNeedABank { get; } = Entry(
        Area.Placement,
        "segment-mirrors-need-a-bank",
        Severity.Error,
        "segment `{0}` has `mirrors` but no `bank` for them to mirror",
        "`mirrors` lists other banks where the segment's home bank also appears, so the segment needs a home bank, "
            + "given with `bank = ...`.");

    internal static DiagnosticDescriptor SegmentBlockRedundant { get; } = Entry(
        Area.Placement,
        "segment-block-redundant",
        Severity.Error,
        "this block is already in segment `{0}`, so it moves nothing, and execution falls into it",
        "A nested segment block moves its contents out of line into another segment, so that execution does not "
            + "fall through into them. A block that names the segment it is already in moves nothing, and its "
            + "contents stay where the code above runs into them. Name a different segment, or remove the block.");

    internal static DiagnosticDescriptor IndirectJumpWraps { get; } = Entry(
        Area.Instructions,
        "indirect-jump-wraps",
        Severity.Error,
        "the vector is at {0}, the last byte of a page, so the NMOS 6502 reads its high byte from {1} rather than {2}",
        "On the NMOS 6502, `jmp (vector)` reads the vector's high byte from the address after its low byte, but "
            + "without carrying into the next page, so a vector at $xxff has its high byte read from $xx00. The "
            + "CMOS 6502s and the 65816 do not do this. Move the vector off the last byte of its page, for example "
            + "with `.align 2` before it. Where only the linker decides the vector's address, ca65 has ld65 check "
            + "it, and ld65 reports it at the jump in the `.s` file.");

    internal static DiagnosticDescriptor OutsideEverySegment { get; } = Entry(
        Area.Placement,
        "outside-every-segment",
        Severity.Error,
        "{0} is not in any segment: put a `.segment NAME` line above it, or place it in a `.segment NAME` block",
        "Every byte goes in a segment, and the linker decides where each segment goes. Unlike ca65, which starts "
            + "in `CODE`, nt65 has no default segment, and nothing is placed just by coming first. Put a "
            + "`.segment` line, such as `.segment CODE`, above it.");

    internal static DiagnosticDescriptor NeverWritten { get; } = Entry(
        Area.Placement,
        "never-written",
        Severity.Error,
        "{0} in segment `{1}`, which {2}, so ld65 never writes them",
        "ld65 writes a segment's bytes only when the segment is `ro` or `rw` and the memory area it loads into "
            + "has a file. A `bss` or `zp` segment, or one that loads into an area with `file = \"\"`, only reserves "
            + "room, so values given there never reach memory and the program starts with whatever the memory "
            + "held. Leave the values out and set them in code, or move them to a segment that loads into an area "
            + "ld65 writes, and copy them where they run.");

    internal static DiagnosticDescriptor InstructionInData { get; } = Entry(
        Area.Placement,
        "instruction-in-data",
        Severity.Error,
        "{0} in a `.proc`, not in a `.data` declaration",
        "A `.data` declaration holds only data. Instructions go in a routine (`.proc`), where the flow analysis "
            + "can follow them. Hand-assembled opcodes belong in data as `.byte` values.");

    internal static DiagnosticDescriptor InstructionOutsideARoutine { get; } = Entry(
        Area.Placement,
        "instruction-outside-a-routine",
        Severity.Error,
        "{0} in a `.proc`: nt65 follows and checks code only inside routines",
        "Flow analysis works routine by routine, so code outside a `.proc` could never be followed or checked. Put "
            + "the code in a `.proc`, or, if the bytes are data, declare them with `.data`.");

    internal static DiagnosticDescriptor PaddingOutsideARoutine { get; } = Entry(
        Area.Placement,
        "padding-outside-a-routine",
        Severity.Error,
        "`{0}` outside a `.proc` must be part of a `.data` declaration{1}",
        "Outside a routine, every byte belongs to a named `.data` declaration, such as `.data table: .byte 1, 2, "
            + "3`, which gives the bytes a name and a size. Only an unnamed `.res` or `.align` may stand on its "
            + "own, as padding between declarations.");

    internal static DiagnosticDescriptor FarNeeds65816 { get; } = Entry(
        Area.Placement,
        "far-needs-65816",
        Severity.Error,
        "{0} is `far`, which needs the 65816",
        "A far address is a 24-bit bank and offset, which only the 65816 has. ca65 rejects `far` on every other "
            + "processor, so nt65 reports it where it is declared rather than emitting output ca65 would reject. "
            + "Declare the segment or import `abs` or `zp`, or build for the 65816.");

    // Instructions

    internal static DiagnosticDescriptor EncodedAboutNothing { get; } = Entry(
        Area.Instructions,
        "encoded-about-nothing",
        Severity.Error,
        "`.encoded` applies to the instruction below it, and there is none",
        "`.encoded $34` gives the opcode byte the instruction below it is written as. Only blank lines, bare labels "
            + "and `.allow` lines may stand between them.");

    internal static DiagnosticDescriptor EncodedNotAByte { get; } = Entry(
        Area.Instructions,
        "encoded-not-a-byte",
        Severity.Error,
        "`.encoded` takes the opcode byte the instruction below it is written as, a constant from 0 to 255",
        "The byte is written in place of the opcode ca65 would choose, so it has to be known when nt65 lays the "
            + "code out.");

    internal static DiagnosticDescriptor EncodedMismatch { get; } = Entry(
        Area.Instructions,
        "encoded-mismatch",
        Severity.Error,
        "`.encoded {0}` cannot write `{1}`: {2}",
        "Some processors run more than one byte as the same instruction, such as the NMOS 6502's undocumented "
            + "`nop` encodings, and ca65 writes only one of them. `.encoded` chooses another, and the byte has to run "
            + "as the instruction below it, in a form its operand allows. The form the byte runs as decides "
            + "between the direct-page and the absolute form. A branch and a block move have one encoding each.");

    internal static DiagnosticDescriptor InstructionNotOnCpu { get; } = Entry(
        Area.Instructions,
        "instruction-not-on-cpu",
        Severity.Error,
        "`{0}` is not available on the {1}{2}",
        "The program is built for one processor, and this instruction is not in its set. Where another processor "
            + "nt65 knows has it, the message names that processor.");

    internal static DiagnosticDescriptor OperandMissing { get; } = Entry(
        Area.Instructions,
        "operand-missing",
        Severity.Error,
        "`{0}` needs an operand",
        "This instruction has no form without an operand, so it needs one.");

    internal static DiagnosticDescriptor OperandNotTaken { get; } = Entry(
        Area.Instructions,
        "operand-not-taken",
        Severity.Error,
        "`{0}` does not take this operand on the {1}",
        "The instruction exists on this processor, but not with this addressing mode; for example, `lda (ptr)` "
            + "without `,y` needs a 65C02. Check the operand, or the processor the program is built for.");

    internal static DiagnosticDescriptor OperandIsText { get; } = Entry(
        Area.Instructions,
        "operand-is-text",
        Severity.Error,
        "`{0}` is a string, and an instruction's operand must be a number or an address",
        "An instruction's operand is a number or an address. A string is a sequence of bytes, which a data "
            + "directive emits in a `.data` declaration.");

    internal static DiagnosticDescriptor OperandHasNoNextByte { get; } = Entry(
        Area.Instructions,
        "operand-has-no-next-byte",
        Severity.Error,
        "{0} in the macro needs a plain address, and this call gives `{1}` {2} operand",
        "In a macro body, `p + n` or `.byteof` on an operand parameter addresses a byte after the one the argument "
            + "names, which works only when the argument is a plain address, indexed or not. An immediate, the "
            + "accumulator, an indirect operand or a stack-relative one has no later byte to address. Pass a plain "
            + "address at this call, or change the macro body.");

    internal static DiagnosticDescriptor BranchOperandNotTaken { get; } = Entry(
        Area.Instructions,
        "branch-operand-not-taken",
        Severity.Error,
        "`{0}` takes only a target address as its operand",
        "A long branch such as `jeq` or `jne` takes one target address, as a short branch does, and has no other "
            + "addressing mode.");

    internal static DiagnosticDescriptor TransferPrefix { get; } = Entry(
        Area.Instructions,
        "transfer-prefix",
        Severity.Error,
        "`{0}` does not take an address-size prefix: a jump, branch or call is sized by its target",
        "An address-size prefix (`z:`, `a:`, `f:`) chooses how wide an operand is read. A jump, branch or call is "
            + "sized by its target and its mnemonic instead: `jmp` or `jml`, `jsr` or `jsl`. Remove the prefix, "
            + "and use the mnemonic for the distance you mean.");

    internal static DiagnosticDescriptor TargetTooFar { get; } = Entry(
        Area.Instructions,
        "target-too-far",
        Severity.Error,
        "`{0}` cannot reach a far target outside the current bank",
        "`jmp`, `jsr`, the branches and `per` reach an address within the current bank, and this target is in "
            + "another bank. On the 65816 use the long form: `jml` instead of `jmp`, or `jsl` instead of `jsr` for "
            + "a routine that returns with `rtl`.");

    internal static DiagnosticDescriptor TargetTooNear { get; } = Entry(
        Area.Instructions,
        "target-too-near",
        Severity.Error,
        "`{0}` is for a far target, and this one is {1}: use `{2}`",
        "`jml` and `jsl` take a three-byte address and are for targets in another bank. This target is in reach of "
            + "the shorter form, which is smaller and faster. A constant address is taken as given, so `jml "
            + "$008000` is allowed.");

    internal static DiagnosticDescriptor BranchOutOfReach { get; } = Entry(
        Area.Instructions,
        "branch-out-of-reach",
        Severity.Error,
        "`{0}` would branch {1} bytes, beyond a branch's reach of -128 to 127{2}",
        "A branch stores its target as one signed byte counted from the instruction after it, so it reaches 128 "
            + "bytes back or 127 forward. Reverse the condition and branch over a `jmp`, or use the long branch "
            + "(`jeq`, `jne` and so on), which nt65 writes as the short branch where it reaches and as a branch "
            + "over a `jmp` where it does not.");

    internal static DiagnosticDescriptor AddressingModeMissing { get; } = Entry(
        Area.Instructions,
        "addressing-mode-missing",
        Severity.Error,
        "`{0}` has no {1} form of this operand on the {2}: remove the address-size prefix",
        "An address-size prefix (`z:`, `a:`, `f:`) asks for one particular form of the instruction, and on this "
            + "processor the instruction has no form of that width for this operand. nt65 does not silently drop a "
            + "prefix it cannot honour. Remove the prefix, or give the operand a shape that has that form.");

    internal static DiagnosticDescriptor AddressingModeTooNarrow { get; } = Entry(
        Area.Instructions,
        "addressing-mode-too-narrow",
        Severity.Error,
        "`{0}` has only a {1} form of this operand, and `{2}` is {3}",
        "This operand shape exists only in a narrower form: `(ptr),y`, for example, takes a zero-page pointer. The "
            + "address named is wider, so it would be cut down to its low byte. Put what it names in a `zp` "
            + "segment, or use a different addressing mode.");

    internal static DiagnosticDescriptor AddressSizeUnreachable { get; } = Entry(
        Area.Instructions,
        "address-size-unreachable",
        Severity.Error,
        "`{0}` cannot reach a {1} address on the {2}",
        "No form of this instruction on this processor takes an address as wide as this operand, such as a far "
            + "address on a processor without long addressing. Move what it names to a segment the instruction can "
            + "reach, or reach it another way.");

    internal static DiagnosticDescriptor ImmediateTooWide { get; } = Entry(
        Area.Instructions,
        "immediate-too-wide",
        Severity.Error,
        "{0} does not fit in {1}",
        "An immediate is one byte, or, on the 65816, two bytes when the register it goes to is 16-bit at this "
            + "point, as the analysis tracks it from `rep`, `sep` and the routine's signature. Use a value that "
            + "fits, take one byte of it with `<` or `>`, or widen the register.");

    internal static DiagnosticDescriptor ImmediateMissing { get; } = Entry(
        Area.Instructions,
        "immediate-missing",
        Severity.Warning,
        "`{0} {1}` reads memory at address {1}: write `#{1}` for the number, or `{2}` for the address",
        "An operand without `#` is an address, and addresses are written in hexadecimal. A plain decimal address "
            + "on an instruction that also takes an immediate, such as `lda 10` or `cmp 32`, is nearly always a "
            + "number whose `#` was left out. Write `#10` for the number, or `$0a` where the address was meant. An "
            + "indexed operand such as `lda 2,x` is not reported, because a small decimal base there is common.");

    internal static DiagnosticDescriptor DirectPageNeeds65816 { get; } = Entry(
        Area.Instructions,
        "direct-page-needs-65816",
        Severity.Error,
        "`d:` needs the 65816, and this program is built for the {0}: use `z:` for a zero-page address",
        "`d:` marks an address reached through the 65816's direct page, which can be moved with the D register. "
            + "Earlier processors have a fixed zero page, which `z:` asks for.");

    internal static DiagnosticDescriptor DirectPagePrefixOnSymbol { get; } = Entry(
        Area.Instructions,
        "direct-page-prefix-on-symbol",
        Severity.Error,
        "`d:` takes only a constant address: to reach a symbol through the direct page, declare it in a `zp` segment",
        "Whether a symbol is reached through the direct page is decided by its segment: symbols in a `zp` segment "
            + "are, with the segment's `dp` as the base. That way, moving a declaration between segments does not "
            + "mean editing every line that uses it. `d:` is for a literal address, such as `d:$05`.");

    internal static DiagnosticDescriptor DirectPageFormMissing { get; } = Entry(
        Area.Instructions,
        "direct-page-form-missing",
        Severity.Error,
        "`{0}` has no direct-page form of this operand, which `d:` asks for",
        "`d:` asks for the one-byte direct-page form of the instruction, and the instruction has no such form with "
            + "this operand; `lda` has no `dp,y` form, for example. Remove the `d:`, or use an addressing mode "
            + "that has a direct-page form.");

    internal static DiagnosticDescriptor DirectPageOnly { get; } = Entry(
        Area.Instructions,
        "direct-page-only",
        Severity.Error,
        "`{0}` is in direct-page segment `{1}`, so it must be a direct-page operand; as {2} operand it would address the data bank",
        "A symbol in a `zp` segment with a nonzero `dp` is an offset from the direct page. As a direct-page "
            + "operand it reaches the direct page plus that offset. As an absolute or long operand, whether forced "
            + "with a prefix or because the instruction has no direct-page form with this index (`lda dp,y`, for "
            + "example), it would reach the same offset in the data bank, a different address. Use a direct-page "
            + "addressing mode.");

    internal static DiagnosticDescriptor AssertionFailed { get; } = Entry(
        Area.Instructions,
        "assertion-failed",
        Severity.Error,
        "{0}",
        "The `.assert` condition is false. nt65 checks an assertion as soon as it can evaluate the condition; one "
            + "that depends on final addresses is passed on for the linker to check.");

    internal static DiagnosticDescriptor ConfigRefused { get; } = Entry(
        Area.Instructions,
        "config-refused",
        Severity.Error,
        "{0}",
        "The build reached an `.error` directive, usually inside an `.if` that rejects a configuration the file "
            + "does not support. The message is the file's own text.");

    internal static DiagnosticDescriptor ConfigWarned { get; } = Entry(
        Area.Instructions,
        "config-warned",
        Severity.Warning,
        "{0}",
        "The build reached a `.warning` directive. The message is the file's own text, and the build continues.");

    // Control flow

    internal static DiagnosticDescriptor AnnotationAboutNothing { get; } = Entry(
        Area.ControlFlow,
        "annotation-about-nothing",
        Severity.Error,
        "`{0}` applies to the statement above it, and there is none",
        "An annotation such as `.next` or `.patch` goes directly after the statement it describes, so it "
            + "reads next to it. Here there is no statement above it to apply to. Move it to just below the "
            + "statement it is about.");

    internal static DiagnosticDescriptor CodeUnreachable { get; } = Entry(
        Area.ControlFlow,
        "code-unreachable",
        Severity.Warning,
        "this code is never reached: {0}",
        "No path from the routine's entry reaches the code. The statement above it returns, jumps, calls a "
            + "routine that never returns, or is a branch the flags show is always taken, and nothing branches or "
            + "jumps to it, so it is assembled but never runs. In a macro body, it is reported only where no call "
            + "of the macro reaches it. "
            + "A nested segment block's bytes are placed in another segment, away from the code around them, so "
            + "execution never falls into them from above either. Code is reached only through a label: one "
            + "something branches, jumps or calls to, one a `.next` names, or one a `.state` declares as an entry "
            + "point. Where the code is reached in a way nt65 cannot follow, such as a branch into the middle of an "
            + "instruction, `.allow \"code-unreachable\"` says so.");

    internal static DiagnosticDescriptor LabelUnreachable { get; } = Entry(
        Area.ControlFlow,
        "label-unreachable",
        Severity.Warning,
        "`{0}` is never reached: no code falls into it and nothing refers to it",
        "The code above the label does not fall through into it, and nothing branches, jumps or calls to it or "
            + "takes its address. If it is reached in a way nt65 cannot see, add a `.state` after the label to "
            + "declare it an entry point; otherwise the code is dead and can be removed.");

    internal static DiagnosticDescriptor RunsIntoData { get; } = Entry(
        Area.ControlFlow,
        "runs-into-data",
        Severity.Error,
        "the instruction above falls into this {0}: {1}",
        "Execution falls from the instruction above into these bytes, so the processor would run them as code, as "
            + "in the `.byte $2c` skip trick or an opcode given as bytes. nt65 cannot follow flow through "
            + "data, so add a `.next` after the data naming where flow really goes. If the instruction above is "
            + "a conditional branch that is always taken, put a `.next` naming its target under the branch instead. "
            + "Padding from a `.res` or `.align` is data too, so falling into it needs the `.next` even where its "
            + "fill byte runs on. Where the fill does not run on, the `.next` is refused as `next-after-padding`, "
            + "so give the padding a fill byte such as `$ea`, which is `nop`, or `jmp` over it.");

    internal static DiagnosticDescriptor NextAfterPadding { get; } = Entry(
        Area.ControlFlow,
        "next-after-padding",
        Severity.Error,
        "`.next` cannot follow `{0}`: {1}; give it a fill byte that runs on, such as `$ea` (`nop`), or `jmp` over it",
        "A `.next` after data says that execution runs through the data's bytes and on to the labels it names. "
            + "Padding from a `.res` or `.align` holds its own fill byte when it names a constant one. Otherwise "
            + "ld65 fills it with the segment's `fillval`, else the `fillval` of the memory area the segment loads "
            + "into, else zero, so nt65 knows the byte only from a config under `links`. Execution runs through "
            + "the padding only when that byte is a one-byte instruction that does nothing on the program's CPU, "
            + "as `$ea`, which is `nop`, does on every CPU. Zero is `brk`, and other bytes change a register, transfer "
            + "control, or take the bytes after them as an operand, so the claimed edge would be false. Give the "
            + "padding a fill byte such as `$ea`, set `fillval = $ea` in the config, or put a `jmp` before the "
            + "padding to step over it.");

    internal static DiagnosticDescriptor RoutineRunsOffTheEnd { get; } = Entry(
        Area.ControlFlow,
        "routine-runs-off-the-end",
        Severity.Error,
        "`{0}` runs off {1} into whatever {2}: {3}",
        "The routine's last instruction does not return, jump or branch away, so execution continues into "
            + "whatever the linker puts after it. Usually an `rts`, `rtl` or `jmp` is missing. Where running on "
            + "was meant, end the body with `.fallthrough NAME`, naming the routine it runs into; nt65 checks that "
            + "NAME starts where this routine ends, and checks it as a tail call. Where the last instruction is a "
            + "conditional branch that is always taken, a `.next` naming the branch's own target says so. "
            + "Elsewhere, `.next ?` says control goes somewhere nt65 is not told about, and the analysis then "
            + "assumes it may change anything.");

    internal static DiagnosticDescriptor LabelPositionInvalid { get; } = Entry(
        Area.ControlFlow,
        "label-position-invalid",
        Severity.Error,
        "`.label {0}` has to name a byte inside an instruction of this routine, as `@op + 1` does: {1}",
        "`.label name = @op + 1` names the position one byte into the instruction at `@op`, where a branch or "
            + "an entry from outside runs the instruction's bytes as other instructions. The position is a label "
            + "at an instruction plus a constant number of bytes, more than 0 and less than the instruction's "
            + "length.");

    internal static DiagnosticDescriptor HiddenPathUnfollowed { get; } = Entry(
        Area.ControlFlow,
        "hidden-path-unfollowed",
        Severity.Error,
        "nt65 cannot follow the bytes from `{0}`: {1}",
        "From a position inside an instruction, nt65 decodes the bytes as the processor runs them, until they "
            + "reach the start of an instruction as written, and the analyses follow those instructions. Every "
            + "byte has to be known before linking, each instruction has to be one the processor has, and none may "
            + "change where control goes or move the stack, or nt65 could not tell where they lead.");

    internal static DiagnosticDescriptor PatchVariantRejected { get; } = Entry(
        Area.ControlFlow,
        "patch-variant-rejected",
        Severity.Error,
        "`.patch` cannot list `{0}` for `{1}`: {2}",
        "`.patch @op as dex` says the store above it can turn the instruction at `@op` into `dex`. A variant "
            + "replaces the opcode, so it keeps the instruction's addressing mode and operand, and the CPU must have it "
            + "in that form. A store known to write only the operand, as `sta @op+1` is, has no variant to list. The "
            + "analyses take the union of what the written instruction and each variant do, so a variant may not move "
            + "the stack, change the processor's widths or run a handler, and it must run on where the written "
            + "instruction runs on and branch where it branches. On the 65816 a variant's immediate must be as wide "
            + "as the written one's, sized by the same register.");

    internal static DiagnosticDescriptor PatchVariantsRequired { get; } = Entry(
        Area.ControlFlow,
        "patch-variants-required",
        Severity.Error,
        "`{0}` may write the opcode of `{1}`, so the `.patch` must list what the instruction can become with `as`",
        "A `.patch` without `as` is for a store known to write only the operand, as `sta @op+1` is. A store that "
            + "starts at the opcode, as `sta @op` does, or whose offset is not known, as with `sta @op,x` or a store "
            + "through another name, may write the opcode. The variants `as` lists bound what the instruction can "
            + "become. That is what lets the width, stack and flow analyses go on trusting the shape of the written "
            + "instruction. Without them nothing could be assumed after the instruction.");

    internal static DiagnosticDescriptor PatchMissesStore { get; } = Entry(
        Area.ControlFlow,
        "patch-misses-store",
        Severity.Error,
        "`{0}` writes {1} `{2}`, which is {3}, {4}",
        "`.patch @op` says the store above it writes into the instruction at `@op`, so the bytes the store writes "
            + "must lie inside that instruction. Where nt65 knows both where the store writes and where the "
            + "instruction stands, as for `sta @op+3` and a two-byte `@op`, it checks this. A store that writes "
            + "past the instruction rewrites whatever comes next, which the analyses would otherwise go on "
            + "trusting as written. Label that instruction and name it with a `.patch` of its own, in place of the "
            + "first or as well as it where the store writes both. A store that reaches into data needs no `.patch` for those bytes, because a `.patch` names an "
            + "instruction.");

    internal static DiagnosticDescriptor NextTargetNotCode { get; } = Entry(
        Area.ControlFlow,
        "next-target-not-code",
        Severity.Error,
        "`{0}` is {1}, and `{2}` must name a code label, a routine, or a table of them",
        "`.next` and `.patch` name places in code: a label, a routine, or a list or table of addresses holding "
            + "them. What is named here is not somewhere flow can go.");

    internal static DiagnosticDescriptor NextTableHasNoLabels { get; } = Entry(
        Area.ControlFlow,
        "next-table-has-no-labels",
        Severity.Error,
        "table `{0}` holds no code labels for `.next` to follow",
        "A `.next` may name a table of addresses, and flow then goes to each code label the table holds. This "
            + "table holds none, so it gives no information about where flow goes. Name the labels directly, or fill the "
            + "table with code labels.");

    internal static DiagnosticDescriptor NextTargetNotATable { get; } = Entry(
        Area.ControlFlow,
        "next-target-not-a-table",
        Severity.Error,
        "`{0}` is not a table of addresses: `.next` can name data only when it holds `.addr` or `.faraddr` values",
        "A `.next` that names data follows the code labels the data holds, so the data must be a table of "
            + "addresses: data declared with `.addr` or `.faraddr`, records whose type has members declared so, "
            + "or mixed data made of either. In records, the other members are passed over.");

    internal static DiagnosticDescriptor NextSuccessorsKnown { get; } = Entry(
        Area.ControlFlow,
        "next-successors-known",
        Severity.Error,
        "`.next` is not needed: {0} already {1}{2}",
        "`.next` states where flow goes after a statement it cannot follow by itself: an indirect jump or "
            + "call, an `rts` or `rtl` used as a jump, a jump to a computed address, or data that execution falls "
            + "into. After a conditional branch it may name the branch's own target, to state that the branch is always "
            + "taken. After any other statement nt65 already knows where flow goes, and a `.next` could only "
            + "contradict it. To state that a routine runs into the one after it, use `.fallthrough`. `.next ?` "
            + "may stand after any statement, to say control goes somewhere nt65 is not told about.");

    internal static DiagnosticDescriptor NextNotTheBranchTarget { get; } = Entry(
        Area.ControlFlow,
        "next-not-the-branch-target",
        Severity.Error,
        "`.next` after {0} can name only the branch's own target, `{1}`, to state that the branch is always taken",
        "Under a conditional branch, a `.next` states that the branch is always taken, because the flags are known there, "
            + "so flow never continues past it. It must then name exactly the branch's own target; naming anything "
            + "else would claim the branch goes somewhere its operand does not. Where nt65 can prove from the flags "
            + "that the branch is always taken, the `.next` is not needed, and the editor offers to remove it.");

    internal static DiagnosticDescriptor NextNeverTaken { get; } = Entry(
        Area.ControlFlow,
        "next-never-taken",
        Severity.Error,
        "`.next {0}` says `{1}` is always taken, but {2}, so it is never taken",
        "nt65 follows the N, Z, C and V flags through each routine, from what the instructions themselves set. "
            + "Where the flag a branch tests has the same value on every path to it, the branch goes one way only. "
            + "A `.next` naming the branch's target says the branch is always taken, and here the flags show it "
            + "never is, so the code after the branch, which does run, would go unchecked. Remove the `.next`, or "
            + "correct the code that sets the flag.");

    internal static DiagnosticDescriptor NextUnknownAfterBranch { get; } = Entry(
        Area.ControlFlow,
        "next-unknown-after-branch",
        Severity.Error,
        "`{0}` cannot follow {1}, because it would also give up the path where the branch is not taken: {2}",
        "A `.next` under a conditional branch replaces both of the branch's ways on, so `.next ?` there would "
            + "say that neither is known, and the code after the branch would go unchecked. A branch always goes "
            + "to its operand or to the next statement, so neither is unknown, and neither is a return to the "
            + "caller, so `.next .return` cannot stand there either. Where the branch is always taken, say so "
            + "with a `.next` naming its target. Where the operand is not a label, such as `NULL-1`, write the "
            + "label at that address instead.");

    internal static DiagnosticDescriptor ReturnAfterCall { get; } = Entry(
        Area.ControlFlow,
        "return-after-call",
        Severity.Error,
        "`.next .return` cannot follow {0}, because a call comes back to the statement after it",
        "`.next .return` says that a jump goes back to the routine's caller, as the routine's own return "
            + "would, which is how a routine that pulls its return address and jumps back through it returns. A "
            + "call does not leave the routine: the routine it calls returns to the statement after it. Name the "
            + "routines an indirect call reaches with `.next` instead.");

    internal static DiagnosticDescriptor ReturnCountNotConstant { get; } = Entry(
        Area.ControlFlow,
        "return-count-not-constant",
        Severity.Error,
        "the count of `.next .return` must be a constant number of bytes",
        "`.next .return n` promises how many bytes the caller's stack holds after the return beyond what it "
            + "held before the call. nt65 has to know the number to use it at every call, so it must be a "
            + "constant. Leave the count out to have nt65 count the bytes, or write `?` where it cannot.");

    internal static DiagnosticDescriptor ReturnCountMismatch { get; } = Entry(
        Area.ControlFlow,
        "return-count-mismatch",
        Severity.Error,
        "`.next .return {0}` promises the caller's stack holds {0} more bytes than before the call, but nt65 counts {1} here",
        "nt65 counts what the routine has pushed and pulled since it was called, starting from the return "
            + "address, and the count it finds where the routine returns must be the one written. Callers use the "
            + "written count, so a wrong one would have them pair a pull with the wrong push. The fix writes the "
            + "count nt65 found; leaving the count out has nt65 use that count without writing it.");

    internal static DiagnosticDescriptor FallthroughMisplaced { get; } = Entry(
        Area.ControlFlow,
        "fallthrough-misplaced",
        Severity.Error,
        "`.fallthrough` must be the last line of a `.proc` body, once `.if` conditions are resolved",
        "`.fallthrough NAME` states that every path reaching the end of the routine runs on into routine NAME, so it "
            + "is about the end of the body, not the statement above it. It stands last in a `.proc` body, or last "
            + "in a branch of an `.if` chain that is itself last in the body, to any depth; conditions are resolved "
            + "before analysis, so in each build at most one remains, and it is last. It may not appear in a macro "
            + "body, a block argument or a repetition, and nothing may follow it or the chain it ends.");

    internal static DiagnosticDescriptor FallthroughNotARoutine { get; } = Entry(
        Area.ControlFlow,
        "fallthrough-not-a-routine",
        Severity.Error,
        "`{0}` is {1}, not a routine: `.fallthrough` names the routine execution runs into",
        "`.fallthrough` states that this routine runs on into the first byte of another routine, so it must name a `.proc`.");

    internal static DiagnosticDescriptor FallthroughNotAdjacent { get; } = Entry(
        Area.ControlFlow,
        "fallthrough-not-adjacent",
        Severity.Error,
        "`{0}` does not start where this routine ends: `.fallthrough` names the routine directly after it",
        "A routine that reaches its end runs on into whatever comes next in its segment: the next thing the file "
            + "puts in that segment, even when regions of other segments come between in the text, as ca65 lays "
            + "the bytes out. `.fallthrough` must name that routine; naming any other would claim something the "
            + "bytes do not do. Move the routines so the named one comes directly after, or end this one with a "
            + "`jmp`.");

    internal static DiagnosticDescriptor FallthroughOtherSegment { get; } = Entry(
        Area.ControlFlow,
        "fallthrough-other-segment",
        Severity.Error,
        "`{0}` is in segment `{2}`, and this routine ends in `{1}`: a routine can only run into what comes "
            + "next in its own segment",
        "A segment's bytes are laid out in the order they appear, whatever other segments appear in "
            + "between, so the end of a routine is followed by the next thing in its own segment. A routine in "
            + "another segment cannot be what it runs into. Across a `.place`, what comes next is the placed "
            + "module's first routine in this segment, or what the placing file puts next in it.");

    internal static DiagnosticDescriptor FallthroughNotPlaced { get; } = Entry(
        Area.ControlFlow,
        "fallthrough-not-placed",
        Severity.Error,
        "`{0}` is in module `{1}`, whose order only the linker decides: use `.place` to lay one module out inside the other",
        "Across translation units the order of the bytes is decided at link time, which nt65 does not see, so it "
            + "cannot check that one routine runs into another. Where one module places the other with `.place` "
            + "(the other declared `placed`), both are laid out in one output file, and running into the other "
            + "module's routine is checked as it is within a file.");

    internal static DiagnosticDescriptor IndirectCallUnchecked { get; } = Entry(
        Area.ControlFlow,
        "indirect-call-unchecked",
        Severity.Error,
        "{0} is an indirect call, which nt65 cannot follow: add a `.next` naming the routines it may call",
        "The call goes to an address read at run time, so the analysis cannot tell which routine it reaches or "
            + "check the call. Add a `.next` after it naming the routines it may call, or the table that holds "
            + "them; each is then checked as a call.");

    internal static DiagnosticDescriptor IndirectJumpUnchecked { get; } = Entry(
        Area.ControlFlow,
        "indirect-jump-unchecked",
        Severity.Error,
        "{0} is an indirect jump, which nt65 cannot follow: add a `.next` naming the labels it may reach, or "
            + "`.next ?` where they cannot be named",
        "The jump goes to an address read at run time. A `.next` after it names the labels it may reach, or the "
            + "jump table that holds them, and the analysis continues at each. Where it goes back to the routine's "
            + "caller through a return address the routine pulled, `.next .return` says so. `.next ?` states that the jump goes "
            + "somewhere nt65 is not told about. The path ends there, and the analysis assumes the code it reaches "
            + "may change anything, so no register is kept across it and its cost is unknown.");

    internal static DiagnosticDescriptor ComputedJumpUnchecked { get; } = Entry(
        Area.ControlFlow,
        "computed-jump-unchecked",
        Severity.Error,
        "{0} jumps to a computed address, which nt65 cannot follow: add a `.next` naming the labels it may reach, "
            + "or `.next ?` where they cannot be named",
        "The target is an expression built on a label, a routine or data, such as `table + 3`, rather than a bare "
            + "name, so the analysis has no label at which to continue. A `.next` after the jump names the labels "
            + "it may reach. Where the target is not the start of an instruction, such as a jump into the middle "
            + "of one, use `.next ?`. The analysis then assumes the code it reaches may change anything. A jump "
            + "to a constant address, such as `jmp $FFD2`, is not computed and needs neither. It is a tail call "
            + "to a routine nothing is known about, as `jsr $FFD2` is a call to one, and an extern proc, "
            + "`.proc NAME = $FFD2`, can say what that routine expects and returns with.");

    internal static DiagnosticDescriptor ComputedBranchUnchecked { get; } = Entry(
        Area.ControlFlow,
        "computed-branch-unchecked",
        Severity.Error,
        "{0} branches to a computed address, which nt65 cannot follow: write the label it goes to as its operand",
        "The target is an expression rather than a label, so the analysis has no label at which to continue. A "
            + "branch can only reach a place within its range, which is code the program holds, so the label at "
            + "that address can be named. Where nothing there has a label, add one.");

    internal static DiagnosticDescriptor PushedReturnUnchecked { get; } = Entry(
        Area.ControlFlow,
        "pushed-return-unchecked",
        Severity.Error,
        "`{0}` returns to an address pushed in this block, so it is really a jump: add a `.next` naming where it "
            + "goes",
        "The block pushes an address and then executes a return, which jumps to that address (the \"RTS "
            + "trick\"), so nt65 cannot tell where it goes. A `.next` after it names the labels it may reach.");

    internal static DiagnosticDescriptor ReturnPastPushes { get; } = Entry(
        Area.ControlFlow,
        "return-past-pushes",
        Severity.Error,
        "`{0}` returns through {1} this routine pushed rather than through its return address: pull them first, "
            + "or add a `.next` naming where it goes",
        "nt65 counts what a routine pushes and pulls from where it was called. Where nothing has pulled the return "
            + "address, it is still beneath what the routine pushed, so a return with pushes left on the stack pulls "
            + "them as the address to go to. A routine that pushes an address to jump through its return names where "
            + "it goes with a `.next`.");

    internal static DiagnosticDescriptor JumpTargetNotALabel { get; } = Entry(
        Area.ControlFlow,
        "jump-target-not-a-label",
        Severity.Error,
        "{0} goes to `{1}`, which is {2}, not a label, so nt65 cannot follow it: {3}",
        "A jump or branch normally names a label in code. This one names something that is not an address and "
            + "has no constant value, such as a scope, so the analysis cannot tell where flow goes. A `.next` after "
            + "a jump names the labels it reaches, or `.next ?` says it goes somewhere nt65 is not told about. A "
            + "jump to a constant is not reported: it is a tail call to a routine nothing is known about. A branch "
            + "can only reach code within its range, so write the label it goes to as its operand.");

    internal static DiagnosticDescriptor JumpIntoData { get; } = Entry(
        Area.ControlFlow,
        "jump-into-data",
        Severity.Error,
        "`{0}` labels data, and this jumps to it: add a `.state` after the label and a `.next` after the data "
            + "stating where flow goes",
        "The label marks data, so the processor would run those bytes as code. A `.state` after the label declares "
            + "it an entry point and what the processor state is there, and a `.next` after the data states where "
            + "flow goes from there.");

    internal static DiagnosticDescriptor EntryNotDeclared { get; } = Entry(
        Area.ControlFlow,
        "entry-not-declared",
        Severity.Error,
        "`{0}` is inside routine `{1}`: mark it an entry point with a `.state` after the label",
        "A jump into the middle of another routine arrives with a processor state that routine's own paths do not "
            + "give, and the widths, mode and registers there cannot be inferred from the jump. A `.state` directly "
            + "after the label declares it an entry point and what the processor state is there, and both the jump "
            + "and the routine are then checked against it. Only 65816 code needs this, because only there does the "
            + "code after the label depend on the processor state.");

    internal static DiagnosticDescriptor ExportedEntryNotDeclared { get; } = Entry(
        Area.ControlFlow,
        "exported-entry-not-declared",
        Severity.Error,
        "`{0}` is exported from inside routine `{1}`: mark it an entry point with a `.state` after the label",
        "An exported label inside a routine lets other modules jump into the middle of it, with a processor state "
            + "this module cannot see. A `.state` after the label declares it an entry point and what the processor "
            + "state is there, so the routine is checked from it. Only 65816 code needs this, because only there does "
            + "the code after the label depend on the processor state.");

    internal static DiagnosticDescriptor CodeLabelAsData { get; } = Entry(
        Area.ControlFlow,
        "code-label-as-data",
        Severity.Error,
        "taking the address of `{0}` lets code jump to it unseen: add a `.state` after the label, or name it in a `.next` in `{1}`",
        "Taking the address of an instruction, in a table, a `pea` or an immediate, means something may later jump "
            + "to it indirectly, where the analysis cannot follow. Declare the label an entry point with a "
            + "`.state` after it, or name it in a `.next` on the indirect jump in its own routine, so the analysis "
            + "follows flow to it.");

    internal static DiagnosticDescriptor SelfModifyingUnchecked { get; } = Entry(
        Area.ControlFlow,
        "self-modifying-unchecked",
        Severity.Error,
        "{0} modifies the instruction at `{1}`: add `.patch {2}` after it to mark the self-modifying code",
        "The store writes into an instruction's bytes, so that instruction does not do what its source text shows. A "
            + "`.patch` naming the instruction's label, added after the store, acknowledges it and shows a "
            + "reader that the instruction is changed at run time.");

    internal static DiagnosticDescriptor HandlerCalled { get; } = Entry(
        Area.ControlFlow,
        "handler-called",
        Severity.Error,
        "`{0}` is an interrupt handler and cannot be called: it returns with `rti`, which would not return to the "
            + "caller",
        "The processor enters an interrupt handler by pushing the return address and the status flags, and the "
            + "handler leaves with `rti`, which pulls both. A call pushes only the return address, so the `rti` "
            + "would pull the wrong bytes and not return to the caller.");

    internal static DiagnosticDescriptor HandlerReturnsNotRti { get; } = Entry(
        Area.ControlFlow,
        "handler-returns-not-rti",
        Severity.Error,
        "`{0}` is an interrupt handler and must return with `rti`, not `{1}`",
        "The processor pushed the status flags when it entered the handler, and only `rti` pulls them. An `rts` or "
            + "`rtl` would leave the flags on the stack and return to the wrong address.");

    internal static DiagnosticDescriptor RtiOutsideHandler { get; } = Entry(
        Area.ControlFlow,
        "rti-outside-handler",
        Severity.Warning,
        "`{0}` returns with `rti`, but it is not marked `interrupt`: mark it so that it is checked as one",
        "A routine that leaves by `rti` is entered by the processor from anywhere, with whatever widths, direct page "
            + "and data bank the interrupted code had. Marking it `interrupt` makes nt65 assume nothing about them at "
            + "its entry, and reports a call to it, which an `rti` would not return from.");

    internal static DiagnosticDescriptor NoreturnReturns { get; } = Entry(
        Area.ControlFlow,
        "noreturn-returns",
        Severity.Error,
        "`{0}` is declared `noreturn`, but `{1}` returns from it",
        "The routine's signature declares that it never returns, and its callers are checked on that promise: nothing after "
            + "a call to it is expected to run. Leave it some other way, such as a `jmp`, or remove `noreturn` "
            + "from its signature.");

    internal static DiagnosticDescriptor InlineDataMissing { get; } = Entry(
        Area.ControlFlow,
        "inline-data-missing",
        Severity.Error,
        "`{0}` expects {1} directly after each call, and {2}",
        "The routine's `inline` signature item declares that each call is followed by data, which the routine reads and "
            + "returns past. This call is not followed by that data, so the routine would skip over the wrong "
            + "bytes. Put the data directly after the call, in the same segment and with no label between.");

    internal static DiagnosticDescriptor InlineCountNotConstant { get; } = Entry(
        Area.ControlFlow,
        "inline-count-not-constant",
        Severity.Error,
        "`{0}` has `{1}`, and the byte count it gives must be a constant",
        "How many bytes the routine skips after each call decides where flow continues, so the count in `inline "
            + "N` must be a number nt65 knows while it builds, and not negative.");

    internal static DiagnosticDescriptor TailCallToHandler { get; } = Entry(
        Area.ControlFlow,
        "tail-call-to-handler",
        Severity.Error,
        "{0} is a tail call into interrupt handler `{1}`, whose `rti` would not return to this routine's caller",
        "A tail call is a jump that leaves the callee to return to this routine's caller. An interrupt handler "
            + "returns with `rti`, which pulls the status flags as well as the address, and nothing pushed them, "
            + "so it would not return properly. Call the handler's code some other way, or jump to it only from "
            + "another handler or a `noreturn` routine.");

    internal static DiagnosticDescriptor TailCallDistanceMismatch { get; } = Entry(
        Area.ControlFlow,
        "tail-call-distance-mismatch",
        Severity.Error,
        "{0} is a tail call, but `{1}` is {2} and `{3}` is {4}, so `{1}` would return the wrong way to `{3}`'s "
            + "caller",
        "In a tail call the callee returns directly to this routine's caller. A near routine returns with `rts` "
            + "and a far one with `rtl`, so the callee must be the same distance as this routine, or the caller "
            + "would get the wrong kind of return. Call it with `jsr` or `jsl` and return normally instead.");

    internal static DiagnosticDescriptor KeepsBroken { get; } = Entry(
        Area.ControlFlow,
        "keeps-broken",
        Severity.Error,
        "`{0}` promises `keeps {1}`, but {2} {3} not the same as on entry here{4}",
        "The routine's signature declares that it keeps these registers: every path that leaves it returns them holding the "
            + "value they had on entry, and callers rely on that. On this path the analysis sees a register "
            + "changed and not restored. Save and restore it, for example with `pha` and `pla`, or, where it is "
            + "restored in a way the analysis cannot see, add `.state keeps REG` at that point.");

    internal static DiagnosticDescriptor UnpromisedKeep { get; } = Entry(
        Area.ControlFlow,
        "unpromised-keep",
        Severity.Warning,
        "this {0} relies on `{1}` keeping {2}, {3}",
        "A routine that declares `keeps` promises the registers it lists, and only those. nt65 still finds what "
            + "its body keeps and uses that, so the analysis stays accurate, but a register the list leaves out may "
            + "change whenever the body does. Here the code after the call uses a value a register held before the "
            + "call, or this routine's own `keeps` promise depends on it. A routine that declares no `keeps` keeps "
            + "what its own code keeps, and passes on, without promoting it, whatever the routines it calls keep "
            + "without promising: relying on that through it draws this warning too, naming the routine that "
            + "declined. Add the register to that routine's `keeps`, save and restore it around the call, or, "
            + "where the reliance is deliberate, say so with `.allow \"unpromised-keep\"`. A routine with no body "
            + "keeps only what it declares, so it never draws this warning.");

    internal static DiagnosticDescriptor CallFlagMismatch { get; } = Entry(
        Area.ControlFlow,
        "call-flag-mismatch",
        Severity.Error,
        "`{0}` needs `{1}`, but {2}",
        "A routine whose signature gives a flag a value before `->`, as `c = 0` does, may only be called with the "
            + "flag at that value, and every call is checked against the signature. nt65 follows the flags only "
            + "through what the CPU defines, so a flag set from memory, or by a routine that does not declare it, "
            + "is not known. Set the flag before the call, for example with `.ensure c = 0`, which emits `clc` only "
            + "where the flag is not already 0.");

    internal static DiagnosticDescriptor ReturnFlagMismatch { get; } = Entry(
        Area.ControlFlow,
        "return-flag-mismatch",
        Severity.Error,
        "`{0}` declares it returns with `{1}`, but {2}",
        "A flag given a value after `->`, as in `-> c = 0`, is a promise to every caller, which may branch on it "
            + "without testing anything. nt65 checks it at every return, and at every tail call against what the "
            + "routine jumped to returns with. On this path the flag is not known to have that value. Set it before "
            + "returning, for example with `.ensure c = 0`, or correct the signature.");

    internal static DiagnosticDescriptor ReturnFlagNotSet { get; } = Entry(
        Area.ControlFlow,
        "return-flag-not-set",
        Severity.Error,
        "`{0}` declares `-> {2}`, but on this path nothing sets {1} before the return",
        "A flag named on its own after `->`, as in `-> c`, is a result the routine computes, such as a carry that "
            + "says whether a search found anything. Its caller branches on it, so every path through the routine "
            + "has to set it. On this path the flag still holds what the caller left in it. Set the flag on that "
            + "path, for example with `clc` or `sec`.");

    internal static DiagnosticDescriptor StateFlagMismatch { get; } = Entry(
        Area.ControlFlow,
        "state-flag-mismatch",
        Severity.Error,
        "`.state {0}` does not match: {1} here",
        "A `.state` that gives a flag a value declares it where nt65 cannot know it, such as after a call to a ROM "
            + "routine with no signature, and asserts it where nt65 can. Here the flags prove the other value on at "
            + "least one path. Fix the code on that path, or correct the `.state`.");

    internal static DiagnosticDescriptor UnpromisedFlag { get; } = Entry(
        Area.ControlFlow,
        "unpromised-flag",
        Severity.Warning,
        "{0} relies on `{1}` returning with `{2}`, {3}",
        "A routine that names any flag after `->` promises the flags it names, and only those. nt65 still finds "
            + "what its body returns the others with and uses that, so the analysis stays accurate, but a flag the "
            + "signature leaves out may change whenever the body does. Here a branch, a call's entry flags or a "
            + "return's exit flags depend on such a flag. A routine that names no flag after `->` returns what its "
            + "body is found to, and passes on, without promoting it, what the routines it calls return without "
            + "promising: relying on that through it draws this warning too, naming the routine that declined. "
            + "Add the flag to that routine's exit, set the flag yourself, or, where the reliance is deliberate, "
            + "say so with `.allow \"unpromised-flag\"`.");

    internal static DiagnosticDescriptor KeepsRedundant { get; } = Entry(
        Area.ControlFlow,
        "keeps-redundant",
        Severity.Warning,
        "`{0}` is redundant here: {1} already holds its value from entry",
        "The analysis already knows the register holds the value it had when the routine was entered, so the "
            + "`.state keeps` adds nothing. Remove it.");

    internal static DiagnosticDescriptor ReadsUndeclared { get; } = Entry(
        Area.ControlFlow,
        "reads-undeclared",
        Severity.Error,
        "`{0}` declares `{1}`, but uses the value its caller left in {2}{3}",
        "The routine's signature lists the registers whose values from its caller it uses, and its callers rely on "
            + "that list. On this path the routine, or a routine it passes control to, uses a register that is not "
            + "listed, with the value it had when the routine was entered. Add the register to `reads`, or give it a "
            + "value first. An unlisted carry is often a missing `clc` or `sec` before `adc` or `sbc`.");

    internal static DiagnosticDescriptor SavesNotAStore { get; } = Entry(
        Area.ControlFlow,
        "saves-not-a-store",
        Severity.Error,
        "`{0}` must stand directly under a store of {1}'s value: {2}",
        "`.state saves x` says that the store on the line above it only saves X, to be restored later, so that the "
            + "store does not count as a use of X's value. It must stand directly under `stx`, or under `sta` or "
            + "`sty` of a register that holds the same value as X, such as `sta` after `txa`.");

    // Processor state

    internal static DiagnosticDescriptor WidthUnknown { get; } = Entry(
        Area.ProcessorState,
        "width-unknown",
        Severity.Error,
        "`{0} #` needs the width of {1}, and {2}",
        "On the 65816 an immediate operand such as `lda #` or `ldx #` is one byte or two depending on the M or X "
            + "flag, so nt65 has to know how wide A, or X and Y, is on that line. Here it does not: the paths that "
            + "reach the line disagree, something the analysis cannot follow changed the flags, the routine's "
            + "signature declares `a*` or `i*`, or no path reaches the line at all. Put `.ensure a8`, `a16`, `i8` or "
            + "`i16` before the line to set the width, add a `.state` after a label to declare it, or give the "
            + "width in the routine's signature.");

    internal static DiagnosticDescriptor CallersDisagree { get; } = Entry(
        Area.ProcessorState,
        "callers-disagree",
        Severity.Error,
        "`{0}` is called with {1} by some callers and {2} by others, and `{3} #` depends on the width of {4}",
        "A routine that declares nothing about a width is entered with the width its callers agree on. Here they "
            + "disagree, and the routine has an immediate whose size depends on that width before anything sets "
            + "it, so its bytes cannot be right for every caller. Declare the width the routine expects in its "
            + "signature, and each caller in the other state is then reported where it calls, with an `.ensure` "
            + "to set the width there. Or set the width in the routine with `rep`, `sep` or `.ensure` before the "
            + "immediate.");

    internal static DiagnosticDescriptor ImmediateInEmulation { get; } = Entry(
        Area.ProcessorState,
        "immediate-in-emulation",
        Severity.Error,
        "`{0} #` has a 16-bit width here, but the processor is in emulation mode, where A, X and Y are always 8-bit",
        "In emulation mode the processor forces A, X and Y to 8 bits whatever the M and X flags are, so an "
            + "immediate is always one byte. The analysis finds a 16-bit width and emulation mode on the same "
            + "path, which cannot both be true. Check the signature or `.state` that declared the 16-bit width, or "
            + "the `xce` that entered emulation mode.");

    internal static DiagnosticDescriptor WidthInEmulation { get; } = Entry(
        Area.ProcessorState,
        "width-in-emulation",
        Severity.Error,
        "{0} is impossible in emulation mode, where A, X and Y are always 8-bit",
        "In emulation mode the processor forces A, X and Y to 8 bits whatever the M and X flags are, so a `.state` "
            + "or signature that declares `emu` together with `a16` or `i16` describes a state the processor "
            + "cannot be in. Declare `a8` and `i8`, or leave the widths out: `emu` already implies them.");

    internal static DiagnosticDescriptor EnsureNeedsNative { get; } = Entry(
        Area.ProcessorState,
        "ensure-needs-native",
        Severity.Error,
        "`.ensure {0}` needs native mode, and {1}",
        "`.ensure a16` and `.ensure i16` emit a `rep`, and 16-bit widths exist only in native mode: in emulation "
            + "mode A, X and Y stay 8-bit whatever `rep` does. Switch to native mode (`clc` then `xce`) before the "
            + "`.ensure`, or, where the analysis cannot see the mode, declare it with `native` in the routine's "
            + "signature or a `.state native`.");

    internal static DiagnosticDescriptor EnsureItemNotAWidth { get; } = Entry(
        Area.ProcessorState,
        "ensure-item-not-a-width",
        Severity.Error,
        "`.ensure` cannot set `{0}`: it sets only widths, `c`, `d` and `i` to 0 or 1, and `v` to 0",
        "`.ensure` emits the `rep` or `sep` that makes a register width true, and the `clc`, `sec`, `cld`, `sed`, "
            + "`cli`, `sei` or `clv` that sets a flag, each only where the analysis does not find it already so. "
            + "Those are the only parts of the processor state it can set without changing a register: Z and N "
            + "follow every result, and nothing sets V on its own. To declare anything else at a point, such as "
            + "the mode, D or B, use `.state`.");

    internal static DiagnosticDescriptor StateItemNotAPoint { get; } = Entry(
        Area.ProcessorState,
        "state-item-not-a-point",
        Severity.Error,
        "`{0}` describes a whole routine, not one point in it: put it in the routine's signature, not in `.state`",
        "A `.state` declares the processor state at one line: register widths, mode, D and B. Items that describe "
            + "the routine as a whole, such as `near`, `far`, `args`, `inline`, `interrupt`, `noreturn`, `reads`, a "
            + "signature set, or a `*` item meaning unchanged since entry, belong in the signature after the "
            + "routine's name.");

    internal static DiagnosticDescriptor StateOutsideARoutine { get; } = Entry(
        Area.ProcessorState,
        "state-outside-a-routine",
        Severity.Error,
        "`{0}` must be inside a `.proc`",
        "`.state`, `.ensure` and `.frame` describe or change the processor state or the stack at one point in a "
            + "routine's code, and the analysis follows that state only inside a `.proc`. Move the directive into "
            + "the routine it belongs to.");

    internal static DiagnosticDescriptor StateModeMismatch { get; } = Entry(
        Area.ProcessorState,
        "state-mode-mismatch",
        Severity.Error,
        "`.state {0}` does not match: the processor is in {1} mode here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the processor is in the other mode. Fix the code on that path, "
            + "or correct the `.state`.");

    internal static DiagnosticDescriptor StateWidthMismatch { get; } = Entry(
        Area.ProcessorState,
        "state-width-mismatch",
        Severity.Error,
        "`.state {0}` does not match: {1} {2} {3} here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the register has the other width. Fix the code on that path, "
            + "for example with `.ensure`, or correct the `.state`.");

    internal static DiagnosticDescriptor StateValueMismatch { get; } = Entry(
        Area.ProcessorState,
        "state-value-mismatch",
        Severity.Error,
        "`.state {0}` does not match: {1} is {2} here",
        "A `.state` declares what is true at a point, and the analysis checks it against every path that reaches "
            + "that point. On at least one of them the direct page register D, or the data bank register B, holds "
            + "a different value. Fix the code on that path, or correct the `.state`.");

    internal static DiagnosticDescriptor StateValueNotConstant { get; } = Entry(
        Area.ProcessorState,
        "state-value-not-constant",
        Severity.Error,
        "`.state {0}` needs a constant value for {1}",
        "The analysis tracks the direct page register D and the data bank register B as exact values, to check "
            + "each direct-page and absolute operand against them. So the value in `.state dp = ...` or `.state "
            + "dbr = ...` must be a constant expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor StateValueOutOfRange { get; } = Entry(
        Area.ProcessorState,
        "state-value-out-of-range",
        Severity.Error,
        "`.state {0}` is out of range: {1}",
        "The direct page register D holds a 16-bit address, $0000 to $ffff, and the data bank register B holds one "
            + "byte, $00 to $ff.");

    internal static DiagnosticDescriptor CallStateMismatch { get; } = Entry(
        Area.ProcessorState,
        "call-state-mismatch",
        Severity.Error,
        "{0} needs `{1}`, but {2}",
        "Every call is checked against the entry state in the called routine's signature, not against its body. "
            + "Here the state at the call differs from what that signature requires. Change the state before the "
            + "call, for example with `.ensure a16`, or correct the callee's signature if it is wrong.");

    internal static DiagnosticDescriptor ReturnStateMismatch { get; } = Entry(
        Area.ProcessorState,
        "return-state-mismatch",
        Severity.Error,
        "{0}`{1}` declares it returns {2}, but {3}",
        "A routine's signature declares what state it returns in: the state after `->`, or its entry state when there "
            + "is no `->`. Callers rely on it. On this path the state at the return, or for a tail call the state "
            + "when the routine jumped to returns, is different. Restore the state before returning, or correct "
            + "the signature.");

    internal static DiagnosticDescriptor AssertedItemNotRestored { get; } = Entry(
        Area.ProcessorState,
        "asserted-item-not-restored",
        Severity.Error,
        "{0}`{1}` declares `{2}`, so {3} must be {4}, but {5} it may not be",
        "A `*` item in a signature, such as `a*` or `dp*`, means that the routine returns that part of the processor "
            + "state exactly as it found it on entry, whatever it was. On this path the routine may have changed "
            + "it without restoring it. Restore it before returning, for example with `php` and `plp` around a "
            + "width change, or by saving and restoring D or B.");

    internal static DiagnosticDescriptor CallTargetUnknown { get; } = Entry(
        Area.ProcessorState,
        "call-target-unknown",
        Severity.Error,
        "`{0}` must call a routine on the 65816: a `.proc`, an extern proc or a `proc(...)` import",
        "On the 65816 every call is checked against the called routine's signature, which declares what register "
            + "widths, mode, D and B it expects. A call to a bare address, or to anything else without a "
            + "signature, cannot be checked, so the target has to be a `.proc`, an extern proc or a `proc(...)` "
            + "import.");

    internal static DiagnosticDescriptor CallTargetNotARoutine { get; } = Entry(
        Area.ProcessorState,
        "call-target-not-a-routine",
        Severity.Error,
        "`{0}` is not a routine: on the 65816, a call must target a `.proc`, an extern proc or a `proc(...)` import",
        "On the 65816 every call is checked against the called routine's signature. What this call names has no "
            + "signature, so there is nothing to check it against. Call a `.proc`, an extern proc or a `proc(...)` "
            + "import instead.");

    internal static DiagnosticDescriptor CallDistanceMismatch { get; } = Entry(
        Area.ProcessorState,
        "call-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, so call it with `{2}`",
        "A near routine is called with `jsr`, which pushes a two-byte return address, and returns with `rts`; a "
            + "far routine is called with `jsl`, which also pushes the program bank, and returns with `rtl`. "
            + "Calling it the other way leaves the stack unbalanced when it returns. Use the instruction the "
            + "message names, or change `near` or `far` in the routine's signature.");

    internal static DiagnosticDescriptor ReturnDistanceMismatch { get; } = Entry(
        Area.ProcessorState,
        "return-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, so it returns with `{2}`",
        "A far routine is called with `jsl` and returns with `rtl`; a near one is called with `jsr` and returns "
            + "with `rts`. Returning the other way pulls the wrong number of bytes off the stack. Use the "
            + "instruction the message names, or change `near` or `far` in the routine's signature.");

    internal static DiagnosticDescriptor JumpDistanceMismatch { get; } = Entry(
        Area.ProcessorState,
        "jump-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}: jump to it with `{2} {3}`",
        "`jmp` stays in the current bank, and `jml` also sets the program bank. A far routine is reached with "
            + "`jml`, and a near one in the same bank with `jmp`.");

    internal static DiagnosticDescriptor BranchToFarRoutine { get; } = Entry(
        Area.ProcessorState,
        "branch-to-far-routine",
        Severity.Error,
        "`{0}` is far, and a branch cannot set the program bank: branch on the opposite condition around a `jml {0}`",
        "A far routine is reached with `jml`, which also sets the program bank. A conditional branch stays in the "
            + "current bank and has no long form, so it cannot be changed into a `jml`. Branch on the opposite "
            + "condition past a `jml` to the routine instead.");

    internal static DiagnosticDescriptor JumpAcrossBanks { get; } = Entry(
        Area.ProcessorState,
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
        Area.ProcessorState,
        "jump-leaves-bank",
        Severity.Error,
        "{1} is in bank {2}, and `{0}` cannot leave bank {3}: {4}",
        "`jsr`, `jmp` and the branches change only the 16-bit address and keep the program bank, so they cannot "
            + "reach code in a segment that the project places in another bank. Use `jsl` or `jml`, which set the "
            + "bank too; a conditional branch has no long form, so branch on the opposite condition around a `jml` "
            + "to the target.");

    internal static DiagnosticDescriptor RelativeCallNeedsPhk { get; } = Entry(
        Area.ProcessorState,
        "relative-call-needs-phk",
        Severity.Error,
        "`{0}` is far: push the bank with `phk` before the `per` of this relative call",
        "A relative call is a `per` that pushes the return address, then a branch to the routine. `per` "
            + "pushes only 16 bits, but a far routine returns with `rtl`, which pulls three bytes: the address and "
            + "the bank. Put a `phk` before the `per` so the bank is on the stack.");

    internal static DiagnosticDescriptor RelativeCallExtraPhk { get; } = Entry(
        Area.ProcessorState,
        "relative-call-extra-phk",
        Severity.Error,
        "`{0}` is near: remove the `phk` from this relative call",
        "A near routine returns with `rts`, which pulls only the two-byte return address that `per` pushed. The "
            + "byte `phk` pushed would be left on the stack. Remove the `phk`, or make the routine `far`.");

    internal static DiagnosticDescriptor ArgsNotPushed { get; } = Entry(
        Area.ProcessorState,
        "args-not-pushed",
        Severity.Error,
        "`{0}` declares `args {1}`, bytes the caller pushes before the call, but {2}",
        "`args n` in a routine's signature states that the caller pushes n bytes of arguments before calling it, and the "
            + "routine reads or pulls exactly that many. At this call fewer bytes are on the stack than that, "
            + "counting what the calling routine has pushed since its entry. Push the arguments before the call.");

    internal static DiagnosticDescriptor DirectPageMismatch { get; } = Entry(
        Area.ProcessorState,
        "direct-page-mismatch",
        Severity.Error,
        "`{0}` is in segment `{1}`, which expects the direct page at {2}, but D is {3} here",
        "A segment in the project file can declare `dp`, the value D must hold for its variables to be reached "
            + "with one-byte direct-page operands. Here D holds another value, so the operand would reach a "
            + "different address. Set D before this line, or declare its value with `.state dp = ...` or `dp = "
            + "...` in the routine's signature.");

    internal static DiagnosticDescriptor DirectPageUnknown { get; } = Entry(
        Area.ProcessorState,
        "direct-page-unknown",
        Severity.Error,
        "{0}, and {1}",
        "A `d:` operand, and a direct operand on a symbol whose segment declares `dp`, is an offset from the "
            + "direct page register D, so nt65 needs D's value to know which address it reaches. Set D first, or "
            + "declare it with `.state dp = ...` at this point or with `dp = ...` in the routine's signature. An "
            + "interrupt handler starts with D unknown, because it runs with whatever D the interrupted code held, "
            + "so it sets D itself.");

    internal static DiagnosticDescriptor DirectPageOutOfReach { get; } = Entry(
        Area.ProcessorState,
        "direct-page-out-of-reach",
        Severity.Error,
        "{0} at {1}, which covers only {2} to {3}",
        "A direct-page operand is a one-byte offset from D, so it reaches only the 256 bytes from D to D+$ff, and "
            + "this address is outside them. Change D, or use an absolute operand.");

    internal static DiagnosticDescriptor BankMismatch { get; } = Entry(
        Area.ProcessorState,
        "bank-mismatch",
        Severity.Error,
        "`{0}` is in segment `{1}`, which is {2}, but B is {3} here",
        "A segment in the project file can declare the bank its contents are in. An absolute operand is read from "
            + "the bank in the data bank register B, so with B holding another bank it would reach the same "
            + "address in the wrong bank. Set B first (for example with `plb`), declare it with `.state dbr = "
            + "...`, or use a long operand such as `f:`.");

    internal static DiagnosticDescriptor RangeBankMismatch { get; } = Entry(
        Area.ProcessorState,
        "range-bank-mismatch",
        Severity.Error,
        "{0} is reachable only from banks {1}, but B is {2} here",
        "The project file's `ranges` state which banks each absolute address can be reached from, for hardware that "
            + "is mirrored only in some banks. Here the data bank register B may hold a bank outside that set, so "
            + "the operand would reach something else. Set B to one of the listed banks, or use a long operand.");

    internal static DiagnosticDescriptor MirrorBankMismatch { get; } = Entry(
        Area.ProcessorState,
        "mirror-bank-mismatch",
        Severity.Error,
        "`{0}` is in segment `{1}`, {2}, but this reaches it through bank {3}",
        "The segment's `bank` and `mirrors` in the project file state which banks its contents appear in. This long "
            + "address names the routine in a bank that is neither, so it would land somewhere else. Use an "
            + "address in the segment's bank or in one of its mirrors.");

    internal static DiagnosticDescriptor FrameNotARecord { get; } = Entry(
        Area.ProcessorState,
        "frame-not-a-record",
        Severity.Error,
        "`.frame {0}` needs a struct or union type: its size gives how many stack bytes the frame covers",
        "`.frame name: T` lays out the top of the stack as the struct or union T, and T's size gives how many bytes "
            + "the frame covers. Anything else has no members or size to lay out. Give the frame a struct or union "
            + "type.");

    internal static DiagnosticDescriptor FramePastTheStack { get; } = Entry(
        Area.ProcessorState,
        "frame-past-the-stack",
        Severity.Error,
        "frame `{0}` is {1} bytes, but only {2} bytes are pushed here",
        "A `.frame` names bytes on top of the stack, so it can cover at most what this routine has pushed so far. "
            + "This one covers more, so its last members would name the return address or the caller's bytes. Push "
            + "the frame's bytes before the `.frame`, or use a smaller type.");

    internal static DiagnosticDescriptor FrameStackUnknown { get; } = Entry(
        Area.ProcessorState,
        "frame-stack-unknown",
        Severity.Error,
        "frame `{0}` names the top {1} bytes of the stack, but how many bytes are pushed here is not known{2}",
        "A `.frame` names bytes on top of the stack, so it can cover at most what this routine has pushed so far. "
            + "Here nt65 cannot count that, because paths that pushed different amounts meet, another routine may "
            + "jump in having pushed nothing, or something pushed a width that is not known. On one of those paths "
            + "the frame would name the return address or the caller's bytes. After `tcs` or `txs` the program has "
            + "moved the stack itself, and a frame may reach beneath what is known.");

    internal static DiagnosticDescriptor FrameDepthUnknown { get; } = Entry(
        Area.ProcessorState,
        "frame-depth-unknown",
        Severity.Error,
        "`{0}` is an offset from the stack pointer, but how many bytes are pushed here is not known{1}",
        "A frame member becomes an `n,s` operand whose offset counts every byte pushed since the `.frame`. Where "
            + "the paths reaching this line push different amounts, or something the analysis cannot follow moved "
            + "the stack pointer, the offset cannot be worked out. Make every path push the same bytes, or repeat "
            + "the `.frame` after the point where the stack changes.");

    internal static DiagnosticDescriptor FrameGone { get; } = Entry(
        Area.ProcessorState,
        "frame-gone",
        Severity.Error,
        "`{0}` is in frame `{1}`, which has been pulled off the stack here",
        "The bytes the frame covered have been pulled off the stack by this point, so its members no longer name "
            + "anything. Use the member before those pulls, or add a new `.frame` for what is on the stack now.");

    internal static DiagnosticDescriptor FrameMemberNotStackRelative { get; } = Entry(
        Area.ProcessorState,
        "frame-member-not-stack-relative",
        Severity.Error,
        "`{0}` is a stack slot: use it as `{1},s`",
        "A frame member is an offset from the stack pointer, not an address, so it can only be the whole operand "
            + "of a stack-relative instruction, as in `lda locals::count,s` or `lda (locals::ptr,s),y`. It cannot "
            + "be used in an expression or with another addressing mode.");

    // Output

    internal static DiagnosticDescriptor OutputNameCollision { get; } = Entry(
        Area.Output,
        "output-name-collision",
        Severity.Error,
        "`{0}` and {1} both become `{2}` in the ca65 output",
        "nt65 writes ca65 source, and flattens its scoped names into ca65 symbols. These two different names "
            + "flatten to the same ca65 symbol, so the output would define it twice. Rename one of them.");

    internal static DiagnosticDescriptor CHeaderUntyped { get; } = Entry(
        Area.Output,
        "c-header-untyped",
        Severity.Warning,
        "`{0}` has type `{1}`, which is not exported, so the C header declares `{0}` as bytes",
        "The C header declares only exported types. This exported data has a struct or union type that is not "
            + "exported, so the header cannot name it and declares the data as an `unsigned char` array of the "
            + "same size. Export the type to give the data its C type.");

    internal static DiagnosticDescriptor ExportNameTaken { get; } = Entry(
        Area.Output,
        "export-name-taken",
        Severity.Error,
        "`{0}` and `{1}` are both exported to the linker as `{2}`",
        "Exports share one flat namespace with everything else the linker sees, so two exports may not reach it "
            + "under one name. `as` gives one of them a name of its own.");

    internal static DiagnosticDescriptor CannotBeTranslated { get; } = Entry(
        Area.Output,
        "cannot-be-translated",
        Severity.Error,
        "internal error: nt65 cannot translate `{0}` to ca65; please report this bug",
        "Everything nt65 accepts should have a ca65 translation, and anything it cannot translate should have been "
            + "reported as an error first. Reaching this means neither happened, which is a bug in nt65 rather "
            + "than in the program. Please report it with the line that triggers it.");

    // The project file

    internal static DiagnosticDescriptor ProjectJsonInvalid { get; } = Entry(
        Area.TheProjectFile,
        "project-json-invalid",
        Severity.Error,
        "{0}",
        "The project file is not valid JSON. Comments and trailing commas are allowed; otherwise it follows "
            + "standard JSON syntax. The message gives the parser's reason, and the position points at where it "
            + "stopped.");

    internal static DiagnosticDescriptor ProjectNotAnObject { get; } = Entry(
        Area.TheProjectFile,
        "project-not-an-object",
        Severity.Error,
        "{0} must hold one JSON object",
        "The top level of the project file is one JSON object, whose keys state what the program is built from and "
            + "how.");

    internal static DiagnosticDescriptor ProjectKeyUnknown { get; } = Entry(
        Area.TheProjectFile,
        "project-key-unknown",
        Severity.Error,
        "`{0}` is not a key of {1}{2}",
        "The project file accepts a fixed set of keys: `cpu`, `files`, `out`, `settings`, `diagnostics`, `spaces`, "
            + "`segments`, `ranges` and `configurations`, plus `$schema`, which is accepted and ignored. An "
            + "unknown key is an error, so that a misspelt setting does not silently do nothing.");

    internal static DiagnosticDescriptor ProjectCpuUnknown { get; } = Entry(
        Area.TheProjectFile,
        "project-cpu-unknown",
        Severity.Error,
        "`{0}` is not a supported `cpu`: use {1}",
        "`cpu` names the processor the program is built for, spelt exactly as one of the listed names.");

    internal static DiagnosticDescriptor ProjectNotAList { get; } = Entry(
        Area.TheProjectFile,
        "project-not-a-list",
        Severity.Error,
        "`{0}` must be a list of strings",
        "`files` takes a JSON array of strings: glob patterns, relative to the project root, that name the "
            + "program's source files.");

    internal static DiagnosticDescriptor ProjectNotAString { get; } = Entry(
        Area.TheProjectFile,
        "project-not-a-string",
        Severity.Error,
        "`{0}` must be a string",
        "This key takes a JSON string: `cpu` a processor name, `out` the directory the build writes its output into.");

    internal static DiagnosticDescriptor ProjectValueNotAnObject { get; } = Entry(
        Area.TheProjectFile,
        "project-value-not-an-object",
        Severity.Error,
        "`{0}` must be an object",
        "This key takes a JSON object whose own keys are its entries: setting names, diagnostic names, "
            + "configuration names, segment names, space names or address ranges.");

    internal static DiagnosticDescriptor SettingNameInvalid { get; } = Entry(
        Area.TheProjectFile,
        "setting-name-invalid",
        Severity.Error,
        "`{0}` is not a valid setting name: use letters, digits and `_`, optionally after a module path such as "
            + "`hw::`",
        "A setting is named as a constant is: a letter or `_` followed by letters, digits and `_`. It may have a "
            + "module's path in front of it, as in `hw::SOUND`, to name the module that declares it.");

    internal static DiagnosticDescriptor SettingNotANumber { get; } = Entry(
        Area.TheProjectFile,
        "setting-not-a-number",
        Severity.Error,
        "setting `{0}` must be a number",
        "A setting is a number. In the project file give a JSON number or a string in nt65 number syntax, such as "
            + "\"$20\"; on the command line, `-D NAME=value` takes the same syntax, and `-D NAME` alone sets it to "
            + "1.");

    internal static DiagnosticDescriptor ConfigurationNameInvalid { get; } = Entry(
        Area.TheProjectFile,
        "configuration-name-invalid",
        Severity.Error,
        "`{0}` is not a valid configuration name: use only letters, digits, `_` and `-`",
        "A configuration is chosen by name with `--config` on the command line, so its name is limited to "
            + "characters that need no quoting.");

    internal static DiagnosticDescriptor ConfigurationNotAnObject { get; } = Entry(
        Area.TheProjectFile,
        "configuration-not-an-object",
        Severity.Error,
        "configuration `{0}` must be an object, with any of `settings`, `diagnostics`, `links` and `out`",
        "A named configuration is a JSON object that changes the project's `settings`, `diagnostics`, `links` and "
            + "`out` for builds that choose it with `--config`: its settings, diagnostic severities and links take "
            + "precedence over the project's, and its `out` replaces the project's.");

    internal static DiagnosticDescriptor ConfigurationKeyUnknown { get; } = Entry(
        Area.TheProjectFile,
        "configuration-key-unknown",
        Severity.Error,
        "configuration `{0}` cannot set `{1}`: a configuration may set only `settings`, `diagnostics`, `links` and "
            + "`out`",
        "A named configuration changes only a few things about a build: its settings, its diagnostic severities, "
            + "its links and its output directory. Everything else, such as `cpu`, `files` and `segments`, is set "
            + "once for the whole project.");

    internal static DiagnosticDescriptor ConfigurationUnknown { get; } = Entry(
        Area.TheProjectFile,
        "configuration-unknown",
        Severity.Error,
        "`{0}` is not a configuration: {1}",
        "The name given to `--config`, or to `default` in the project file, has to be one of the configurations "
            + "declared under `configurations` in the project file. Check the spelling against the names listed.");

    internal static DiagnosticDescriptor ProjectSegmentNotAnObject { get; } = Entry(
        Area.TheProjectFile,
        "project-segment-not-an-object",
        Severity.Error,
        "segment `{0}` must be an object with a `size`",
        "Each entry under `segments` is an object describing that segment: its address `size`, which is required, "
            + "and optionally `dp`, `bank`, `mirrors` and `space`.");

    internal static DiagnosticDescriptor ProjectSegmentSizeMissing { get; } = Entry(
        Area.TheProjectFile,
        "project-segment-size-missing",
        Severity.Error,
        "segment `{0}` needs a `size` of \"zp\", \"abs\" or \"far\"",
        "A segment's `size` is how wide addresses in it are: \"zp\" for one-byte zero-page addresses, \"abs\" for "
            + "two-byte absolute ones, and \"far\" for three-byte long ones. It decides how every reference to the "
            + "segment's contents is assembled, so every segment has to give one.");

    internal static DiagnosticDescriptor ProjectSegmentFromLink { get; } = Entry(
        Area.TheProjectFile,
        "project-segment-from-link",
        Severity.Error,
        "segment `{0}` is linked, so `segments` cannot set its `{1}`: {2}",
        "With `links`, a segment is declared by the ld65 configs that place it. Its size comes from its `type`, "
            + "and its bank from where it runs. Mirrors and spaces are facts about memory, so they are given on the "
            + "memory area under the link. An entry under `segments` adds only what no config can say: `far`, a "
            + "`dp`, or a `bank` the config leaves unclear.");

    internal static DiagnosticDescriptor LinkNotAnObject { get; } = Entry(
        Area.TheProjectFile,
        "link-not-an-object",
        Severity.Error,
        "link `{0}` must be an object with a `config`",
        "Each entry under `links` names a link and describes it: `config`, the path of its ld65 config from the "
            + "project file, which is required, and optionally `space` and `memory`.");

    internal static DiagnosticDescriptor LinkKeyUnknown { get; } = Entry(
        Area.TheProjectFile,
        "link-key-unknown",
        Severity.Error,
        "{0} cannot set `{1}`: {2}",
        "A link sets its `config`, the `space` everything in it is in, and `memory`, which describes the "
            + "config's memory areas by name. A memory area sets its `mirrors` and its `space`. Any other key is an "
            + "error, so that a misspelt one does not silently do nothing.");

    internal static DiagnosticDescriptor LinkedConfigUnreadable { get; } = Entry(
        Area.TheProjectFile,
        "linked-config-unreadable",
        Severity.Error,
        "cannot read the linker config `{0}`",
        "A link's `config` is a path from the project file to an ld65 config, which nt65 reads for the segments "
            + "it declares. Check that the file exists at that path.");

    internal static DiagnosticDescriptor LinkedConfigInvalid { get; } = Entry(
        Area.TheProjectFile,
        "linked-config-invalid",
        Severity.Error,
        "this linker config cannot be read: {0}",
        "nt65 reads the `MEMORY`, `SEGMENTS` and `SYMBOLS` blocks of a linked ld65 config and skips the rest. "
            + "Each entry is a name, a colon, attributes such as `start = $8000` and a semicolon. ld65 would "
            + "reject the same text, so fix it there.");

    internal static DiagnosticDescriptor LinkMemoryUnknown { get; } = Entry(
        Area.TheProjectFile,
        "link-memory-unknown",
        Severity.Error,
        "`{0}` has no memory area `{1}`",
        "A link's `memory` describes the memory areas of its config by the names the config's `MEMORY` block "
            + "gives them. Check the spelling against the config.");

    internal static DiagnosticDescriptor ProjectSpaceHoldsUnknown { get; } = Entry(
        Area.TheProjectFile,
        "project-space-holds-unknown",
        Severity.Error,
        "space `{0}` must be \"code\" or \"data\"",
        "Each entry under `spaces` names an address space and states what it holds. \"code\" means the space runs "
            + "this program's processor, and nt65 checks its code; \"data\" means anything else, including code "
            + "for another processor, which nt65 treats as data and macro calls.");

    internal static DiagnosticDescriptor ProjectSegmentKeyUnknown { get; } = Entry(
        Area.TheProjectFile,
        "project-segment-key-unknown",
        Severity.Error,
        "segment `{0}` cannot set `{1}`: a segment may set only `size`, `dp`, `bank`, `mirrors` and `space`",
        "A segment's entry states how wide its addresses are (`size`), which direct page and banks its contents are "
            + "reached through (`dp`, `bank`, `mirrors`), and which address space it is in (`space`). Any other "
            + "key is an error, so that a misspelt one does not silently do nothing.");

    internal static DiagnosticDescriptor DiagnosticNameUnknown { get; } = Entry(
        Area.TheProjectFile,
        "diagnostic-name-unknown",
        Severity.Error,
        "`{0}` is not a diagnostic nt65 reports{1}",
        "The names are a fixed set, so a typo is a mistake rather than a line that quietly does nothing. "
            + "`nt65 explain` lists them.");

    internal static DiagnosticDescriptor DiagnosticSeverityUnknown { get; } = Entry(
        Area.TheProjectFile,
        "diagnostic-severity-unknown",
        Severity.Error,
        "the severity of `{0}` must be \"off\", \"warning\" or \"error\"",
        "Under `diagnostics`, each diagnostic's name maps to one of three severities: \"off\" to silence it, "
            + "\"warning\" to report it without failing the build, or \"error\" to fail the build.");

    internal static DiagnosticDescriptor DiagnosticNotTurnedDown { get; } = Entry(
        Area.TheProjectFile,
        "diagnostic-not-turned-down",
        Severity.Error,
        "`{0}` is an error, and a project cannot turn an error into a warning or off",
        "A warning points at something that may be intended; an error means nt65 cannot produce correct output for "
            + "the program. Lowering an error would not make the output right, only hide the problem, so "
            + "`diagnostics` can change only warnings.");

    internal static DiagnosticDescriptor RangeInvalid { get; } = Entry(
        Area.TheProjectFile,
        "range-invalid",
        Severity.Error,
        "`{0}` is not a range of absolute addresses, such as \"$2100-$21ff\"",
        "A key under `ranges` is one absolute address, or two with a `-` between them, lowest first, each from "
            + "$0000 to $ffff.");

    internal static DiagnosticDescriptor RangesOverlap { get; } = Entry(
        Area.TheProjectFile,
        "ranges-overlap",
        Severity.Error,
        "`{0}` overlaps `{1}-{2}`: an address can be in only one range",
        "Each range states which banks its addresses can be reached from, so an address in two ranges would have two "
            + "answers. Change the ranges so they do not overlap.");

    internal static DiagnosticDescriptor BanksNotAList { get; } = Entry(
        Area.TheProjectFile,
        "banks-not-a-list",
        Severity.Error,
        "`{0}` must be a list of banks, such as [\"$00-$3f\", \"$80-$bf\"]",
        "Banks are given as a JSON array. Each item is one bank, as a number or a string, or a string holding a "
            + "range of banks such as \"$00-$3f\".");

    internal static DiagnosticDescriptor BankInvalid { get; } = Entry(
        Area.TheProjectFile,
        "bank-invalid",
        Severity.Error,
        "`{0}`: {1} is not a bank or a range of banks",
        "A bank is one byte, $00 to $ff, and a range of banks is two of them with a `-` between them, lowest first.");

    // Signatures

    internal static DiagnosticDescriptor SignatureSetSelfReference { get; } = Entry(
        Area.Signatures,
        "signature-set-self-reference",
        Severity.Error,
        "signature set `{0}` includes itself through `{1}`",
        "A signature set is replaced by the items it names. If it names itself, directly or through other sets, "
            + "that replacement never ends. Remove the reference that closes the loop.");

    internal static DiagnosticDescriptor AliasDistanceMismatch { get; } = Entry(
        Area.Signatures,
        "alias-distance-mismatch",
        Severity.Error,
        "`{0}` is declared {1}, but `{2}`, the routine it names, is {3}",
        "`.proc name = routine: ...` declares another name for an existing routine. It is the same code, so it is "
            + "called and returns the same way: a near routine with `jsr` and `rts`, a far one with `jsl` and "
            + "`rtl`. Give the alias the routine's own `near` or `far`, or leave its signature out to take the "
            + "routine's.");

    internal static DiagnosticDescriptor AliasSignatureMismatch { get; } = Entry(
        Area.Signatures,
        "alias-signature-mismatch",
        Severity.Error,
        "`{0}` is declared `{1}`, but `{2}`, the routine it names, is `{3}`: another name for a routine must "
            + "declare the same signature",
        "Calls through another name for a routine are checked against the signature on that name, so a name that "
            + "declares something different from the routine would check the same code two different ways. Give it "
            + "the routine's signature, or leave its signature out to take the routine's.");

    internal static DiagnosticDescriptor ArgsNotConstant { get; } = Entry(
        Area.Signatures,
        "args-not-constant",
        Severity.Error,
        "`{0}` needs a constant byte count",
        "`args n` states how many bytes the caller pushes before the call. The analysis uses it to check every call "
            + "and to work out where the routine finds its arguments on the stack, so n has to be a constant "
            + "expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor ArgsOutOfRange { get; } = Entry(
        Area.Signatures,
        "args-out-of-range",
        Severity.Error,
        "`{0}` is out of range: the byte count must be from 0 to $ffff",
        "`args n` counts bytes on the stack, so n has to be from 0 to $ffff, the largest amount the 65816's stack can hold.");

    internal static DiagnosticDescriptor NoreturnDeclaresAnExit { get; } = Entry(
        Area.Signatures,
        "noreturn-declares-an-exit",
        Severity.Error,
        "a `noreturn` routine never returns, so it cannot declare a state after `->`",
        "What follows `->` in a signature is the state a routine returns in, for the caller to rely on after the "
            + "call. A `noreturn` routine never returns, so there is nothing to declare. Remove the `->` and what "
            + "follows it.");

    internal static DiagnosticDescriptor FlagResultAtEntry { get; } = Entry(
        Area.Signatures,
        "flag-result-at-entry",
        Severity.Error,
        "`{0}` on its own belongs after `->`, where it says the routine sets {1}: to say the routine uses its "
            + "caller's {1}, write `reads {0}`",
        "A flag named on its own after `->` is a result the routine sets for its caller. Before `->` it would "
            + "say nothing a signature can check. A routine that uses the flag as its caller left it declares "
            + "that with `reads`, and one that needs the flag at a value gives it, as in `c = 0`.");

    internal static DiagnosticDescriptor FlagValueNotABit { get; } = Entry(
        Area.Signatures,
        "flag-value-not-a-bit",
        Severity.Error,
        "`{0}` gives a flag a value, which must be the constant 0 or 1",
        "A flag is one bit, so the value after its `=` is 0 or 1, and nt65 needs it while it builds to check "
            + "calls and returns against it.");

    internal static DiagnosticDescriptor KeepsAndExitFlag { get; } = Entry(
        Area.Signatures,
        "keeps-and-exit-flag",
        Severity.Error,
        "`keeps {0}` says {1} comes back as the caller left it, and `{2}` after `->` says otherwise: declare one "
            + "or the other",
        "`keeps` promises that a flag comes back unchanged, and an exit flag promises a value or a result the "
            + "routine sets. A flag cannot be both. Keep the item that describes what the routine does.");

    internal static DiagnosticDescriptor HandlerAssumesState { get; } = Entry(
        Area.Signatures,
        "handler-assumes-state",
        Severity.Error,
        "an interrupt handler cannot assume `{0}`, because it can interrupt any instruction: give only `native` or `emu`",
        "An interrupt can arrive between any two instructions, so a handler cannot assume anything about the "
            + "register widths, D or B; it has to set what it needs itself. Only the mode is known on entry, "
            + "because the processor takes native-mode and emulation-mode interrupts through different vectors. "
            + "Remove the item and set the state inside the handler.");

    internal static DiagnosticDescriptor HandlerDistance { get; } = Entry(
        Area.Signatures,
        "handler-distance",
        Severity.Error,
        "`{0}` describes how a routine is called, and an interrupt handler is never called: the processor enters "
            + "it and `rti` leaves it",
        "`near`, `far`, `inline` and `args` describe how a caller calls a routine and how it returns. An interrupt "
            + "handler has no caller: the processor enters it through a vector, and `rti` leaves it. Remove the "
            + "item.");

    internal static DiagnosticDescriptor HandlerNoreturn { get; } = Entry(
        Area.Signatures,
        "handler-noreturn",
        Severity.Error,
        "`{0}` does not apply to an interrupt handler, which leaves with `rti` and has no caller to return to",
        "`noreturn` means that a routine never comes back to its caller. An interrupt handler has no caller: the "
            + "processor enters it, and `rti` resumes the interrupted code. Remove `noreturn`.");

    internal static DiagnosticDescriptor HandlerKeeps { get; } = Entry(
        Area.Signatures,
        "handler-keeps",
        Severity.Error,
        "`{0}` does not apply to an interrupt handler: give the mode it is entered in, `native` or `emu`, or nothing",
        "A `*` item means that a routine hands a part of the state back to its caller as it found it. An interrupt "
            + "handler has no caller: the processor enters it and `rti` leaves it. Give the mode the handler is "
            + "entered in with `native` or `emu`, or leave the mode out.");

    internal static DiagnosticDescriptor HandlerDeclaresAnExit { get; } = Entry(
        Area.Signatures,
        "handler-declares-an-exit",
        Severity.Error,
        "an interrupt handler cannot declare a state after `->`: it leaves with `rti`, which restores the "
            + "interrupted state",
        "What follows `->` is the state a routine returns in, for its caller to rely on. An interrupt handler "
            + "leaves with `rti`, which restores the status register and return address the interrupt pushed, so "
            + "nothing reads the state the handler leaves. Remove the `->` and what follows it.");

    internal static DiagnosticDescriptor SignatureValueNotConstant { get; } = Entry(
        Area.Signatures,
        "signature-value-not-constant",
        Severity.Error,
        "`{0}` needs a constant value",
        "The analysis tracks the direct page register D and the data bank register B as exact values, and checks "
            + "every call against the values in the callee's signature. So `dp = ...` or `dbr = ...` in a "
            + "signature has to be a constant expression that nt65 can evaluate while it builds.");

    internal static DiagnosticDescriptor SignatureValueOutOfRange { get; } = Entry(
        Area.Signatures,
        "signature-value-out-of-range",
        Severity.Error,
        "`{0}` is out of range: {1}",
        "The direct page register D holds a 16-bit address, $0000 to $ffff, and the data bank register B holds one "
            + "byte, $00 to $ff.");

    internal static DiagnosticDescriptor StateBanksNotDbr { get; } = Entry(
        Area.Signatures,
        "state-banks-not-dbr",
        Severity.Error,
        "`{0}`: only `dbr` can be given a set of banks; {1}",
        "A routine that does not set B itself can run with any of several data banks that reach the same memory, "
            + "so `dbr = [...]` may list a set of banks. D decides where every direct-page operand lands, so `dp` "
            + "has to be a single value.");

    internal static DiagnosticDescriptor StateBanksInvalid { get; } = Entry(
        Area.Signatures,
        "state-banks-invalid",
        Severity.Error,
        "`{0}` is not a valid set of banks: each item is a constant bank, or a range given low to high, such as "
            + "`$00..$3f`",
        "A set of banks goes in square brackets, as in `dbr = [$00..$3f, $80..$bf]`. Each item is a constant "
            + "bank number or a range `low..high`, and the set names at least one bank.");

    internal static DiagnosticDescriptor SignatureSetNotFirst { get; } = Entry(
        Area.Signatures,
        "signature-set-not-first",
        Severity.Error,
        "signature set `{0}` must come first in its list: the items after it change what it gives",
        "A signature set supplies a group of items, and the items after it adjust or override them. So the "
            + "set comes first in its list, and the rest read as changes to it.");

    internal static DiagnosticDescriptor SignatureSetNotASet { get; } = Entry(
        Area.Signatures,
        "signature-set-not-a-set",
        Severity.Error,
        "`{0}` is {1}, not a signature set: a bare name among a signature's items has to name a `.signature` set",
        "A bare name among a signature's items refers to a set declared with `.signature`, whose items it brings "
            + "in. Anything else in a signature is given as an item, such as `a16` or `dbr = $7e`.");

    internal static DiagnosticDescriptor MacroKeeps { get; } = Entry(
        Area.Signatures,
        "macro-keeps",
        Severity.Error,
        "`{0}` does not apply to a macro: the routine it expands into declares that",
        "A macro's body is expanded into a routine, so what it saves, restores, uses and sets is part of what that "
            + "routine keeps, reads and returns with, and `keeps`, `reads` and the flags belong in the routine's "
            + "signature. Remove it from the macro's.");

    internal static DiagnosticDescriptor MacroNoreturn { get; } = Entry(
        Area.Signatures,
        "macro-noreturn",
        Severity.Error,
        "`noreturn` does not apply to a macro: a macro is expanded in place, not called",
        "A macro is expanded where it is used, and whatever follows the expansion belongs to the routine it is in. "
            + "`noreturn` describes routines that never return to a caller. Remove it from the macro's signature.");

    internal static DiagnosticDescriptor MacroDistance { get; } = Entry(
        Area.Signatures,
        "macro-distance",
        Severity.Error,
        "`{0}` does not apply to a macro: it describes how a routine is called, and a macro is expanded in place",
        "A macro is not called and does not return: its body is expanded where it is used. `near`, `far`, "
            + "`inline`, `args` and `interrupt` describe how a routine is called, entered or left, so they do not "
            + "apply to it. Remove the item.");

    internal static DiagnosticDescriptor DistanceDisagrees { get; } = Entry(
        Area.Signatures,
        "distance-disagrees",
        Severity.Error,
        "`{0}` contradicts the earlier `{1}`: a routine is either near or far",
        "A near routine is called with `jsr` and returns with `rts`; a far one is called with `jsl` and returns "
            + "with `rtl`. A routine is one or the other, so its signature declares `near` or `far` once. Remove one "
            + "of them.");

    internal static DiagnosticDescriptor SignatureItemTwice { get; } = Entry(
        Area.Signatures,
        "signature-item-twice",
        Severity.Error,
        "`{0}` and `{1}` both describe the same part of the state",
        "Each part of the processor state is declared once in a signature, so what it declares does not depend on the "
            + "order its items are read in. Keep one of the two items.");

    internal static DiagnosticDescriptor UnchangedNeedsEntry { get; } = Entry(
        Area.Signatures,
        "unchanged-needs-entry",
        Severity.Error,
        "`{0}` after `->` needs `{1}` before it too: a routine can promise to return a part unchanged only if it "
            + "assumes nothing about it on entry",
        "A `*` after the arrow promises that the routine returns that part of the state as its caller left it. "
            + "That promise only means something when the entry also declares `*`, accepting whatever the caller has. "
            + "If the entry gives a value, put that value after the arrow instead.");

    internal static DiagnosticDescriptor SavesInSignature { get; } = Entry(
        Area.Signatures,
        "saves-in-signature",
        Severity.Error,
        "`{0}` says what one store does, so it belongs in a `.state` directly under that store, not in a signature",
        "`saves x` marks the store on the line above it as a save, one that does not count as a use of X's value. "
            + "It describes one line, not a routine, so it is written in a `.state` under that line.");

    internal static DiagnosticDescriptor ItemBelongsAtEntry { get; } = Entry(
        Area.Signatures,
        "item-belongs-at-entry",
        Severity.Error,
        "`{0}` belongs before `->`",
        "What a routine is and how it is called are true of it from entry to exit, so they are declared once, "
            + "before the arrow. What comes after the arrow is what the routine leaves.");

    // Allowing warnings

    internal static DiagnosticDescriptor AllowUnused { get; } = Entry(
        Area.Allowing,
        "allow-unused",
        Severity.Warning,
        "`.allow \"{0}\"` hides nothing: {1} has no such warning",
        "An `.allow` that no longer hides a warning has outlived the code it was written for, and it would hide the "
            + "next such warning there without anyone deciding to. Remove it. An `.allow` in a branch the build "
            + "configuration leaves out, or in a macro body, is not reported, because another build or another call "
            + "may need it.");

    internal static DiagnosticDescriptor AllowError { get; } = Entry(
        Area.Allowing,
        "allow-error",
        Severity.Error,
        "`{0}` is an error, and `.allow` hides only warnings",
        "A warning points at something that may be intended, and `.allow` records that it is. An error means nt65 "
            + "cannot produce correct output for the program, and hiding it would not make the output right.");

    internal static DiagnosticDescriptor AllowAnswered { get; } = Entry(
        Area.Allowing,
        "allow-answered",
        Severity.Error,
        "`.allow` cannot hide `{0}`: add the annotation its message names, which tells nt65 what really happens",
        "Some diagnostics say that the analysis cannot see where flow goes or what an instruction becomes, and "
            + "their fix is an annotation such as `.next`, `.patch` or `.state`. Hiding one would hide the message "
            + "while the analysis went on following paths that are not there, so `.allow` refuses them.");

    internal static DiagnosticDescriptor AllowAboutNothing { get; } = Entry(
        Area.Allowing,
        "allow-about-nothing",
        Severity.Error,
        "`.allow` applies to the statement below it, and there is none",
        "An `.allow` goes directly before the statement it applies to, so that a reader sees it before the code it "
            + "excuses. Before a line that opens a block, such as a `.proc`, it covers the whole block. Here nothing "
            + "follows it in its block.");

    // Suggestions

    internal static DiagnosticDescriptor TailCall { get; } = Entry(
        Area.Suggestions,
        "tail-call",
        Severity.Info,
        "`{0}` then `{1}` can be `{2}`, which saves {3} cycles{4}",
        "A call followed at once by a return comes back only to leave again. A jump to the routine does the same "
            + "work, because the routine's own return then goes straight to this routine's caller. `jmp` in place of "
            + "`jsr` and `rts` saves 9 cycles, and `jml` in place of `jsl` and `rtl` saves 10. Where nothing else "
            + "reaches the return, it can go too, which saves a byte. The suggestion is not made for a routine that "
            + "reads what follows its call, takes arguments on the stack, or never returns.");

    internal static DiagnosticDescriptor ConstantUsedAsAddress { get; } = Entry(
        Area.Suggestions,
        "constant-used-as-address",
        Severity.Info,
        "`{0}` is used as an address: declare it as data, or with `.mmio` if it is a hardware register",
        "A constant is a number to nt65, even where an instruction reaches memory through it, so what is at that "
            + "address has no element type, size or fields, and the editor cannot follow values stored there. Declared "
            + "as data found elsewhere, `.data name: .byte = address`, the same name has all of those, and with "
            + "`.mmio` in place of `.data` it is a hardware register, whose value the hardware sets. The output is the "
            + "same. A constant that a branch, a jump or a call names directly is the address of code, and is not "
            + "suggested.");

    internal static DiagnosticDescriptor WidthAlreadySet { get; } = Entry(
        Area.Suggestions,
        "width-already-set",
        Severity.Info,
        "`{0}` {1}: {2}",
        "The processor-state analysis knows the widths on every path to this line, and the `rep` or `sep` sets "
            + "one or both to what they already are. One that changes nothing can go, which saves 2 bytes and 3 "
            + "cycles. One that changes only part of what it names can name less. Where the widths are only known "
            + "because the routine's signature or a `.state` declares them, that declaration is the promise the "
            + "line relies on.");

    internal static DiagnosticDescriptor NextProved { get; } = Entry(
        Area.Suggestions,
        "next-proved",
        Severity.Info,
        "the `.next` is not needed: `{0}` is always taken, because {1}",
        "nt65 follows the N, Z, C and V flags through each routine, from what the instructions themselves set, and "
            + "here it proves the branch is always taken. The branch is then a jump to its target already, so a "
            + "`.next` saying so adds nothing. Without it, nt65 checks the claim again whenever the code before the "
            + "branch changes.");

    internal static DiagnosticDescriptor BranchNeverTaken { get; } = Entry(
        Area.Suggestions,
        "branch-never-taken",
        Severity.Info,
        "`{0}` is never taken, because {1}",
        "nt65 follows the N, Z, C and V flags through each routine, from what the instructions themselves set. The "
            + "flag this branch tests has the value that does not branch on every path to it, so the branch only "
            + "takes 2 cycles. That is usually a sign that the code before it does not set the flag the branch was "
            + "meant to test. Where the branch is there on purpose, it can go.");

    internal static DiagnosticDescriptor JumpAsBranch { get; } = Entry(
        Area.Suggestions,
        "jump-as-branch",
        Severity.Info,
        "`{0}` can be `{1}`, which saves a byte{2}{3}",
        "A conditional branch whose flag is known always branches, and takes 2 bytes where `jmp` takes 3. Where the "
            + "target is within a branch's reach, the `jmp` can be that branch. It takes the same 3 cycles, or 4 "
            + "where it crosses a page, which matters in code timed to the cycle. The message says so unless the "
            + "65816 is known to be in native mode, where a branch never pays for a page. On the 65C02 and the "
            + "65816, `bra` always branches, and needs no known flag. "
            + "nt65 follows the flags from what the instructions themselves set, so the suggestion holds on any "
            + "system.");

    internal static DiagnosticDescriptor BranchOverJump { get; } = Entry(
        Area.Suggestions,
        "branch-over-jump",
        Severity.Info,
        "`{0}` over `{1}` can be the one branch `{2}`, which saves 3 bytes",
        "A conditional branch over a `jmp` makes the jump only where the branch is not taken. The opposite branch "
            + "to the jump's target does the same in one instruction, where the target is within a branch's reach. "
            + "It saves 3 bytes, and 1 or 2 cycles on each path. The label the branch went to goes too, where "
            + "nothing else names it.");

    internal static DiagnosticDescriptor CarryAlreadySet { get; } = Entry(
        Area.Suggestions,
        "carry-already-set",
        Severity.Info,
        "`{0}` changes nothing: C is already {1} here",
        "nt65 follows the carry through each routine, from what the instructions themselves set, such as a `clc`, "
            + "a `sec`, or a branch that tested it. Here C already has the value this instruction gives it on every "
            + "path, so the instruction can go, which saves a byte and 2 cycles.");

    internal static DiagnosticDescriptor CarryFolded { get; } = Entry(
        Area.Suggestions,
        "carry-folded",
        Severity.Info,
        "`{0}` then `{1}` can be `{2}`, because C is {3} here, which saves a byte and 2 cycles",
        "`adc` adds the carry, so where C is known to be 1, `adc #n-1` adds the same as `clc` then `adc #n`. "
            + "Likewise, where C is known to be 0, `sbc #n-1` subtracts the same as `sec` then `sbc #n`. The result "
            + "and C come out the same, and so does V unless n is 0 or $80, where n-1 has the other sign. Decimal "
            + "mode agrees unless n's low digit is 0. The suggestion is not made in those cases.");

    internal static DiagnosticDescriptor ZeroCompare { get; } = Entry(
        Area.Suggestions,
        "zero-compare",
        Severity.Info,
        "`{0}` changes nothing that is read: N and Z already reflect {1} here, and {2}",
        "A compare with zero sets N and Z from the register itself, and sets C to 1. Where the instruction before "
            + "already set N and Z from that register, as `lda`, `dex` or `and` do, the compare changes only C. It can "
            + "go where C is already 1, or where nothing reads C before it is written again, which saves 2 bytes and 2 "
            + "cycles. nt65 follows the registers and flags from what the instructions themselves do, so the "
            + "suggestion holds on any system.");

    internal static DiagnosticDescriptor LoadAlreadyHeld { get; } = Entry(
        Area.Suggestions,
        "load-already-held",
        Severity.Info,
        "`{0}` changes nothing that is read: {1} already holds {2} here, and {3}",
        "nt65 follows the constants A, X and Y hold through each routine, from immediate loads, transfers, "
            + "increments and the like, and from a branch that found Z set after a register was loaded or counted. "
            + "Here the register already holds the constant the load gives it on every path. The load can go where N "
            + "and Z already say what it would set them to, or where nothing reads them before they change, which "
            + "saves 2 bytes and 2 cycles. Memory is never assumed to hold anything.");

    internal static DiagnosticDescriptor ExportStateInferred { get; } = Entry(
        Area.Suggestions,
        "export-state-inferred",
        Severity.Info,
        "`{0}` is exported, and its bytes depend on {1}, which is inferred from its callers here: a caller outside "
            + "nt65 is not checked against it, so the signature can declare it",
        "nt65 infers each part of a routine's entry that its signature leaves out from the routine's callers in the "
            + "program, and sizes immediates and writes `d:` operands from what it infers. A caller outside nt65 is "
            + "not checked, so for an exported routine whose bytes depend on an inferred width, mode or direct page, "
            + "nothing states what such a caller must hold. Declaring the items makes that the contract, which every "
            + "caller in the program is then checked against. This is offered only where a byte depends on the part.");

    internal static DiagnosticDescriptor LoadFromRegister { get; } = Entry(
        Area.Suggestions,
        "load-from-register",
        Severity.Info,
        "`{0}` can be `{1}`, because {2}, which saves a byte",
        "An immediate load takes 2 bytes. Where another register already holds the constant, a transfer such as "
            + "`tax` gives the same value in 1 byte, and where the register holds one more or one less, `dex` or `inx` "
            + "does. Each sets N and Z from the result as the load does, takes the same 2 cycles, and leaves C and V "
            + "alone. The 65816 is left out, because there the registers' widths decide what a transfer copies.");

    // The list is found by reflecting over the class rather than listed by hand, so that a new
    // entry above is included automatically. It is built on first use rather than alongside the
    // entries, because reflecting on a type while its own static initializer is still running can
    // deadlock two threads that ask for it at the same time.
    private static readonly Lazy<IReadOnlyList<DiagnosticDescriptor>> all = new(() =>
        [.. typeof(Catalogue).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DiagnosticDescriptor))
            .Select(property => (DiagnosticDescriptor)property.GetValue(null)!)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)]);

    // Find is called for every name in a project file and on every nt65 explain, so the names are
    // looked up in a dictionary. It is built from All, on first use, for the same reason All is.
    private static readonly Lazy<FrozenDictionary<string, DiagnosticDescriptor>> byId = new(() =>
        All.ToFrozenDictionary(descriptor => descriptor.Id, StringComparer.Ordinal));

    // Built on first use, like All, so that every entry exists before the set is made.
    private static readonly Lazy<FrozenSet<DiagnosticDescriptor>> answered = new(() => new[]
    {
        RunsIntoData, IndirectJumpUnchecked, ComputedJumpUnchecked, SelfModifyingUnchecked, CodeLabelAsData,
    }.ToFrozenSet());

    /// <summary>
    /// Gets the entries that <c>.allow</c> refuses to hide. The fix for each is an annotation that
    /// tells nt65 what really happens, and hiding one would leave the analysis following paths that
    /// are not there.
    /// </summary>
    internal static IReadOnlySet<DiagnosticDescriptor> AnsweredByAnnotations => answered.Value;

    /// <summary>
    /// Returns a value indicating whether <c>.allow</c> may hide the diagnostic named
    /// <paramref name="id"/>. It may hide a warning that no annotation answers.
    /// </summary>
    public static bool IsAllowable(string id) =>
        Find(id) is { Severity: Severity.Warning } descriptor && !AnsweredByAnnotations.Contains(descriptor);

    /// <summary>Gets every descriptor, in name order.</summary>
    public static IReadOnlyList<DiagnosticDescriptor> All => all.Value;

    /// <summary>
    /// Returns the descriptor named <paramref name="id"/>, or null when no diagnostic has that name.
    /// </summary>
    public static DiagnosticDescriptor? Find(string id) => byId.Value.GetValueOrDefault(id);

    /// <summary>Creates one entry in the given area.</summary>
    private static DiagnosticDescriptor Entry(
        DiagnosticArea area, string id, Severity severity, string format, string explanation) =>
        new(id, area, severity, format, explanation);

    /// <summary>
    /// Holds the areas. Each entry names its area, so an entry keeps its area wherever it is
    /// declared. The areas live in their own class so that they are created before any entry
    /// asks for one, whatever order the entries are declared in.
    /// </summary>
    private static class Area
    {
        public static DiagnosticArea ReadingALine { get; } =
            new("Reading a line", "Syntax: numbers, text, braces, and what may appear where on a line.");

        public static DiagnosticArea Names { get; } =
            new("Names", "Declarations, scopes, modules and what a path reaches.");

        public static DiagnosticArea Values { get; } =
            new("Values", "Constants, expressions, built-in functions and the build configuration.");

        public static DiagnosticArea Macros { get; } =
            new("Macros", "Macro declarations, calls, arguments and expansion.");

        public static DiagnosticArea Data { get; } = new("Data", "Declarations, records, arrays, text and padding.");

        public static DiagnosticArea Placement { get; } =
            new("Placement", "Segments, where a declaration sits and how wide an address is.");

        public static DiagnosticArea Instructions { get; } =
            new("Instructions", "Mnemonics, operands, addressing modes and branch range.");

        public static DiagnosticArea ControlFlow { get; } =
            new("Control flow", "Where execution goes, and the annotations the analysis needs where it cannot see.");

        public static DiagnosticArea ProcessorState { get; } = new(
            "Processor state",
            "65816 register widths, emulation mode, direct page, data bank, stack frames, calls and returns.");

        public static DiagnosticArea Output { get; } =
            new("Output", "Names that clash in the ca65 output, and the C header.");

        public static DiagnosticArea TheProjectFile { get; } =
            new("The project file", "`nt65.json` and the command line that adds to it.");

        public static DiagnosticArea Signatures { get; } =
            new("Signatures", "What a routine or macro signature may declare, and what a signature set may hold.");

        public static DiagnosticArea Allowing { get; } =
            new("Allowing warnings", "`.allow`, which keeps a warning from being reported where code knowingly relies on it.");

        public static DiagnosticArea Suggestions { get; } =
            new("Suggestions", "Changes that make code smaller, faster or clearer about what it means, which only the editor shows.");
    }
}
