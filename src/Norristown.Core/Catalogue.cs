using System.Reflection;

namespace Norristown;

/// <summary>
/// Every diagnostic nt65 reports, by name. A name says what is wrong rather than which
/// pass found it, is kebab-case, and is stable once released: it is what a project file
/// switches, what the editor shows beside the message, and what CI matches on.
/// <para>
/// A reporting site names one of these and hands it the pieces of its sentence. The
/// explanation is not the message: it is what the one line has no room for, and is what
/// <c>nt65 explain</c> prints.
/// </para>
/// </summary>
public static class Catalogue
{
    // Reading a line: what a line is made of, and what is written where.

    internal static DiagnosticDescriptor NumberInvalid { get; } = new(
        "number-invalid",
        Severity.Error,
        "invalid {0} number `{1}`",
        "A number runs to the end of the word, so a digit that does not belong to the base is part of the number "
            + "rather than the start of something else.");

    internal static DiagnosticDescriptor NumberSeparator { get; } = new(
        "number-separator",
        Severity.Error,
        "`{0}` has a `_` that separates no digits: a separator stands between two of them",
        "A `_` in a number is there to group its digits, in any base, and groups nothing at either end of the "
            + "number or beside another `_`.");

    internal static DiagnosticDescriptor DigitsMissing { get; } = new(
        "digits-missing",
        Severity.Error,
        "expected {0} digits after `{1}`{2}",
        "A `$` or a `%` says which base the digits after it are in, and there are none.");

    internal static DiagnosticDescriptor NameAfterAt { get; } = new(
        "name-after-at",
        Severity.Error,
        "expected a name after `@`",
        "A `@` begins a cheap local, which is a name private to the routine around it.");

    internal static DiagnosticDescriptor StrayDot { get; } = new(
        "stray-dot",
        Severity.Error,
        "unexpected `.`",
        "A `.` begins a directive or a built-in function, and a `..` a range. On its own it is neither.");

    internal static DiagnosticDescriptor UnexpectedCharacter { get; } = new(
        "unexpected-character",
        Severity.Error,
        "unexpected character `{0}`",
        "The character begins nothing the language has. Text and comments may hold anything; nothing else may.");

    internal static DiagnosticDescriptor EscapeHexDigits { get; } = new(
        "escape-hex-digits",
        Severity.Error,
        "`\\x` must be followed by two hexadecimal digits",
        "A `\\xHH` escape writes one byte, and writes it in full, so that the text reads as the bytes it is.");

    internal static DiagnosticDescriptor EscapeUnknown { get; } = new(
        "escape-unknown",
        Severity.Error,
        "unknown escape `\\{0}`",
        "The escapes are a fixed set, the same in a character literal and in a string. Every escape a literal "
            + "cannot read is reported, since each is a separate thing to correct.");

    internal static DiagnosticDescriptor TextUnterminated { get; } = new(
        "text-unterminated",
        Severity.Error,
        "unterminated {0}",
        "A literal is closed on the line it opens: nothing in nt65 runs past the end of a line.");

    internal static DiagnosticDescriptor CharacterEmpty { get; } = new(
        "character-empty",
        Severity.Error,
        "empty character literal",
        "A character literal is one character, which is one value. Text is written in double quotes.");

    internal static DiagnosticDescriptor CharacterTooLong { get; } = new(
        "character-too-long",
        Severity.Error,
        "a character literal holds exactly one character",
        "A character literal is one value. Several characters are text, written in double quotes, and a data "
            + "declaration writes their bytes.");

    internal static DiagnosticDescriptor BlockNotClosed { get; } = new(
        "block-not-closed",
        Severity.Error,
        "missing `}}` to close this block",
        "Blocks are read from the braces before anything else in a file is read, so an unclosed one is reported "
            + "at the line that opens it rather than at the end of the file.");

    internal static DiagnosticDescriptor UnmatchedBrace { get; } = new(
        "unmatched-brace",
        Severity.Error,
        "unmatched `}}`",
        "A `}` closes a block, and there is no open one here.");

    internal static DiagnosticDescriptor UnexpectedToken { get; } = new(
        "unexpected-token",
        Severity.Error,
        "unexpected {0}",
        "The line was read as far as it goes, and there is more written after it. Only the first thing wrong with "
            + "a line is reported, so the tokens walked past afterwards are the same news.");

    internal static DiagnosticDescriptor BlockBraceEndsTheLine { get; } = new(
        "block-brace-ends-the-line",
        Severity.Error,
        "a block's `{{` ends the line that opens it: {0} goes on the next line, and `}}` on its own",
        "A block is written on more than one line, so that the shape of a file can be read from its indentation "
            + "and its braces alone.");

    internal static DiagnosticDescriptor Ca65Spelling { get; } = new(
        "ca65-spelling",
        Severity.Error,
        "`{0}` is written `{1}`",
        "nt65 has the directive under another name. The fix writes the nt65 spelling.");

    internal static DiagnosticDescriptor Ca65Tag { get; } = new(
        "ca65-tag",
        Severity.Error,
        "`.tag T` is written `.type T`, and `.tag T, n` is `.type T[n]`",
        "A record type is written the same way wherever it is used, and a count is written in brackets after it, "
            + "as every other count is.");

    internal static DiagnosticDescriptor Ca65BlockEnd { get; } = new(
        "ca65-block-end",
        Severity.Error,
        "a block ends with `}}`, and `{0}` closes nothing",
        "Braces rather than end-keywords, so that a block's shape is readable and an nt65 file looks nothing "
            + "like a ca65 one.");

    internal static DiagnosticDescriptor DataNeedsAName { get; } = new(
        "data-needs-a-name",
        Severity.Error,
        "`.data` declares data, and needs a name: the segment is written `.segment DATA`",
        "`.data` is a declaration, and a declaration has a name. The segment called DATA is named on a "
            + "`.segment` line.");

    internal static DiagnosticDescriptor DataValuesNeedBraces { get; } = new(
        "data-values-need-braces",
        Severity.Error,
        "{0}",
        "An array's values and a record's values are written in braces, so that where one declaration's values "
            + "end is written down rather than read off the count.");

    internal static DiagnosticDescriptor ExpectedName { get; } = new(
        "expected-name",
        Severity.Error,
        "expected {0}",
        "A declaration, a parameter, a path or a `.use` item has a place for a name here and the line does not "
            + "write one. The message says which name is wanted; nt65 never invents one, so the line is read no further "
            + "than the piece that is missing.");

    internal static DiagnosticDescriptor ExpectedStatement { get; } = new(
        "expected-statement",
        Severity.Error,
        "expected {0}",
        "A line is a label, a constant, an instruction, a directive or a macro call, and this one begins as none "
            + "of them. What follows a label is narrower still: only what takes bytes.");

    internal static DiagnosticDescriptor ExpectedExpression { get; } = new(
        "expected-expression",
        Severity.Error,
        "expected an expression",
        "An expression belongs here and the line has something that starts none: a stray operator, a closing "
            + "bracket, or nothing at all.");

    internal static DiagnosticDescriptor ExpectedElementIndex { get; } = new(
        "expected-element-index",
        Severity.Error,
        "expected the element: `name[i]` is the i-th of what `name` declares",
        "The brackets after a name pick one element of what it declares, and the brackets here are empty. A count "
            + "is written on the declaration; an index is written on a use of it.");

    internal static DiagnosticDescriptor ExpectedParenthesis { get; } = new(
        "expected-parenthesis",
        Severity.Error,
        "expected {0}",
        "A parameter list, an argument list or a parenthesised expression is unbalanced or unopened.");

    internal static DiagnosticDescriptor ExpectedBrace { get; } = new(
        "expected-brace",
        Severity.Error,
        "expected {0}",
        "A block opens with `{` at the end of the line that opens it and closes with `}` on a line of its own, "
            + "and one of the two is not written.");

    internal static DiagnosticDescriptor ExpectedBracket { get; } = new(
        "expected-bracket",
        Severity.Error,
        "expected {0}",
        "A count, an index, a long indirect operand or a list of banks is written in brackets, and one of them is "
            + "not closed or not opened.");

    internal static DiagnosticDescriptor ExpectedEquals { get; } = new(
        "expected-equals",
        Severity.Error,
        "expected {0}",
        "A constant, a member value, a setting, a default or a `.func` body is written after an `=`, and the line "
            + "stops before it.");

    internal static DiagnosticDescriptor ExpectedColon { get; } = new(
        "expected-colon",
        Severity.Error,
        "expected {0}",
        "A `:` separates a declaration from what it is: a segment from its address size, data from its element "
            + "type, a frame from the record it is laid out as.");

    internal static DiagnosticDescriptor ExpectedComma { get; } = new(
        "expected-comma",
        Severity.Error,
        "expected {0}",
        "A `.multiproc` names the enum it walks and then the name each routine is named from, with a comma "
            + "between them.");

    internal static DiagnosticDescriptor ExpectedText { get; } = new(
        "expected-text",
        Severity.Error,
        "expected {0}",
        "A message, or a linker name, is written in double quotes. nt65 has no bare-word text.");

    internal static DiagnosticDescriptor ExpectedDataType { get; } = new(
        "expected-data-type",
        Severity.Error,
        "expected {0}",
        "A `.data` declaration says what its bytes are — a number type, an address type, a record type or an "
            + "included file — and this one says nothing the language reads as one.");

    internal static DiagnosticDescriptor ExpectedMemberValue { get; } = new(
        "expected-member-value",
        Severity.Error,
        "expected `member = value`",
        "Each line of a multi-line record initializer gives one member a value.");

    internal static DiagnosticDescriptor ExpectedCpu { get; } = new(
        "expected-cpu",
        Severity.Error,
        "expected {0}",
        "`.cpu` names one of the processors nt65 knows, written as the project file writes it.");

    internal static DiagnosticDescriptor ExpectedAddressSize { get; } = new(
        "expected-address-size",
        Severity.Error,
        "expected {0}",
        "How wide an address is has three spellings and no others: `zp`, `abs` and `far`. An import may also give "
            + "a routine signature or an element type in place of a size.");

    internal static DiagnosticDescriptor ImportNeedsAnElementType { get; } = new(
        "import-needs-an-element-type",
        Severity.Error,
        "`{0}` is not an element type: an import says what its bytes are as `.byte`, `.word`, `.addr` or `.type T`",
        "A typed import describes storage another object holds, so it writes an element type and a count. A "
            + "directive that reads a file or writes text describes bytes this program would emit, and an import "
            + "emits none.");

    internal static DiagnosticDescriptor ImportHoldsNoValues { get; } = new(
        "import-holds-no-values",
        Severity.Error,
        "an import of `{0}` says what its bytes are and holds none of them: the definition is in another object",
        "An import declares what a symbol another object defines looks like, so that nt65 can size it and reach "
            + "its members. The bytes themselves belong to whoever defines it.");

    internal static DiagnosticDescriptor ExpectedSegmentAttribute { get; } = new(
        "expected-segment-attribute",
        Severity.Error,
        "expected {0}",
        "What a segment declaration may say beyond its address size is fixed: the direct page it is reached "
            + "through, the bank it sits in, and the banks it is mirrored in.");

    internal static DiagnosticDescriptor ExpectedParameterKind { get; } = new(
        "expected-parameter-kind",
        Severity.Error,
        "expected {0}",
        "A macro parameter says what it takes, and the words it may say are fixed. A parameter that says nothing "
            + "takes an expression.");

    internal static DiagnosticDescriptor ExpectedStateItem { get; } = new(
        "expected-state-item",
        Severity.Error,
        "expected {0}",
        "A signature, a `.state` and an `.ensure` are made of processor-state items, and this is not one.");

    internal static DiagnosticDescriptor ExpectedKeptRegisters { get; } = new(
        "expected-kept-registers",
        Severity.Error,
        "expected {0}",
        "A `keeps` names the registers a routine hands back as it was entered with them, and names at least one.");

    internal static DiagnosticDescriptor ExpectedLabel { get; } = new(
        "expected-label",
        Severity.Error,
        "expected {0}",
        "A `.next` names where flow goes and a `.patch` names the instruction being written into; each takes a "
            + "label, and `.next` also takes `?` for a path that ends.");

    internal static DiagnosticDescriptor NestingTooDeep { get; } = new(
        "nesting-too-deep",
        Severity.Error,
        "this nests more than {0} expressions deep, which is as far as nt65 reads",
        "An expression nested past the limit is read no further. The limit is far beyond anything written by "
            + "hand, and is there so that a half-typed line of brackets cannot take the process down with it.");

    internal static DiagnosticDescriptor UnnamedLabel { get; } = new(
        "unnamed-label",
        Severity.Error,
        "an unnamed label is written `@name`: a cheap local, private to the routine around it",
        "ca65's `:`, `:+` and `:-` are positional, which §3 refuses. A label private to the routine around it is "
            + "written `@name`, and costs the same.");

    internal static DiagnosticDescriptor DirectiveUnknown { get; } = new(
        "directive-unknown",
        Severity.Error,
        "unknown directive `{0}`",
        "The word begins with a `.` and is no directive nt65 has. Directives are a fixed set: nothing declares "
            + "one.");

    internal static DiagnosticDescriptor DirectiveAfterLabel { get; } = new(
        "directive-after-label",
        Severity.Error,
        "`{0}` may not follow a label",
        "A label is a position in code, so what may follow it on the line is what takes bytes there: an "
            + "instruction, a data directive or a macro call.");

    internal static DiagnosticDescriptor ElseIfMisplaced { get; } = new(
        "elseif-misplaced",
        Severity.Error,
        "`{0}` continues an `.if`, and belongs after its `}}`",
        "A branch is continued on the line that closes the one before it, `} .else {`, so that the shape of the "
            + "block is readable without matching braces by eye.");

    internal static DiagnosticDescriptor AssertLevel { get; } = new(
        "assert-level",
        Severity.Error,
        "`{0}` is ca65's: an nt65 assertion that fails is always an error, checked as soon as nt65 can and otherwise at link time, so `.assert` takes only the condition and the message",
        "ca65's `.assert` takes a level because ca65 cannot always decide. nt65 can, and an assertion that fails "
            + "is an error: it is checked as soon as the value is known, and otherwise left for the linker to check.");

    internal static DiagnosticDescriptor SegmentNameQuoted { get; } = new(
        "segment-name-quoted",
        Severity.Error,
        "a segment name is written without quotes: `.segment {0}`",
        "Segments are a table of their own and share no namespace with symbols, so a segment name is written as a "
            + "word. The quotes are ca65's habit.");

    internal static DiagnosticDescriptor ModuleNameQuoted { get; } = new(
        "module-name-quoted",
        Severity.Error,
        "a module name is written without quotes: `.module hw::vic`",
        "A module name is a path of words, and is written as one.");

    internal static DiagnosticDescriptor ExportDeclaresNothing { get; } = new(
        "export-declares-nothing",
        Severity.Error,
        "`.export` goes before a declaration, and `{0}` declares nothing to export",
        "`.export` before a declaration exports what that declaration declares, so the directive after it has to "
            + "be one that declares something.");

    internal static DiagnosticDescriptor DataBodyHoldsValues { get; } = new(
        "data-body-holds-values",
        Severity.Error,
        "a data body holds values, and `{0}` is a directive: what the values are is the declaration's to say",
        "What the values of a declaration are is the declaration line's to say. Its body holds only the values, "
            + "so a directive in it would be saying it twice.");

    internal static DiagnosticDescriptor DataBodyNeedsACount { get; } = new(
        "data-body-needs-a-count",
        Severity.Error,
        "values in a body need a count: `{0}[] {{` counts them",
        "A body of values belongs to an array, and an array says how many elements it holds. `[]` counts the "
            + "values given, which is what a body without a count usually meant.");

    internal static DiagnosticDescriptor StateItemUnknown { get; } = new(
        "state-item-unknown",
        Severity.Error,
        "`{0}` is not a processor-state item",
        "The items a signature, a `.state` or an `.ensure` may hold are a fixed set, and this word is not one of "
            + "them. A word that is no item at all names a signature set instead.");

    internal static DiagnosticDescriptor OperatorsNeedParentheses { get; } = new(
        "operators-need-parentheses",
        Severity.Error,
        "`{0}` and `{1}` need parentheses to show which applies first",
        "nt65 gives every operator a precedence, but refuses the three pairings a reader misjudges: a shift or "
            + "bitwise operator beside a different operator, and logical operators mixed. Parentheses say which was "
            + "meant.");

    internal static DiagnosticDescriptor ByteOperatorNeedsParentheses { get; } = new(
        "byte-operator-needs-parentheses",
        Severity.Error,
        "unary `{0}` before `{1}` needs parentheses to show what `{2}` applies to",
        "The byte operators bind tighter than anything, so `<label + 1` is `(<label) + 1`. Where that is not "
            + "obviously what was meant, nt65 asks for the parentheses rather than guessing.");

    internal static DiagnosticDescriptor NotAFunction { get; } = new(
        "not-a-function",
        Severity.Error,
        "`{0}` is not a function",
        "The built-in functions are a fixed set. A function the program declares is a `.func` and is written "
            + "without the leading `.`.");

    // Names: declarations, scopes, modules and what a path reaches.

    internal static DiagnosticDescriptor NotDeclared { get; } = new(
        "not-declared",
        Severity.Error,
        "`{0}` is not declared{1}",
        "Nothing in scope here declares the name. Where a name one letter away is declared, or another module "
            + "exports it, the message says so, because that is nearly always what was meant.");

    internal static DiagnosticDescriptor NotDeclaredIn { get; } = new(
        "not-declared-in",
        Severity.Error,
        "`{0}` is not declared in {1}{2}",
        "The path names a scope, a module, a type or a routine that does not declare this last part. What a scope "
            + "holds is a set, and it is read from the declaration rather than from what happens to be written before "
            + "this line.");

    internal static DiagnosticDescriptor NotExported { get; } = new(
        "not-exported",
        Severity.Error,
        "`{0}` is not exported by module `{1}`",
        "A module names what other modules may see, and this is not among it. The fix is an `.export` on the "
            + "declaration in the module that owns it.");

    internal static DiagnosticDescriptor DeclaredInAnotherModule { get; } = new(
        "declared-in-another-module",
        Severity.Error,
        "`{0}` is not declared here, and module `{1}` exports it: write `{2}::{3}`, or bring it in with `.use {4}::{5}`",
        "The name is not in scope in this file, and exactly one module in the program exports it, which is almost "
            + "always the one meant.");

    internal static DiagnosticDescriptor ModuleNotInTheBuild { get; } = new(
        "module-not-in-the-build",
        Severity.Error,
        "`{0}` is not declared, and no module `{1}` is in this build",
        "The first part of a path names a module or something in scope, and is neither. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ModuleUnknown { get; } = new(
        "module-unknown",
        Severity.Error,
        "no module `{0}` is in this build",
        "A path written from the root of the modules names a module the program has. A module the project's "
            + "`files` do not name is not part of the program.");

    internal static DiagnosticDescriptor ExportAmbiguous { get; } = new(
        "export-ambiguous",
        Severity.Error,
        "`{0}` is exported by both `{1}` and `{2}`, and a `.use` brings in everything each exports: "
            + "`{3}::{4}` says which",
        "Two modules brought in with `.use` export the same name, so which one is meant would depend on the order "
            + "the `.use` lines are read in. Writing the path says which.");

    internal static DiagnosticDescriptor NotAScope { get; } = new(
        "not-a-scope",
        Severity.Error,
        "`{0}` is {1}, not a scope",
        "A `::` reaches into a module, a scope, a routine or a named type. The symbol before it is none of those, "
            + "so nothing can be reached through it.");

    internal static DiagnosticDescriptor NameAlreadyDeclared { get; } = new(
        "name-already-declared",
        Severity.Error,
        "`{0}` is already declared in this scope",
        "Declarations are a set, and a scope declares each name once. Where the other declaration is in this file "
            + "it is shown beside this one.");

    internal static DiagnosticDescriptor IdentParameterDeclared { get; } = new(
        "ident-parameter-declared",
        Severity.Error,
        "`{0}` is an `ident` parameter, and a body may not declare the name it stands for",
        "An `ident` parameter stands for a name the caller gives, so a body that declared that name would decide "
            + "for the caller what its argument means.");

    internal static DiagnosticDescriptor RegisterName { get; } = new(
        "register-name",
        Severity.Error,
        "`{0}` is a register name and cannot be used as a name",
        "The register names are reserved everywhere, because position cannot tell them apart from a symbol: `asl "
            + "a` is a question about an operand. No mnemonic is reserved; the registers are.");

    internal static DiagnosticDescriptor MnemonicName { get; } = new(
        "mnemonic-name",
        Severity.Warning,
        "`{0}` is an instruction on the {1}; as a name it is legal and easy to misread",
        "No mnemonic is reserved: a line that starts with one is an instruction unless a `:` or an `=` follows "
            + "the first word, which is what makes this a declaration. ca65 cannot define a bare symbol spelled like an "
            + "instruction, so nt65 writes a top-level one with its module in front, `main__lda`, exactly as it already "
            + "writes an exported name. The program is right and so is the output; what is left is that a reader meets "
            + "a word they know as an instruction. A team that wants the old strictness writes `\"mnemonic-name\": "
            + "\"error\"` under `diagnostics` in nt65.json.");

    internal static DiagnosticDescriptor CheapLocalOutsideAScope { get; } = new(
        "cheap-local-outside-a-scope",
        Severity.Error,
        "`{0}` is a cheap local, which needs an enclosing `.proc` or `.scope`",
        "A `@name` is private to the routine or scope around it, so there has to be one for it to be private to.");

    internal static DiagnosticDescriptor CheapLocalInAPath { get; } = new(
        "cheap-local-in-a-path",
        Severity.Error,
        "`{0}` is a cheap local and cannot be reached with `::`",
        "A cheap local is reachable only from inside the routine that declares it. That is the whole of what "
            + "makes it cheap.");

    internal static DiagnosticDescriptor ModuleUsedAsAName { get; } = new(
        "module-used-as-a-name",
        Severity.Error,
        "`{0}` is a module: a name in it is written `{1}::name`",
        "A module is not a value and has no address. What is wanted is a name inside it.");

    internal static DiagnosticDescriptor NameAloneOnALine { get; } = new(
        "name-alone-on-a-line",
        Severity.Error,
        "`{0}` is {1}; a name written on its own splices a `block` parameter, and nothing else belongs on a line alone",
        "A name written on a line by itself splices a `block` parameter into a macro body. Everything else takes "
            + "an operand, a `=`, a `:` or a `!`.");

    internal static DiagnosticDescriptor NotAMacro { get; } = new(
        "not-a-macro",
        Severity.Error,
        "`{0}` is {1}, and `!` calls a macro",
        "`!` calls a macro, and marks the line as one whose meaning is somewhere else. Nothing else is called "
            + "that way.");

    internal static DiagnosticDescriptor DeclarationInABlockArgument { get; } = new(
        "declaration-in-a-block-argument",
        Severity.Error,
        "`{0}` is declared in a block argument, which may declare only cheap locals: the macro it is given to may splice it in more than one place",
        "A block argument may be spliced into a macro body in more than one place, and a declaration in it would "
            + "then be declared more than once. A cheap local is renamed per expansion, so it is safe.");

    internal static DiagnosticDescriptor ModuleMissing { get; } = new(
        "module-missing",
        Severity.Error,
        "a file is a module, and says which first: `.module name`",
        "A file is one module, and says which before anything else, so that what it declares has a path from the "
            + "first line.");

    internal static DiagnosticDescriptor MultiprocMisplaced { get; } = new(
        "multiproc-misplaced",
        Severity.Error,
        "`.multiproc` declares routines, and this one is inside {0}: a routine belongs at file level or in a `.scope`",
        "A family declares one routine per member of an enum, and routines are declared where routines belong.");

    internal static DiagnosticDescriptor ModuleDeclaredTwice { get; } = new(
        "module-declared-twice",
        Severity.Error,
        "a file is one module, and names it once",
        "A file is one module. A large module is split into submodules, each its own file.");

    internal static DiagnosticDescriptor ModuleNotFirst { get; } = new(
        "module-not-first",
        Severity.Error,
        "`.module` comes first: the file's other items belong to the module it names",
        "Everything a file declares belongs to the module it names, so the name comes before the declarations.");

    internal static DiagnosticDescriptor ModuleNameTaken { get; } = new(
        "module-name-taken",
        Severity.Error,
        "module `{0}` is already `{1}`: a module is one file, and a large one is split into submodules",
        "A module name is the path everything in it is reached by, and the name its output file is named after, "
            + "so two files may not share one.");

    internal static DiagnosticDescriptor ModuleNamesDifferInCase { get; } = new(
        "module-names-differ-in-case",
        Severity.Error,
        "modules `{0}` and `{1}` differ only in case, and a file system that ignores case writes both to one file",
        "Output is named after the module, and on a file system that ignores case two such names are one file. "
            + "The build would depend on which ran last.");

    internal static DiagnosticDescriptor NameIsAModulePath { get; } = new(
        "name-is-a-module-path",
        Severity.Error,
        "`{0}` is the path of a module, and module `{1}` may not declare `{2}` as well",
        "A path reaches either a module or a name in one, so a module may not declare a name that is already the "
            + "start of another module's path.");

    internal static DiagnosticDescriptor UseMisplaced { get; } = new(
        "use-misplaced",
        Severity.Error,
        "`.use` belongs at the top level of a module",
        "What a module brings in is part of its interface, so a `.use` belongs where the interface is: at the top "
            + "level, not under a block.");

    internal static DiagnosticDescriptor UseBringsInTwice { get; } = new(
        "use-brings-in-twice",
        Severity.Error,
        "a `.use` already brings in `{0}`",
        "Two `.use` items that bring in the same name would leave which one meant undecided. `as` gives one of "
            + "them a name of its own.");

    internal static DiagnosticDescriptor UseCollidesWithDeclaration { get; } = new(
        "use-collides-with-declaration",
        Severity.Error,
        "`{0}` is declared in this module, and a `.use` may not bring in another: `as` brings it in under a name of its own",
        "A name brought in stands beside the names a file declares, and one name means one thing.");

    internal static DiagnosticDescriptor UseStarNotAModule { get; } = new(
        "use-star-not-a-module",
        Severity.Error,
        "`.use {0}::*` brings in what a module exports, and `{1}` is {2}",
        "`::*` brings in everything a module exports, so what is before it has to be a whole module name.");

    internal static DiagnosticDescriptor ReexportStar { get; } = new(
        "reexport-star",
        Severity.Error,
        "`.export .use` names what it re-exports: a `*` would make everything the other module exports, now and later, part of this one",
        "A `.export .use` makes another module's names part of this module's interface. A `*` would make that "
            + "interface whatever the other module exports later, which is not a promise this module can keep.");

    internal static DiagnosticDescriptor ReexportModule { get; } = new(
        "reexport-module",
        Severity.Error,
        "`{0}` is a module, and `.export .use` re-exports a name in one",
        "A module is not a name a path can end at, so there is nothing to re-export.");

    internal static DiagnosticDescriptor ReexportNeeded { get; } = new(
        "reexport-needed",
        Severity.Error,
        "`{0}` is declared in module `{1}`, and a module exports what it declares: `.export .use {2}` makes it part of this one",
        "A name another module declares is not part of this one just because this one brought it in. `.export "
            + ".use` says that it is.");

    internal static DiagnosticDescriptor LabelOutsideARoutine { get; } = new(
        "label-outside-a-routine",
        Severity.Error,
        "`{0}` is a label outside a `.proc`: a label is only a position in code{1}",
        "A label is a position in code, and code is inside a `.proc`. Data is named by the declaration that holds "
            + "it.");

    internal static DiagnosticDescriptor LabelInData { get; } = new(
        "label-in-data",
        Severity.Error,
        "`{0}` is a label in `.data`: a named member is `.data {1}: ...`, and a position is `@{2}:`",
        "Mixed data names its members with `.data`, and marks a position in it with a cheap local. A bare label "
            + "would be neither.");

    internal static DiagnosticDescriptor SegmentRegionMisplaced { get; } = new(
        "segment-region-misplaced",
        Severity.Error,
        "a `.segment NAME` region belongs at file level, outside every block: inside one, `.segment NAME {{ }}` places what it holds",
        "A `.segment NAME` region says where everything after it in the file goes, which only makes sense at file "
            + "level. Inside a block, the block form places what it holds.");

    internal static DiagnosticDescriptor SignatureSetNameIsAnItem { get; } = new(
        "signature-set-name-is-an-item",
        Severity.Error,
        "`{0}` is a signature item, and cannot name a signature set",
        "A signature set stands for the items it names, so its own name has to be one a signature would not read "
            + "as an item.");

    internal static DiagnosticDescriptor SignatureItemNeeds65816 { get; } = new(
        "signature-item-needs-65816",
        Severity.Error,
        "`{0}` cannot hold on the {1}, whose registers are eight bits",
        "Widths, the emulation flag, the direct page and the data bank are the 65816's. Earlier processors have "
            + "eight-bit registers and none of that state.");

    internal static DiagnosticDescriptor FamilyMisplaced { get; } = new(
        "family-misplaced",
        Severity.Error,
        "{0}",
        "A family declares one name per member of an enum into the scope around its `.each`, so it stands where "
            + "those declarations belong and over something whose members have names.");

    internal static DiagnosticDescriptor FamilyNotOverAnEnum { get; } = new(
        "family-not-over-an-enum",
        Severity.Error,
        "a family declares one routine per member of a named enum, and `{0}` {1}",
        "A family declares one routine or one data declaration per member of a named enum, and is named after the "
            + "members, so the enum has to be one whose members have names.");

    internal static DiagnosticDescriptor FamilyDeclaresTooMuch { get; } = new(
        "family-declares-too-much",
        Severity.Error,
        "`{0}` here would declare one {1} per member, and everything inside it once for each: a family declares routines and data, so write one family per role, `note::{2}` and `stop::{3}`",
        "A family declares one name per member. A block that declared several would declare each of them once per "
            + "member, which is a family per role written as one.");

    internal static DiagnosticDescriptor FamilyMemberCollides { get; } = new(
        "family-member-collides",
        Severity.Error,
        "`{0}` is a member of `{1}` and is already declared in this scope: a family declares one name per member",
        "Each instance of a family takes the member's name, so a name already declared in the scope would be "
            + "declared twice.");

    internal static DiagnosticDescriptor MacroMisplaced { get; } = new(
        "macro-misplaced",
        Severity.Error,
        "a `.macro` belongs at file level or in a `.scope`, not inside {0}",
        "A macro is a declaration, and declarations of macros belong at file level or in a `.scope`. A routine "
            + "holds code.");

    internal static DiagnosticDescriptor DefinedAsksAboutDefines { get; } = new(
        "defined-asks-about-defines",
        Severity.Error,
        "`{0}` is declared by the program, and `.defined` asks only about defines: a condition tests the build configuration, and a check on the program is an `.assert`",
        "A condition tests the build configuration and nothing else, so `.defined` asks only about defines. A "
            + "check on the program is an `.assert`, which the analysis can answer.");

    internal static DiagnosticDescriptor DefineRedeclared { get; } = new(
        "define-redeclared",
        Severity.Error,
        "`{0}` is a define, and a file may not declare one",
        "A define is visible in every file as if every module had brought it in, so a declaration of the same "
            + "name would shadow it in one file and not in another.");

    internal static DiagnosticDescriptor SignatureMissing { get; } = new(
        "signature-missing",
        Severity.Error,
        "`{0}` declares no processor state, and {1} has no body to check one against: say what a caller must hold to, or `?` where nothing is known",
        "On the 65816 a routine with no body is all the analysis has to go on at its callers, so it says what it "
            + "takes and what it leaves. `?` says that nothing is known.");

    internal static DiagnosticDescriptor ExportNarrowsAddressSize { get; } = new(
        "export-narrows-address-size",
        Severity.Error,
        "`{0}` is `{1}`, and an export may widen an address size but not narrow it: `{2}: {3}` or wider",
        "Other modules reach the name at the size the export gives, and reaching a two-byte address as if it were "
            + "one byte reads the wrong place. Widening is safe; narrowing is not.");

    internal static DiagnosticDescriptor LinkerNameIsAnInstruction { get; } = new(
        "linker-name-is-an-instruction",
        Severity.Error,
        "`{0}` is an instruction to ca65, which cannot define a symbol spelled like one: an exported name is written into the output exactly as `as` gives it",
        "An exported name goes into the output exactly as `as` gives it, and ca65 reads a line that starts with "
            + "an instruction as an instruction. There is no spelling to fall back on, which is why this is an error "
            + "where `mnemonic-name` is a warning.");

    internal static DiagnosticDescriptor UnusedSymbol { get; } = new(
        "unused-symbol",
        Severity.Warning,
        "`{0}` is never used: nothing names it, and it is not exported",
        "Nothing in the program names the declaration and the file does not export it, so nothing reads it. Data "
            + "that holds values may be there for where it lands, so only a declaration that reserves storage is "
            + "reported.");

    internal static DiagnosticDescriptor UnusedUseItem { get; } = new(
        "unused-use-item",
        Severity.Warning,
        "`{0}` is brought in and nothing names it: the `.use` item may go",
        "The `.use` brings the name in and the file never writes it. A `.export .use` re-exports rather than "
            + "uses, and is not reported.");

    // Values: constants, expressions, built-in functions and the build configuration.

    internal static DiagnosticDescriptor DefinedInTermsOfItself { get; } = new(
        "defined-in-terms-of-itself",
        Severity.Error,
        "`{0}` is defined in terms of itself",
        "Working the value out would need the value. The cycle is reported once, at one of the declarations in "
            + "it.");

    internal static DiagnosticDescriptor NumberTooWide { get; } = new(
        "number-too-wide",
        Severity.Error,
        "{0} does not fit the 32 bits ca65 computes in: a value the output carries is at least -$80000000 and at most $ffffffff",
        "nt65 computes in 64 signed bits and ca65 in 32, so a value that reaches the output has to fit ca65's. It "
            + "is checked where it is declared rather than again at every name of it.");

    internal static DiagnosticDescriptor ArithmeticOverflow { get; } = new(
        "arithmetic-overflow",
        Severity.Error,
        "{0} overflows the 64 bits nt65 computes in",
        "Arithmetic is 64-bit and signed, and an operation that leaves those bits has no value: nothing wraps, "
            + "because a wrapped number is one nobody wrote.");

    internal static DiagnosticDescriptor ShiftCountOutOfRange { get; } = new(
        "shift-count-out-of-range",
        Severity.Error,
        "a shift counts 0 to 63 places, and this one counts {0}",
        "A shift moves a 64-bit value by a number of places it has.");

    internal static DiagnosticDescriptor SqrtOfANegative { get; } = new(
        "sqrt-of-a-negative",
        Severity.Error,
        "`.sqrt` has no answer for {0}: no whole number squared is negative",
        "`.sqrt(n)` is the largest whole number whose square is at most n, which a negative number has none of.");

    internal static DiagnosticDescriptor TurnOrScaleOutOfRange { get; } = new(
        "turn-or-scale-out-of-range",
        Severity.Error,
        "`{0}` takes a turn of 1 to {1} and a scale of at most that either way",
        "A whole turn is however many units the table counts it in, and the scale is what the answer is measured "
            + "in. Both are bounded because a value the output carries fits ca65's 32 bits anyway.");

    internal static DiagnosticDescriptor BuiltinArguments { get; } = new(
        "builtin-arguments",
        Severity.Error,
        "`{0}` takes {1}",
        "The built-in was given a different number of arguments from the one it takes.");

    internal static DiagnosticDescriptor DivisionByZero { get; } = new(
        "division-by-zero",
        Severity.Error,
        "division by zero",
        "nt65 computes while it builds, so a division by zero has no value to write out.");

    internal static DiagnosticDescriptor OperatorOnText { get; } = new(
        "operator-on-text",
        Severity.Error,
        "`{0}` cannot be used on a string",
        "Text is a sequence of bytes for a data declaration to write. The arithmetic and bitwise operators are on "
            + "numbers.");

    internal static DiagnosticDescriptor ScopeHasNoAddress { get; } = new(
        "scope-has-no-address",
        Severity.Error,
        "`{0}` is a scope, which has no address: a routine or data inside it does",
        "A `.scope` is a name for other names, and takes no bytes of its own. A routine or a declaration inside "
            + "it has an address.");

    internal static DiagnosticDescriptor EnumMemberIsNotAnAddress { get; } = new(
        "enum-member-is-not-an-address",
        Severity.Error,
        "`{0}` is an enum member, whose value is a constant, and this names an address",
        "An enum member is a constant. Where an address is wanted, what was meant is usually a family instance "
            + "named after the member.");

    internal static DiagnosticDescriptor NotIndexable { get; } = new(
        "not-indexable",
        Severity.Error,
        "`{0}` is {1}",
        "`[i]` picks the i-th element of a counted data declaration. Anything else has no elements to pick from.");

    internal static DiagnosticDescriptor ElementIndexNotConstant { get; } = new(
        "element-index-not-constant",
        Severity.Error,
        "an element index is a constant: an index worked out as the program runs is what `{0},x` is for",
        "Which element `[i]` names is decided while nt65 builds, because it becomes an address in the output. An "
            + "index worked out as the program runs is what indexed addressing is for.");

    internal static DiagnosticDescriptor ElementIndexOutOfRange { get; } = new(
        "element-index-out-of-range",
        Severity.Error,
        "{0}",
        "The declaration says how many elements it holds, and this index is not one of them.");

    internal static DiagnosticDescriptor NothingToMeasure { get; } = new(
        "nothing-to-measure",
        Severity.Error,
        "`{0}` is {1} and takes no bytes of its own, so `{2}` has nothing to measure",
        "What is named takes no bytes of its own, so there is nothing for the measurement to be of.");

    internal static DiagnosticDescriptor CountofHasNoElements { get; } = new(
        "countof-has-no-elements",
        Severity.Error,
        "`{0}` is {1}, which has bytes and no elements: `.sizeof({2})` is how many bytes it takes",
        "`.countof` answers how many elements a counted declaration holds. Something that is bytes and not "
            + "elements is measured with `.sizeof`.");

    internal static DiagnosticDescriptor SizeofDependsOnAlignment { get; } = new(
        "sizeof-depends-on-alignment",
        Severity.Error,
        "nt65 cannot say how many bytes `{0}` takes: an `.align` in it depends on where it lands, and `.spanof({1})` measures it in the output",
        "An `.align` pads by however much it takes to reach the boundary, which the linker decides. `.spanof` "
            + "asks the linker instead, and is a link-time value.");

    internal static DiagnosticDescriptor MeasuresADeclaration { get; } = new(
        "measures-a-declaration",
        Severity.Error,
        "`{0}` measures a declaration, and `{1}` is a place in one",
        "The measuring functions take a whole declaration. A position inside one is not something with a size.");

    internal static DiagnosticDescriptor NotMeasurable { get; } = new(
        "not-measurable",
        Severity.Error,
        "`{0}` is {1}: `{2}` measures a `.data` declaration, a routine or a type",
        "Only something that takes bytes, or a type that says how many bytes it takes, can be measured.");

    internal static DiagnosticDescriptor FunctionArgumentCount { get; } = new(
        "function-argument-count",
        Severity.Error,
        "`{0}` takes {1} argument(s), and {2} were given",
        "A `.func` takes exactly the parameters it declares. There are no defaults and no overloads.");

    internal static DiagnosticDescriptor ConditionIsText { get; } = new(
        "condition-is-text",
        Severity.Error,
        "a condition is a number, and this is text",
        "A condition decides which branch the build takes, which is a number being zero or not.");

    internal static DiagnosticDescriptor ConditionNamesTheProgram { get; } = new(
        "condition-names-the-program",
        Severity.Error,
        "`{0}` is not a define or a `.config`. A condition tests the build configuration, and a check on the program is an `.assert`",
        "Which declarations a program has follows from which branches the build takes, so a condition may not "
            + "depend on the program: it would be answering a question it is deciding. An `.assert` is the check on the "
            + "program.");

    internal static DiagnosticDescriptor ConditionAsksAboutTheProgram { get; } = new(
        "condition-asks-about-the-program",
        Severity.Error,
        "`{0}` asks about the program. A condition tests the build configuration, and a check on the program is an `.assert`",
        "The measuring functions ask what the program is, and a condition decides what the program is. An "
            + "`.assert` is the check that runs once it is decided.");

    internal static DiagnosticDescriptor ConditionCallsAFunction { get; } = new(
        "condition-calls-a-function",
        Severity.Error,
        "a condition may not call a function the program declares",
        "A `.func` is a declaration, and which declarations exist is what the conditions are deciding.");

    internal static DiagnosticDescriptor SelectArguments { get; } = new(
        "select-arguments",
        Severity.Error,
        "`.select` takes a condition and the two values it chooses between: `.select(c, a, b)`",
        "`.select` is the one branch an expression may take: a condition and the two values.");

    internal static DiagnosticDescriptor SelectConditionIsText { get; } = new(
        "select-condition-is-text",
        Severity.Error,
        "a `.select` condition is a number, and this is text",
        "A `.select` chooses on a number being zero or not.");

    internal static DiagnosticDescriptor SelectConditionNotConstant { get; } = new(
        "select-condition-not-constant",
        Severity.Error,
        "a `.select` condition is a constant, and this is not one",
        "Which of the two values a `.select` is depends on the condition, so the condition has to be known while "
            + "nt65 builds.");

    internal static DiagnosticDescriptor CpuUnderACondition { get; } = new(
        "cpu-under-a-condition",
        Severity.Error,
        "`.cpu` states the program's processor, which a condition may test, so it may not be written under an `.if`",
        "A condition may test the processor, so the processor is settled before any condition is answered.");

    internal static DiagnosticDescriptor CpuDisagrees { get; } = new(
        "cpu-disagrees",
        Severity.Error,
        "this program is built for the {0}, so `.cpu {1}` disagrees",
        "A program is built for one processor. Where the project or the command line says which, a `.cpu` in a "
            + "file says the same or says nothing.");

    internal static DiagnosticDescriptor ElseWithoutIf { get; } = new(
        "else-without-if",
        Severity.Error,
        "`{0}` continues an `.if`, and there is none to continue",
        "An `.elseif` or an `.else` continues an `.if`, and there is no open one here.");

    internal static DiagnosticDescriptor ConfigMisplaced { get; } = new(
        "config-misplaced",
        Severity.Error,
        "a `.config` is written at file level, outside every block: which settings a program has depends on no condition",
        "A `.config` is part of the module's interface: which settings a program has is what a build sets, and "
            + "cannot itself depend on a condition.");

    internal static DiagnosticDescriptor ConfigIsText { get; } = new(
        "config-is-text",
        Severity.Error,
        "a `.config` is a number, and this is text",
        "A setting is a number, so that a build may give it one.");

    internal static DiagnosticDescriptor SettingUnknown { get; } = new(
        "setting-unknown",
        Severity.Error,
        "`{0}` names no `.config`: the build sets a setting a module declares and exports",
        "A name with a module's path in the build configuration sets that module's `.config`, and the module "
            + "declares none by that name.");

    internal static DiagnosticDescriptor SettingNotExported { get; } = new(
        "setting-not-exported",
        Severity.Error,
        "`{0}` is not exported by module `{1}`, so the build cannot set it: a setting the module keeps to itself is not part of its configuration",
        "A `.config` the module keeps to itself is not part of its configuration, so a build has no say in it.");

    internal static DiagnosticDescriptor DeclarationInARepetition { get; } = new(
        "declaration-in-a-repetition",
        Severity.Error,
        "{0} belongs outside a repetition: {1}",
        "A repetition writes its body out once per turn. Anything that names one symbol, or that is program-wide, "
            + "would be written once per turn along with it.");

    internal static DiagnosticDescriptor RepeatCountNotConstant { get; } = new(
        "repeat-count-not-constant",
        Severity.Error,
        "a `.repeat` count is a constant, and this is not one",
        "How many times a body is written out decides what the program holds, so it is known while nt65 builds.");

    internal static DiagnosticDescriptor RepeatCountNegative { get; } = new(
        "repeat-count-negative",
        Severity.Error,
        "a `.repeat` count cannot be negative, and this one is {0}",
        "A repetition runs a number of turns, and a negative number of turns is not one.");

    internal static DiagnosticDescriptor EachNotOverAList { get; } = new(
        "each-not-over-a-list",
        Severity.Error,
        "`.each` walks a list or an enum, and this is neither",
        "`.each` walks something with items in a fixed order: a `.list`, or the members of an enum.");

    internal static DiagnosticDescriptor BindingNotOverAnEnum { get; } = new(
        "binding-not-over-an-enum",
        Severity.Error,
        "`{0}` does not walk an enum, so it names no member: a path ends in a repetition's name only over an enum's members",
        "A path may end in a repetition's name only where that name stands for an enum member, which is what "
            + "makes it a name of something.");

    internal static DiagnosticDescriptor FamilyMemberMissing { get; } = new(
        "family-member-missing",
        Severity.Error,
        "`{0}` has no `{1}`, which `{2}` stands for on this turn",
        "On this turn the repetition's name stands for a member the container does not have.");

    internal static DiagnosticDescriptor IncbinUnreadable { get; } = new(
        "incbin-unreadable",
        Severity.Error,
        "`{0}` cannot be read",
        "How many bytes an `.incbin` takes is read from the file beside the source that names it, and the file is "
            + "not there or cannot be read. ca65 would read it again at assembly time.");

    internal static DiagnosticDescriptor CharmapHasNoEntry { get; } = new(
        "charmap-has-no-entry",
        Severity.Error,
        "`{0}` does not map `{1}`",
        "A charmap is a complete mapping for the text it is applied to: a character it does not name has no byte.");

    internal static DiagnosticDescriptor MemberHasNoValue { get; } = new(
        "member-has-no-value",
        Severity.Error,
        "`{0}` is a member, which reserves room and holds no value: several are `{1}[n]`",
        "A `.res` member reserves room and holds nothing, so there is no value to read from it. Several values of "
            + "one type are a counted member.");

    internal static DiagnosticDescriptor MemberCountNotANumber { get; } = new(
        "member-count-not-a-number",
        Severity.Error,
        "`{0}` is a member, whose count is a number: `{1}[n]`",
        "How many elements a member holds is part of the type, and is written as a number.");

    internal static DiagnosticDescriptor MemberReservesNothing { get; } = new(
        "member-reserves-nothing",
        Severity.Error,
        "`{0}` reserves no room a member may take",
        "The member takes no room, so nothing can be laid out in it.");

    internal static DiagnosticDescriptor TargetArgument { get; } = new(
        "target-argument",
        Severity.Error,
        "`.target` takes {0}",
        "`.target` asks whether the program is built for one named processor, spelled as the project file "
            + "spells it.");

    internal static DiagnosticDescriptor HasArgument { get; } = new(
        "has-argument",
        Severity.Error,
        "`.has` takes a mnemonic, such as `.has(phx)`",
        "`.has` asks whether the processor has an instruction, whichever it is, which is what a program that runs "
            + "on more than one asks rather than listing the processors that have it.");

    // Macros: macro declarations, calls, arguments and expansion.

    internal static DiagnosticDescriptor DeclarationInAMacroBody { get; } = new(
        "declaration-in-a-macro-body",
        Severity.Error,
        "{0} belongs outside a macro body: {1}",
        "A macro body is written out at each call, in the module that calls it. Each of these would either declare "
            + "a name in the caller or make something program-wide depend on how many times the macro is called.");

    internal static DiagnosticDescriptor MacroRecursive { get; } = new(
        "macro-recursive",
        Severity.Error,
        "`{0}` calls itself{1}, and every expansion has to be bounded",
        "Every expansion is bounded, and a depth limit is not a bound: a macro that calls itself, however far "
            + "around, has none. A `list` parameter or an `.each` does what walking an argument list did.");

    internal static DiagnosticDescriptor MacroNamesUnexported { get; } = new(
        "macro-names-unexported",
        Severity.Error,
        "`{0}!` is exported but names `{1}`, which is not: a macro expands in the module that calls it, and what it names there has to be exported",
        "A macro expands in the module that calls it, so every name in its body has to be reachable from there.");

    internal static DiagnosticDescriptor ParameterUnknown { get; } = new(
        "parameter-unknown",
        Severity.Error,
        "`{0}` has no parameter called `{1}`",
        "A named argument names a parameter the macro declares.");

    internal static DiagnosticDescriptor BlockParameterUnknown { get; } = new(
        "block-parameter-unknown",
        Severity.Error,
        "`{0}` has no `block` parameter called `{1}`",
        "A block argument after the parentheses names a `block` parameter the macro declares.");

    internal static DiagnosticDescriptor ArgumentGivenTwice { get; } = new(
        "argument-given-twice",
        Severity.Error,
        "`{0}` is given twice",
        "Each parameter takes one argument, positionally or by name, and not both.");

    internal static DiagnosticDescriptor ArgumentMissing { get; } = new(
        "argument-missing",
        Severity.Error,
        "`{0}` is not given {1}",
        "A parameter with no default is given at every call.");

    internal static DiagnosticDescriptor ArgumentAfterANamedOne { get; } = new(
        "argument-after-a-named-one",
        Severity.Error,
        "a positional argument comes before the named ones",
        "Positional arguments bind in order, so they come before the named ones, as in every language that has "
            + "both.");

    internal static DiagnosticDescriptor ArgumentCount { get; } = new(
        "argument-count",
        Severity.Error,
        "`{0}` takes {1}, and this call gives more",
        "The call gives more positional arguments than the macro has parameters to bind them to.");

    internal static DiagnosticDescriptor BlockArgumentInParentheses { get; } = new(
        "block-argument-in-parentheses",
        Severity.Error,
        "`{0}` takes a block, which is written after the parentheses",
        "A block is written after the parentheses, where it reads as the block it is.");

    internal static DiagnosticDescriptor BlockArgumentUnexpected { get; } = new(
        "block-argument-unexpected",
        Severity.Error,
        "`{0}` takes no block, and this call gives it one",
        "The macro declares no `block` parameter, so there is nowhere for the block to go.");

    internal static DiagnosticDescriptor BlockContinuesNothing { get; } = new(
        "block-continues-nothing",
        Severity.Error,
        "this block continues no macro call",
        "A `} name {` closes one block argument and opens the next, and there is no call above it for it to "
            + "belong to.");

    internal static DiagnosticDescriptor BlockChangesState { get; } = new(
        "block-changes-state",
        Severity.Error,
        "the block given to `{0}!` has to leave the state as it found it: it starts with `{1}` and ends with `{2}`",
        "A macro that takes a block writes its own code around it, and has no way to know what the block did to "
            + "the processor unless the block leaves it as it found it.");

    internal static DiagnosticDescriptor ParameterAfterBlock { get; } = new(
        "parameter-after-block",
        Severity.Error,
        "`{0}` comes after the `block` parameter `{1}`, and a block is written after the parentheses",
        "A block is written after the parentheses, so a `block` parameter is the last one.");

    internal static DiagnosticDescriptor ParameterAfterList { get; } = new(
        "parameter-after-list",
        Severity.Error,
        "`{0}` comes after the `list` parameter `{1}`, which takes every remaining argument",
        "A `list` parameter takes every remaining argument, so nothing after it could ever be given one.");

    internal static DiagnosticDescriptor OperandArgumentParenthesized { get; } = new(
        "operand-argument-parenthesized",
        Severity.Error,
        "`{0}` takes an operand, and `{1}` reads as an expression in parentheses. Brace it to pass indirect addressing",
        "An unbraced `(ptr)` is an expression in parentheses, as it is everywhere else. Braces pass a whole "
            + "operand, indirection and index included.");

    internal static DiagnosticDescriptor WordArgumentNotListed { get; } = new(
        "word-argument-not-listed",
        Severity.Error,
        "`{0}` takes one of {1}, and this is {2}",
        "A `one(...)` parameter takes one of the words it lists, and the words are never looked up.");

    internal static DiagnosticDescriptor WordArgumentAmbiguous { get; } = new(
        "word-argument-ambiguous",
        Severity.Error,
        "`{0}` takes one of {1}, and `{2}` may also be {3}",
        "The word given is also one of the words another `one(...)` in the same call would accept, so which "
            + "parameter it binds to would depend on the order they are read in.");

    internal static DiagnosticDescriptor IdentArgumentNotAName { get; } = new(
        "ident-argument-not-a-name",
        Severity.Error,
        "`{0}` takes a name, and this is not one",
        "An `ident` parameter stands for a name the body declares or writes, so the argument has to be one.");

    internal static DiagnosticDescriptor ExpressionArgumentBraced { get; } = new(
        "expression-argument-braced",
        Severity.Error,
        "`{0}` takes an expression, and a braced argument is a whole operand",
        "Braces pass a whole operand. A parameter that takes an expression takes it unbraced.");

    internal static DiagnosticDescriptor ExpansionLimit { get; } = new(
        "expansion-limit",
        Severity.Error,
        "the expansions in this file come to more than {0} statements, which is as far as nt65 goes",
        "Expansion is bounded, and the bound is far beyond any program written by hand. Reaching it means a "
            + "repetition or a nest of macros is multiplying out further than was meant.");

    internal static DiagnosticDescriptor RepeatTooMany { get; } = new(
        "repeat-too-many",
        Severity.Error,
        "this repetition runs {0} times, and {1} turns is as far as nt65 goes",
        "A repetition writes its body out once per turn, and the bound on the turns is the bound on how much one "
            + "line may become.");

    // Data: declarations, records, arrays, text and padding.

    internal static DiagnosticDescriptor ElementCountEmpty { get; } = new(
        "element-count-empty",
        Severity.Error,
        "`[]` counts the values given, and there are none: `{0}[n]` holds n",
        "`[]` means as many elements as the values given, and no values are given. A declaration that reserves "
            + "room says how much.");

    internal static DiagnosticDescriptor ElementCountNotConstant { get; } = new(
        "element-count-not-constant",
        Severity.Error,
        "an array's count is a constant",
        "How many elements a declaration holds decides how many bytes it takes, which is decided while nt65 "
            + "builds.");

    internal static DiagnosticDescriptor ElementCountNegative { get; } = new(
        "element-count-negative",
        Severity.Error,
        "an array's count cannot be negative, and this one is {0}",
        "A count is how many elements the declaration holds, and a negative number of them is not one.");

    internal static DiagnosticDescriptor ElementCountMismatch { get; } = new(
        "element-count-mismatch",
        Severity.Error,
        "this array holds {0} {1}, and its values come to {2}",
        "A declaration with a count holds exactly that many elements: nt65 does not pad the rest or drop the "
            + "extra. `[]` counts the values given.");

    internal static DiagnosticDescriptor ElementNotAValue { get; } = new(
        "element-not-a-value",
        Severity.Error,
        "a `{0}` element is one value, and braces hold a record or a list",
        "Braces hold a record or a list, and this element is one value.");

    internal static DiagnosticDescriptor ElementNotARecord { get; } = new(
        "element-not-a-record",
        Severity.Error,
        "each element of {0} is {1}, written `{{ member = value }}`",
        "Each element of an array of records is written as a record, so that which member each value goes to is "
            + "written down.");

    internal static DiagnosticDescriptor ElementIsOneValue { get; } = new(
        "element-is-one-value",
        Severity.Error,
        "each element of `{0}` is one `{1}`",
        "The member holds elements of a plain type, and each of them is one value rather than a record or a list.");

    internal static DiagnosticDescriptor MemberUnknown { get; } = new(
        "member-unknown",
        Severity.Error,
        "`{0}` has no member `{1}`",
        "A record initializer names the members of the type it initializes.");

    internal static DiagnosticDescriptor MemberGivenTwice { get; } = new(
        "member-given-twice",
        Severity.Error,
        "`{0}` is given a value twice: a member is named at most once",
        "Each member takes one value, so which one it is does not depend on the order they are read in.");

    internal static DiagnosticDescriptor UnionManyMembersGiven { get; } = new(
        "union-many-members-given",
        Severity.Error,
        "`{0}` is a union, whose members all start at offset 0, so it takes a value for at most one of them",
        "A union's members share the same bytes, so giving two of them values would write each over the other.");

    internal static DiagnosticDescriptor MemberNeedsARecord { get; } = new(
        "member-needs-a-record",
        Severity.Error,
        "`{0}` is a `{1}`, which takes a braced list of its members",
        "The member is itself a record, so its value is written as one.");

    internal static DiagnosticDescriptor MemberNeedsAList { get; } = new(
        "member-needs-a-list",
        Severity.Error,
        "`{0}` is an array, which takes a braced list: `{1} = {{ … }}`",
        "The member holds several elements, so its value is a braced list of them.");

    internal static DiagnosticDescriptor MemberTakesOneValue { get; } = new(
        "member-takes-one-value",
        Severity.Error,
        "`{0}` is not a record or an array, and takes one value",
        "The member is one value of a plain type, so braces around it would be a record or a list it is not.");

    internal static DiagnosticDescriptor MemberCountMismatch { get; } = new(
        "member-count-mismatch",
        Severity.Error,
        "`{0}` holds {1} {2}, and this list gives {3}",
        "A member holds exactly as many elements as its type says.");

    internal static DiagnosticDescriptor MemberTextTooLong { get; } = new(
        "member-text-too-long",
        Severity.Error,
        "`{0}` has room for {1} bytes, and this is {2}",
        "Text goes into the room the member reserves, and there is not enough of it.");

    internal static DiagnosticDescriptor MemberNotText { get; } = new(
        "member-not-text",
        Severity.Error,
        "`{0}` is one `{1}`, and this text is {2} bytes: text takes a member reserved with `.res`",
        "A member of a plain type holds one value. Room for text is reserved with `.res`, which says how much.");

    internal static DiagnosticDescriptor StrzNotText { get; } = new(
        "strz-not-text",
        Severity.Error,
        "`.strz` takes one text: a string, a string constant, or a charmap applied to one",
        "`.strz` writes text and the zero that ends it, so it takes exactly one text.");

    internal static DiagnosticDescriptor StrzZeroInText { get; } = new(
        "strz-zero-in-text",
        Severity.Error,
        "{0}: `.strz` writes the zero that ends it",
        "A zero byte is what ends the text, so a charmap that maps a character to zero would end it in the "
            + "middle.");

    internal static DiagnosticDescriptor TextNotAscii { get; } = new(
        "text-not-ascii",
        Severity.Error,
        "text is ASCII outside a charmap; write `\\xHH` for a byte above $7f",
        "Which byte a character above $7f becomes depends on an encoding nt65 does not choose. A charmap says "
            + "what the bytes are, and `\\xHH` writes one directly.");

    internal static DiagnosticDescriptor CharmapValueNotAByte { get; } = new(
        "charmap-value-not-a-byte",
        Severity.Error,
        "`{0}` is not a byte; a charmap maps text to bytes",
        "A charmap maps text to bytes, so each value it gives is one byte.");

    internal static DiagnosticDescriptor FarAddressInWord { get; } = new(
        "far-address-in-word",
        Severity.Error,
        "`{0}` is a far address, and `{1}` holds 16 bits: `.faraddr` holds all of it, and `.loword({2})` the low 16 bits",
        "A far address is a bank and a sixteen-bit offset. Writing it into two bytes would drop the bank "
            + "silently, so nt65 asks which was meant.");

    internal static DiagnosticDescriptor AddressDoesNotFit { get; } = new(
        "address-does-not-fit",
        Severity.Error,
        "`{0}` is {1} address, and {2}: {3}",
        "The address is wider than the slot it is written into, so ca65 would refuse the fragment with a range "
            + "error. The fix names the part of it the slot has room for.");

    internal static DiagnosticDescriptor AddressNegative { get; } = new(
        "address-negative",
        Severity.Error,
        "{0} is negative, and an address is not",
        "An address is where something is, and nothing is before the start of memory.");

    internal static DiagnosticDescriptor ValueTooWide { get; } = new(
        "value-too-wide",
        Severity.Error,
        "{0} does not fit in {1}",
        "The value does not fit the bytes the declaration reserves for it. A wider type, or one of the byte "
            + "operators, says which part was meant.");

    internal static DiagnosticDescriptor ResCountNotConstant { get; } = new(
        "res-count-not-constant",
        Severity.Error,
        "a `.res` count must be a constant",
        "How much room is reserved decides where everything after it goes, so it is decided while nt65 builds.");

    internal static DiagnosticDescriptor ResCountOutOfRange { get; } = new(
        "res-count-out-of-range",
        Severity.Error,
        "a `.res` count is between 0 and $ffff, not {0}: that is what ca65 reserves in one directive",
        "One `.res` in the output reserves what ca65 reserves in one directive. More room than that is more than "
            + "one declaration.");

    internal static DiagnosticDescriptor AlignBoundaryNotConstant { get; } = new(
        "align-boundary-not-constant",
        Severity.Error,
        "an `.align` boundary must be a constant",
        "Where the padding ends is worked out from the boundary, so the boundary is known while nt65 builds.");

    internal static DiagnosticDescriptor AlignBoundaryNotPowerOfTwo { get; } = new(
        "align-boundary-not-power-of-two",
        Severity.Error,
        "an `.align` boundary must be a power of two, not {0}",
        "ld65 aligns on powers of two, and a boundary that is not one cannot be asked of it.");

    internal static DiagnosticDescriptor ResNotADeclaration { get; } = new(
        "res-not-a-declaration",
        Severity.Error,
        "`.res` is only padding: data is declared with its type, `.byte[n]`, and holds zeros where it gives no values",
        "A `.res` reserves room inside a declaration. A declaration of its own says what its bytes are, and "
            + "reserves the room it does not fill.");

    internal static DiagnosticDescriptor AlignNotADeclaration { get; } = new(
        "align-not-a-declaration",
        Severity.Error,
        "`.align` is only padding, and has no name: it goes between declarations",
        "An `.align` is padding between declarations, and has nothing of its own to name.");

    // Placement: segments, where a declaration sits and how wide an address is.

    internal static DiagnosticDescriptor SegmentUndeclared { get; } = new(
        "segment-undeclared",
        Severity.Error,
        "segment \"{0}\" is not declared",
        "Every segment is declared once, by a file or by the project, with the address size it is reached at. "
            + "Nothing places bytes in an undeclared one.");

    internal static DiagnosticDescriptor SegmentDeclaredTwice { get; } = new(
        "segment-declared-twice",
        Severity.Error,
        "segment \"{0}\" is already declared",
        "A segment is declared once across the whole program, so that every file reaches it the same way.");

    internal static DiagnosticDescriptor SegmentAttributeTwice { get; } = new(
        "segment-attribute-twice",
        Severity.Error,
        "segment \"{0}\" already gives its `{1}`",
        "Each of a segment's attributes is one answer, so it is given once.");

    internal static DiagnosticDescriptor SegmentDpNotZp { get; } = new(
        "segment-dp-not-zp",
        Severity.Error,
        "`dp` says which direct page a `zp` segment is reached through, and \"{0}\" is not `zp`",
        "Only a `zp` segment is reached through the direct page, so only a `zp` segment has one to name.");

    internal static DiagnosticDescriptor SegmentAttributeNotConstant { get; } = new(
        "segment-attribute-not-constant",
        Severity.Error,
        "`{0}` needs a constant",
        "Where a segment sits decides how every reference to what is in it is checked, so it is a number known "
            + "while nt65 builds.");

    internal static DiagnosticDescriptor SegmentAttributeOutOfRange { get; } = new(
        "segment-attribute-out-of-range",
        Severity.Error,
        "{0}",
        "The direct page is a sixteen-bit address and a bank is one byte.");

    internal static DiagnosticDescriptor SegmentMirrorInvalid { get; } = new(
        "segment-mirror-invalid",
        Severity.Error,
        "a mirror is a constant bank, or a range of banks from the lower to the higher, such as `$00..$3f`",
        "A mirror is a bank, or a range written from the lower bank to the higher.");

    internal static DiagnosticDescriptor SegmentMirrorsNeedABank { get; } = new(
        "segment-mirrors-need-a-bank",
        Severity.Error,
        "segment \"{0}\" gives `mirrors` and no `bank`: a mirror shows a segment's home bank in another bank",
        "A mirror shows a segment's home bank in another bank, so there has to be a home bank for it to show.");

    internal static DiagnosticDescriptor SegmentBlockRedundant { get; } = new(
        "segment-block-redundant",
        Severity.Error,
        "this block names \"{0}\", the segment it is already in, so its contents would stay inline where fall-through reaches them",
        "A block that names the segment it is already in moves nothing. Its contents stay where fall-through "
            + "reaches them, which is what the block was probably meant to prevent.");

    internal static DiagnosticDescriptor OutsideEverySegment { get; } = new(
        "outside-every-segment",
        Severity.Error,
        "{0} is outside every segment: a `.segment NAME` region or block places it",
        "Bytes go in a segment, and the linker decides where each segment goes. Nothing is placed by being "
            + "written first.");

    internal static DiagnosticDescriptor InstructionInData { get; } = new(
        "instruction-in-data",
        Severity.Error,
        "{0} in a `.proc`, and `.data` holds only data",
        "A `.data` declaration holds bytes. Code goes in a routine, where the analysis can follow it.");

    internal static DiagnosticDescriptor InstructionOutsideARoutine { get; } = new(
        "instruction-outside-a-routine",
        Severity.Error,
        "{0} in a `.proc`: code outside one is reached by nothing nt65 can follow",
        "Flow analysis follows routines, so code that is in none is code nothing can be said about.");

    internal static DiagnosticDescriptor PaddingOutsideARoutine { get; } = new(
        "padding-outside-a-routine",
        Severity.Error,
        "`{0}` outside a `.proc` belongs to a `.data` declaration{1}",
        "A `.res` or an `.align` reserves room inside something. Outside a routine that something is a `.data` "
            + "declaration.");

    internal static DiagnosticDescriptor FarNeeds65816 { get; } = new(
        "far-needs-65816",
        Severity.Error,
        "{0}: a `far` address needs the 65816",
        "A far address is a bank and an offset, which only the 65816 has. ca65 refuses `far` on every earlier "
            + "processor, so nt65 says so where it is written rather than writing output ca65 rejects.");

    // Instructions: mnemonics, operands, addressing modes and branch range.

    internal static DiagnosticDescriptor InstructionNotOnCpu { get; } = new(
        "instruction-not-on-cpu",
        Severity.Error,
        "`{0}` is not available on the {1}{2}",
        "The program is built for one processor, and this instruction is not in its set. Where another processor "
            + "nt65 knows has it, the message says which.");

    internal static DiagnosticDescriptor OperandMissing { get; } = new(
        "operand-missing",
        Severity.Error,
        "`{0}` needs an operand",
        "The instruction has no form that takes nothing.");

    internal static DiagnosticDescriptor OperandNotTaken { get; } = new(
        "operand-not-taken",
        Severity.Error,
        "`{0}` does not take this operand on the {1}",
        "The instruction exists on this processor, and not with an operand written this way.");

    internal static DiagnosticDescriptor OperandIsText { get; } = new(
        "operand-is-text",
        Severity.Error,
        "`{0}` is text, and an operand is a number or an address",
        "An operand is a number or an address. Text is a sequence of bytes, which a data declaration writes.");

    internal static DiagnosticDescriptor OperandHasNoNextByte { get; } = new(
        "operand-has-no-next-byte",
        Severity.Error,
        "{0} needs an operand with a next byte, and `{1}` is `{2}` here",
        "The instruction reads the byte after its operand, so the operand has to be one a next byte follows: a "
            + "macro argument that arrived as something else cannot stand there.");

    internal static DiagnosticDescriptor BranchOperandNotTaken { get; } = new(
        "branch-operand-not-taken",
        Severity.Error,
        "`{0}` branches to a near target, and does not take this operand",
        "A branch reaches a label near it, and takes only a label.");

    internal static DiagnosticDescriptor TransferPrefix { get; } = new(
        "transfer-prefix",
        Severity.Error,
        "`{0}` transfers control, and a control transfer is not sized by a prefix",
        "An address-size prefix chooses how wide an operand is read. Which form a jump or a call takes is decided "
            + "by the target it names.");

    internal static DiagnosticDescriptor TargetTooFar { get; } = new(
        "target-too-far",
        Severity.Error,
        "`{0}` takes a near target, and this one is far",
        "The instruction reaches within the bank, and the target is in another one.");

    internal static DiagnosticDescriptor TargetTooNear { get; } = new(
        "target-too-near",
        Severity.Error,
        "`{0}` takes a far target, and this one is {1}: `{2}` reaches it",
        "The instruction takes a three-byte target, and a shorter one reaches this target and costs less.");

    internal static DiagnosticDescriptor BranchOutOfReach { get; } = new(
        "branch-out-of-reach",
        Severity.Error,
        "`{0}` would branch {1} bytes, and a branch reaches only -128 to 127{2}",
        "A branch is one signed byte from the instruction after it. Reaching further is a branch over a jump, "
            + "which the fix writes.");

    internal static DiagnosticDescriptor AddressingModeMissing { get; } = new(
        "addressing-mode-missing",
        Severity.Error,
        "`{0}` has no {1} form of this operand on the {2}",
        "The instruction has no form of this shape on this processor.");

    internal static DiagnosticDescriptor AddressingModeTooNarrow { get; } = new(
        "addressing-mode-too-narrow",
        Severity.Error,
        "`{0}` has only a {1} form of this operand, and `{2}` is {3}",
        "The only form of this shape reaches a narrower address than the operand names.");

    internal static DiagnosticDescriptor AddressSizeUnreachable { get; } = new(
        "address-size-unreachable",
        Severity.Error,
        "`{0}` cannot reach a {1} address on the {2}",
        "The address is wider than anything this instruction can reach on this processor.");

    internal static DiagnosticDescriptor ImmediateTooWide { get; } = new(
        "immediate-too-wide",
        Severity.Error,
        "{0}, and {1} does not fit",
        "The value does not fit the bytes the immediate has. On the 65816 how many bytes that is comes from the "
            + "register width at this point.");

    internal static DiagnosticDescriptor DirectPageNeeds65816 { get; } = new(
        "direct-page-needs-65816",
        Severity.Error,
        "`d:` reaches an address through the 65816's direct page, and this program is built for the {0}",
        "The direct page moves, which is the 65816's. On earlier processors the zero page is fixed and `z:` says "
            + "so.");

    internal static DiagnosticDescriptor DirectPagePrefixOnSymbol { get; } = new(
        "direct-page-prefix-on-symbol",
        Severity.Error,
        "`d:` is for a constant address: a symbol reaches the direct page through a `zp` segment",
        "Where a symbol is reached from is the segment's to say, so that moving a declaration between segments "
            + "does not mean editing every line that names it.");

    internal static DiagnosticDescriptor DirectPageFormMissing { get; } = new(
        "direct-page-form-missing",
        Severity.Error,
        "`d:` makes a direct operand, and `{0}` has no direct form of this operand",
        "`d:` asks for the one-byte form, and the instruction has none of this shape.");

    internal static DiagnosticDescriptor DirectPageOnly { get; } = new(
        "direct-page-only",
        Severity.Error,
        "`{0}` is in \"{1}\", reached through the direct page at {2}, and is only a direct operand: as {3} operand it would reach its offset in the data bank",
        "The segment is reached through the direct page, so the symbol is an offset into it. Reached any other "
            + "way, that offset would be an address in the data bank.");

    internal static DiagnosticDescriptor AssertionFailed { get; } = new(
        "assertion-failed",
        Severity.Error,
        "{0}",
        "The condition is false, and nt65 knew enough to answer it. An assertion nt65 cannot answer is left for "
            + "the linker.");

    internal static DiagnosticDescriptor ConfigRefused { get; } = new(
        "config-refused",
        Severity.Error,
        "{0}",
        "The file says it will not be built in this configuration, and is not.");

    internal static DiagnosticDescriptor ConfigWarned { get; } = new(
        "config-warned",
        Severity.Warning,
        "{0}",
        "The file builds in this configuration and has something to say about it.");

    // Control flow: where flow goes, and what the analysis needs written beside it.

    internal static DiagnosticDescriptor AnnotationAboutNothing { get; } = new(
        "annotation-about-nothing",
        Severity.Error,
        "`{0}` is about the statement above it, and there is none here",
        "An annotation stands between the statement it is about and whatever follows, which is what makes it "
            + "readable without looking for what it attaches to.");

    internal static DiagnosticDescriptor CodeUnreachable { get; } = new(
        "code-unreachable",
        Severity.Warning,
        "this code is never reached: fall-through does not enter a nested segment block, so code there starts at a label a `.next` names or a `.state` declares",
        "Nothing runs into the code and nothing names it, so it is written out and never entered.");

    internal static DiagnosticDescriptor LabelUnreachable { get; } = new(
        "label-unreachable",
        Severity.Warning,
        "`{0}` is never reached: nothing runs into it and nothing names it",
        "Nothing falls into the label and nothing branches, jumps or calls to it.");

    internal static DiagnosticDescriptor RunsIntoData { get; } = new(
        "runs-into-data",
        Severity.Error,
        "the instruction above runs into this data. `.next` on it says where flow goes instead",
        "The bytes after the instruction are data, and the processor would execute them. A `.next` says where "
            + "flow goes instead.");

    internal static DiagnosticDescriptor RoutineRunsOffTheEnd { get; } = new(
        "routine-runs-off-the-end",
        Severity.Warning,
        "`{0}` runs off {1} into whatever {2}: `.next` {3}, or `.next ?` ends the path",
        "The routine ends without transferring control, so flow carries on into whatever the linker puts after "
            + "it. Where that was meant, a `.next` names it and the tail call is then checked.");

    internal static DiagnosticDescriptor NextTargetNotCode { get; } = new(
        "next-target-not-code",
        Severity.Error,
        "`{0}` is {1}, and `{2}` names somewhere code is",
        "A `.next` names places flow reaches, and what is named here is not one.");

    internal static DiagnosticDescriptor NextTableHasNoLabels { get; } = new(
        "next-table-has-no-labels",
        Severity.Error,
        "`{0}` holds no code labels, and `.next` reads the labels a table holds",
        "A `.next` may name a table of addresses, and flow then goes to each label the table holds. This one "
            + "holds none.");

    internal static DiagnosticDescriptor NextTargetNotATable { get; } = new(
        "next-target-not-a-table",
        Severity.Error,
        "`{0}` is not a table of addresses: `.next` reads the labels a table declared as `.addr` or `.faraddr` holds",
        "A `.next` that names data reads the labels the data holds, so the data has to be addresses.");

    internal static DiagnosticDescriptor NextRoutineNotAdjacent { get; } = new(
        "next-routine-not-adjacent",
        Severity.Error,
        "`.next {0}` says flow runs on into `{1}`, and it does not start where this statement ends: a routine runs into the one written directly after it",
        "A routine runs on into the one written directly after it. Naming any other routine would be saying "
            + "something the bytes do not do.");

    internal static DiagnosticDescriptor IndirectCallUnchecked { get; } = new(
        "indirect-call-unchecked",
        Severity.Error,
        "{0} calls where its operand points, which the analysis cannot see: `.next` names the routines it calls",
        "The call goes wherever the operand points, which the analysis cannot read. A `.next` names the routines "
            + "it may reach, and they are then checked as calls.");

    internal static DiagnosticDescriptor IndirectJumpUnchecked { get; } = new(
        "indirect-jump-unchecked",
        Severity.Error,
        "{0} goes where its operand points, which the analysis cannot see: `.next` names the labels it reaches, or `.next ?` ends the path",
        "The jump goes wherever the operand points. A `.next` names the labels it may reach, or `.next ?` says "
            + "the path ends here and checks nothing beyond it.");

    internal static DiagnosticDescriptor ComputedJumpUnchecked { get; } = new(
        "computed-jump-unchecked",
        Severity.Error,
        "{0} goes to a computed address, which the analysis cannot follow: `.next` names the labels it reaches, or `.next ?` ends the path where it is not an instruction boundary",
        "The target is worked out rather than named, so the analysis has no label to carry the state to.");

    internal static DiagnosticDescriptor PushedReturnUnchecked { get; } = new(
        "pushed-return-unchecked",
        Severity.Error,
        "`{0}` here returns to an address this block pushed, which makes it a jump: `.next` names where it goes",
        "The block pushes an address and returns to it, which is a jump written as a return. A `.next` says where "
            + "it goes.");

    internal static DiagnosticDescriptor JumpTargetNotALabel { get; } = new(
        "jump-target-not-a-label",
        Severity.Error,
        "{0} goes to `{1}`, {2} rather than a label, which the analysis cannot follow: `.next` names the labels it reaches, or `.next ?` ends the path",
        "A jump goes to a place in code. What is named here is something else, so the analysis cannot follow the "
            + "path.");

    internal static DiagnosticDescriptor JumpIntoData { get; } = new(
        "jump-into-data",
        Severity.Error,
        "`{0}` labels data, and this jumps to it: the label needs a `.state` after it saying what the state is there, and the data a `.next` saying where flow goes",
        "The label stands on data, and the processor would execute it. The label needs a `.state` and the data a "
            + "`.next`.");

    internal static DiagnosticDescriptor EntryNotDeclared { get; } = new(
        "entry-not-declared",
        Severity.Error,
        "`{0}` is inside `{1}`, and a jump into another routine needs the label declared: a `.state` after it says what the state is there",
        "A jump into the middle of another routine arrives where that routine's own analysis never sees it. A "
            + "`.state` after the label declares what is true there, and both sides are then checked against it.");

    internal static DiagnosticDescriptor ExportedEntryNotDeclared { get; } = new(
        "exported-entry-not-declared",
        Severity.Error,
        "`{0}` is inside `{1}`, and exporting it lets other modules jump into the routine: a `.state` after the label says what the state is there",
        "An exported label inside a routine is a door other modules may come through, and this module's analysis "
            + "never sees them arrive.");

    internal static DiagnosticDescriptor CodeLabelAsData { get; } = new(
        "code-label-as-data",
        Severity.Error,
        "`{0}` labels code and is used here as data, so flow may reach it where the analysis cannot see: a `.state` after the label says what the state is there, or a `.next` in `{1}` naming it carries the state to it",
        "The address of an instruction is taken and used as a value, so flow may reach that instruction somewhere "
            + "the analysis cannot see.");

    internal static DiagnosticDescriptor SelfModifyingUnchecked { get; } = new(
        "self-modifying-unchecked",
        Severity.Error,
        "{0} writes into the instruction at `{1}`: `.patch {2}` acknowledges it",
        "The store writes into an instruction. A `.patch` says so, and is what tells a reader that the "
            + "instruction is not what it looks like.");

    internal static DiagnosticDescriptor HandlerCalled { get; } = new(
        "handler-called",
        Severity.Error,
        "`{0}` is an interrupt handler, which the processor enters and `rti` leaves: a call to it would not come back",
        "An interrupt handler is entered by the processor and left with `rti`, which pulls the flags as well as "
            + "the address. A call to one would not come back.");

    internal static DiagnosticDescriptor HandlerReturnsNotRti { get; } = new(
        "handler-returns-not-rti",
        Severity.Error,
        "`{0}` is an interrupt handler, and leaves by `rti` rather than `{1}`",
        "The processor pushed the flags when it entered, and only `rti` pulls them.");

    internal static DiagnosticDescriptor NoreturnReturns { get; } = new(
        "noreturn-returns",
        Severity.Error,
        "`{0}` never returns, as its `noreturn` says, and `{1}` returns",
        "The routine says it never returns, and its callers are checked on that promise.");

    internal static DiagnosticDescriptor InlineDataMissing { get; } = new(
        "inline-data-missing",
        Severity.Error,
        "`{0}` returns past {1} written after each call, and {2}",
        "The routine's signature says each call is followed by data, which the routine returns past. This call is "
            + "not followed by it.");

    internal static DiagnosticDescriptor InlineCountNotConstant { get; } = new(
        "inline-count-not-constant",
        Severity.Error,
        "`{0}` returns past `{1}`, which needs a constant count of bytes",
        "How far the routine returns past its call decides where flow carries on, so it is a number known while "
            + "nt65 builds.");

    internal static DiagnosticDescriptor TailCallToHandler { get; } = new(
        "tail-call-to-handler",
        Severity.Error,
        "{0} is a tail call, and `{1}` is an interrupt handler, which leaves by `rti`: only a routine that never returns, or another interrupt handler, may jump to one",
        "A tail call leaves the return to the callee, and an interrupt handler returns with `rti`, which pulls "
            + "flags nobody pushed.");

    internal static DiagnosticDescriptor TailCallDistanceMismatch { get; } = new(
        "tail-call-distance-mismatch",
        Severity.Error,
        "{0} is a tail call, and `{1}` is {2} while `{3}` is {4}: it would return to the caller the wrong way",
        "A tail call leaves the return to the callee, so the callee has to return the way this routine's caller "
            + "expects.");

    internal static DiagnosticDescriptor KeepsBroken { get; } = new(
        "keeps-broken",
        Severity.Error,
        "`{0}` promises `keeps {1}`, and {2} {3} not what the routine was entered with here: restore {4} before returning, or a `.state keeps {5}` where the value comes back says so",
        "The routine promises to hand a register back as it was entered with it, and on this path it does not. "
            + "Callers are checked on that promise.");

    internal static DiagnosticDescriptor KeepsRedundant { get; } = new(
        "keeps-redundant",
        Severity.Warning,
        "`{0}` says nothing here: {1} is already what the routine was entered with",
        "The register is already what the routine was entered with, so saying so again says nothing.");

    // Processor state: the 65816's widths, mode, direct page, bank, stack and signatures.

    internal static DiagnosticDescriptor WidthUnknown { get; } = new(
        "width-unknown",
        Severity.Error,
        "`{0} #` needs the width of {1}, and {2}",
        "How many bytes a 65816 immediate takes is the register's width at that point, and no path reaching here "
            + "settles it. A `.state` after the label, or a signature on the routine, says what it is.");

    internal static DiagnosticDescriptor ImmediateInEmulation { get; } = new(
        "immediate-in-emulation",
        Severity.Error,
        "`{0} #` would be 16 bits in emulation mode, where both widths are 8",
        "In emulation mode both widths are eight bits, whatever the width bits say, so a sixteen-bit immediate "
            + "could never be written here.");

    internal static DiagnosticDescriptor WidthInEmulation { get; } = new(
        "width-in-emulation",
        Severity.Error,
        "{0} cannot hold in emulation mode, where both widths are 8 bits",
        "Emulation mode forces both widths to eight bits, so a declaration of sixteen cannot hold.");

    internal static DiagnosticDescriptor EnsureNeedsNative { get; } = new(
        "ensure-needs-native",
        Severity.Error,
        "`.ensure {0}` needs native mode, and {1}",
        "An `.ensure` writes a `rep` or a `sep` to make a width hold, and the width bits only have an effect in "
            + "native mode.");

    internal static DiagnosticDescriptor EnsureItemNotAWidth { get; } = new(
        "ensure-item-not-a-width",
        Severity.Error,
        "`.ensure` makes widths hold, and takes `a8`, `a16`, `i8` and `i16`: `{0}` is not one of them",
        "An `.ensure` makes something true by writing the instruction that makes it true, and widths are the only "
            + "part of the state that works for. Everything else is asserted with `.state`.");

    internal static DiagnosticDescriptor StateItemNotAPoint { get; } = new(
        "state-item-not-a-point",
        Severity.Error,
        "`{0}` describes a routine rather than a point in it, and belongs in a signature",
        "A `.state` says what is true at one point. What a routine takes, keeps or returns describes the routine, "
            + "and belongs in its signature.");

    internal static DiagnosticDescriptor StateOutsideARoutine { get; } = new(
        "state-outside-a-routine",
        Severity.Error,
        "`{0}` describes a point in a routine, and this is outside any `.proc`",
        "A `.state` and an `.ensure` describe a point in code, and code is inside a `.proc`.");

    internal static DiagnosticDescriptor StateModeMismatch { get; } = new(
        "state-mode-mismatch",
        Severity.Error,
        "`.state {0}`, and the processor is in {1} mode here",
        "The `.state` declares which mode the processor is in, and the analysis finds the other one on a path "
            + "that reaches here.");

    internal static DiagnosticDescriptor StateWidthMismatch { get; } = new(
        "state-width-mismatch",
        Severity.Error,
        "`.state {0}`, and {1} {2} {3} here",
        "The `.state` declares a register width, and the analysis finds another on a path that reaches here.");

    internal static DiagnosticDescriptor StateValueMismatch { get; } = new(
        "state-value-mismatch",
        Severity.Error,
        "`.state {0}`, and {1} is {2} here",
        "The `.state` declares what the direct page or the data bank is, and the analysis finds another value on "
            + "a path that reaches here.");

    internal static DiagnosticDescriptor StateValueNotConstant { get; } = new(
        "state-value-not-constant",
        Severity.Error,
        "`.state {0}` needs a constant: the analysis follows {1} by value",
        "The analysis follows the direct page and the data bank by value, so a declaration of one is a number "
            + "known while nt65 builds.");

    internal static DiagnosticDescriptor StateValueOutOfRange { get; } = new(
        "state-value-out-of-range",
        Severity.Error,
        "`.state {0}` is out of range: {1}",
        "The direct page is a sixteen-bit address and the data bank is one byte.");

    internal static DiagnosticDescriptor CallStateMismatch { get; } = new(
        "call-state-mismatch",
        Severity.Error,
        "{0} needs `{1}`, and {2}",
        "The callee's signature says what it needs on entry, and the analysis finds something else here. That is "
            + "what a signature is for: the caller is checked against it, not against the body.");

    internal static DiagnosticDescriptor ReturnStateMismatch { get; } = new(
        "return-state-mismatch",
        Severity.Error,
        "{0}`{1}` returns {2}, and {3}",
        "The callee's signature says what is true when it returns, and the analysis finds something else on the "
            + "line after the call.");

    internal static DiagnosticDescriptor AssertedItemNotRestored { get; } = new(
        "asserted-item-not-restored",
        Severity.Error,
        "{0}`{1}` says `{2}`, so {3} must be {4}, and {5} it may not be",
        "A `*` on a signature item says the routine leaves that part of the state as it found it, whatever it "
            + "was. On this path it does not.");

    internal static DiagnosticDescriptor CallTargetUnknown { get; } = new(
        "call-target-unknown",
        Severity.Error,
        "`{0}` needs a routine to call: on the 65816 a call's target is a proc, an extern proc or a `proc(...)` import, whose signature says what state it takes",
        "On the 65816 a call is checked against the signature of what it calls, so what it calls has to be "
            + "something with one.");

    internal static DiagnosticDescriptor CallTargetNotARoutine { get; } = new(
        "call-target-not-a-routine",
        Severity.Error,
        "`{0}` is not a routine: on the 65816 a call's target is a proc, an extern proc or a `proc(...)` import, whose signature says what state it takes",
        "What is named has no signature, so there is nothing to check the call against.");

    internal static DiagnosticDescriptor CallDistanceMismatch { get; } = new(
        "call-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, and is called with `{2}`",
        "`jsr` pushes two bytes and `jsl` three, and the routine returns with whichever matches. Mixing them "
            + "unbalances the stack.");

    internal static DiagnosticDescriptor ReturnDistanceMismatch { get; } = new(
        "return-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}, and returns with `{2}`",
        "A far routine is called with `jsl` and returns with `rtl`; a near one with `jsr` and `rts`. The "
            + "routine's own declaration says which it is.");

    internal static DiagnosticDescriptor JumpDistanceMismatch { get; } = new(
        "jump-distance-mismatch",
        Severity.Error,
        "`{0}` is {1}: a jump to it is `{2} {3}`",
        "`jmp` stays in the bank and `jml` names one. Which is right follows from where the target is.");

    internal static DiagnosticDescriptor JumpAcrossBanks { get; } = new(
        "jump-across-banks",
        Severity.Error,
        "`{0}` is near and in another bank, and would return with `rts` in its own bank to `{1}`'s caller: only a routine that never returns, or an interrupt handler, enters another bank this way",
        "The jump leaves the bank, and the target returns with `rts`, which comes back inside its own bank. Only "
            + "a routine that never returns may cross this way.");

    internal static DiagnosticDescriptor JumpLeavesBank { get; } = new(
        "jump-leaves-bank",
        Severity.Error,
        "`{0}` stays in bank {1}, and `{2}` is in \"{3}\", in bank {4}: {5}",
        "The instruction keeps the bank it is in, and the target is in another one.");

    internal static DiagnosticDescriptor RelativeCallNeedsPhk { get; } = new(
        "relative-call-needs-phk",
        Severity.Error,
        "`{0}` is far, and a relative call to it pushes the bank with `phk` before the `per`",
        "A `per` pushes a sixteen-bit return address. A far routine returns with `rtl`, which pulls a bank as "
            + "well, so the bank is pushed first.");

    internal static DiagnosticDescriptor RelativeCallExtraPhk { get; } = new(
        "relative-call-extra-phk",
        Severity.Error,
        "`{0}` is near, and a relative call to it pushes no bank: the `phk` is one byte too many",
        "A near routine returns with `rts`, which pulls two bytes. The pushed bank would be left on the stack.");

    internal static DiagnosticDescriptor ArgsNotPushed { get; } = new(
        "args-not-pushed",
        Severity.Error,
        "`{0}` takes `args {1}`, pushed before the call, and {2}",
        "The routine's signature says how many bytes the caller pushes before the call, and it pulls exactly that "
            + "many.");

    internal static DiagnosticDescriptor DirectPageMismatch { get; } = new(
        "direct-page-mismatch",
        Severity.Error,
        "`{0}` is in \"{1}\", which is reached through the direct page at {2}, and D is {3} here",
        "The segment says which direct page its bytes are reached through, and D holds another value here. The "
            + "one-byte operand would read somewhere else entirely.");

    internal static DiagnosticDescriptor DirectPageUnknown { get; } = new(
        "direct-page-unknown",
        Severity.Error,
        "{0}, and {1}",
        "A direct-page operand is an offset from D, so D has to be known for the line to mean anything fixed. A "
            + "`.state dp = ...` says what it is.");

    internal static DiagnosticDescriptor DirectPageOutOfReach { get; } = new(
        "direct-page-out-of-reach",
        Severity.Error,
        "{0} at {1}, which reaches only {2} to {3}",
        "The direct page is 256 bytes from D, and the address is outside it.");

    internal static DiagnosticDescriptor BankMismatch { get; } = new(
        "bank-mismatch",
        Severity.Error,
        "`{0}` is in \"{1}\", which is {2}, and B is {3} here",
        "The segment says which bank its bytes are in, and B holds another here, so an absolute operand would "
            + "read the same offset in the wrong bank.");

    internal static DiagnosticDescriptor RangeBankMismatch { get; } = new(
        "range-bank-mismatch",
        Severity.Error,
        "{0} is reached only from banks {1}, and B is {2} here",
        "The project says which banks an absolute address in this range is reachable from, for hardware that is "
            + "mirrored in some banks only.");

    internal static DiagnosticDescriptor MirrorBankMismatch { get; } = new(
        "mirror-bank-mismatch",
        Severity.Error,
        "`{0}` is in \"{1}\", {2}, and this reaches it in bank {3}",
        "The segment is mirrored in some banks, and this reaches it from one it is not in.");

    internal static DiagnosticDescriptor FrameNotARecord { get; } = new(
        "frame-not-a-record",
        Severity.Error,
        "`.frame {0}` is laid out as a struct or a union, whose size says how many bytes it names",
        "A `.frame` lays the top of the stack out as a struct or a union, whose size says how many bytes it "
            + "covers.");

    internal static DiagnosticDescriptor FramePastTheStack { get; } = new(
        "frame-past-the-stack",
        Severity.Error,
        "`{0}` is {1} bytes, and only {2} are pushed here",
        "The frame covers more bytes than this path has pushed, so its members would name what is under the stack "
            + "pointer.");

    internal static DiagnosticDescriptor FrameDepthUnknown { get; } = new(
        "frame-depth-unknown",
        Severity.Error,
        "`{0}` is counted from the stack pointer, and how much is pushed is not known here",
        "A frame member is an offset from the stack pointer, so how much is pushed has to be known.");

    internal static DiagnosticDescriptor FrameGone { get; } = new(
        "frame-gone",
        Severity.Error,
        "`{0}` is in `{1}`, which is no longer on the stack here",
        "The bytes the frame was laid out over have been pulled, so its members name nothing.");

    internal static DiagnosticDescriptor FrameMemberNotStackRelative { get; } = new(
        "frame-member-not-stack-relative",
        Severity.Error,
        "`{0}` is a place on the stack, and is named only on its own as a stack-relative operand: `{1},s`",
        "A frame member is a place on the stack, not an address: it is reached with `,s` and nothing else.");

    // Output: names that meet in the ca65, and the C header.

    internal static DiagnosticDescriptor OutputNameCollision { get; } = new(
        "output-name-collision",
        Severity.Error,
        "`{0}` and {1} both become `{2}` in the output",
        "Two nt65 names become one ca65 symbol, so the output would define it twice. The generated spellings are "
            + "nt65's, and a name that collides is renamed.");

    internal static DiagnosticDescriptor CHeaderUntyped { get; } = new(
        "c-header-untyped",
        Severity.Warning,
        "`{0}` holds `{1}`, which is not exported, so the C header declares it as bytes",
        "The C header declares what the program exports. A type it names that is not exported has no C "
            + "declaration to refer to, so the name is declared as bytes.");

    internal static DiagnosticDescriptor CHeaderNameLeftOut { get; } = new(
        "c-header-name-left-out",
        Severity.Warning,
        "`{0}` is exported to the linker as `{1}`, which C cannot name, so the C header leaves the {2} out: cc65 puts `_` before a C name, so export it `as \"_{3}\"`",
        "cc65 puts an underscore before a C name, so an exported symbol is reachable from C only when its linker "
            + "name starts with one.");

    internal static DiagnosticDescriptor ExportNameTaken { get; } = new(
        "export-name-taken",
        Severity.Error,
        "`{0}` and `{1}` are both exported to the linker as `{2}`",
        "Exports share one flat namespace with everything else the linker sees, so two exports may not reach it "
            + "under one name. `as` gives one of them a name of its own.");

    /// <summary>
    /// The one entry nothing in the compiler reports: an editor marks the lines the build
    /// leaves out, and nobody else has anything to say about them.
    /// </summary>
    public static DiagnosticDescriptor OmittedBranch { get; } = new(
        "omitted-branch",
        Severity.Info,
        "the build configuration leaves this branch out",
        "The build configuration does not take this branch, so its lines are parsed and nothing else. The editor "
            + "shows them faded.");

    internal static DiagnosticDescriptor CannotBeWritten { get; } = new(
        "cannot-be-written",
        Severity.Error,
        "`{0}` cannot be written out, and nothing said why: this is a bug in nt65",
        "Every construct the analysis accepts has a spelling in ca65. Reaching this means one does not, and "
            + "nothing said so earlier: it is a bug in nt65 rather than in the program.");

    // The project file: nt65.json and the command line that adds to it.

    internal static DiagnosticDescriptor ProjectJsonInvalid { get; } = new(
        "project-json-invalid",
        Severity.Error,
        "{0}",
        "The file is not JSON. Comments and trailing commas are allowed; everything else is JSON as it is written "
            + "everywhere.");

    internal static DiagnosticDescriptor ProjectNotAnObject { get; } = new(
        "project-not-an-object",
        Severity.Error,
        "{0} holds one object",
        "The project file is one object, whose keys say what the program is built from and how.");

    internal static DiagnosticDescriptor ProjectKeyUnknown { get; } = new(
        "project-key-unknown",
        Severity.Error,
        "`{0}` is not a {1} key{2}",
        "The keys are a fixed set, so a typo is a mistake rather than a setting that quietly does nothing. "
            + "`$schema` is accepted and read for nothing.");

    internal static DiagnosticDescriptor ProjectCpuUnknown { get; } = new(
        "project-cpu-unknown",
        Severity.Error,
        "`{0}` is not a processor nt65 knows: {1}",
        "The processors nt65 knows are a fixed set, spelled as the project file spells them.");

    internal static DiagnosticDescriptor ProjectNotAList { get; } = new(
        "project-not-a-list",
        Severity.Error,
        "`{0}` is a list of strings",
        "The key takes a list of strings: the globs that name a program's sources, each written from the "
            + "project root.");

    internal static DiagnosticDescriptor ProjectNotAString { get; } = new(
        "project-not-a-string",
        Severity.Error,
        "`{0}` is a string",
        "The key takes a string, such as the directory the build writes its output into.");

    internal static DiagnosticDescriptor ProjectValueNotAnObject { get; } = new(
        "project-value-not-an-object",
        Severity.Error,
        "`{0}` is an object",
        "The key takes an object, whose own keys are what it is a table of.");

    internal static DiagnosticDescriptor DefineNameInvalid { get; } = new(
        "define-name-invalid",
        Severity.Error,
        "`{0}` is not a name",
        "A define is named as a constant is, or with a module's path in front of it to set that module's "
            + "`.config`.");

    internal static DiagnosticDescriptor DefineNotANumber { get; } = new(
        "define-not-a-number",
        Severity.Error,
        "`{0}` is not a number, and a define is a number",
        "A define is a constant visible in every file, and constants are numbers. A JSON number or a string in "
            + "nt65 number syntax both read.");

    internal static DiagnosticDescriptor ConfigurationNameInvalid { get; } = new(
        "configuration-name-invalid",
        Severity.Error,
        "`{0}` is not a configuration name: one is letters, digits, `_` and `-`",
        "A configuration is chosen by name on the command line, so its name is written without quoting.");

    internal static DiagnosticDescriptor ConfigurationNotAnObject { get; } = new(
        "configuration-not-an-object",
        Severity.Error,
        "configuration `{0}` is an object with `defines`, `diagnostics` and `out`",
        "A configuration gives defines over the project's and an output directory in place of it.");

    internal static DiagnosticDescriptor ConfigurationKeyUnknown { get; } = new(
        "configuration-key-unknown",
        Severity.Error,
        "configuration `{0}`: `{1}` is not a configuration key: a configuration has `defines`, `diagnostics` and "
            + "`out`",
        "What a named configuration may change is a fixed, small set: the defines it gives over the "
            + "project's, and where its output goes.");

    internal static DiagnosticDescriptor ConfigurationUnknown { get; } = new(
        "configuration-unknown",
        Severity.Error,
        "`{0}` is not a configuration: {1}",
        "The name given to `--config` is not one the project file declares. The project's own settings build.");

    internal static DiagnosticDescriptor ProjectSegmentNotAnObject { get; } = new(
        "project-segment-not-an-object",
        Severity.Error,
        "segment \"{0}\" is an object with a `size`",
        "A segment is an object saying how wide an address in it is, and where it sits.");

    internal static DiagnosticDescriptor ProjectSegmentSizeMissing { get; } = new(
        "project-segment-size-missing",
        Severity.Error,
        "segment \"{0}\" needs a `size` of \"zp\", \"abs\" or \"far\"",
        "How wide an address in a segment is decides how every reference to what is in it is written, so every "
            + "segment says it.");

    internal static DiagnosticDescriptor ProjectSegmentKeyUnknown { get; } = new(
        "project-segment-key-unknown",
        Severity.Error,
        "segment \"{0}\": `{1}` is not a segment key: a segment has a `size`, a `dp`, a `bank` and `mirrors`",
        "What a segment may say is a fixed set: how wide an address in it is, and where it sits.");

    internal static DiagnosticDescriptor DiagnosticNameUnknown { get; } = new(
        "diagnostic-name-unknown",
        Severity.Error,
        "`{0}` is not a diagnostic nt65 reports{1}",
        "The names are a fixed set, so a typo is a mistake rather than a line that quietly does nothing. "
            + "`nt65 explain` lists them.");

    internal static DiagnosticDescriptor DiagnosticSeverityUnknown { get; } = new(
        "diagnostic-severity-unknown",
        Severity.Error,
        "`{0}` is reported as \"off\", \"warning\" or \"error\"",
        "A project says how much a diagnostic matters to it, and there are three answers.");

    internal static DiagnosticDescriptor DiagnosticNotTurnedDown { get; } = new(
        "diagnostic-not-turned-down",
        Severity.Error,
        "`{0}` is an error, and a project may not turn an error down",
        "A warning is a matter of taste and an error is a program nt65 refuses to translate. Turning one down "
            + "would not make the output right; it would only stop nt65 saying so.");

    internal static DiagnosticDescriptor RangeInvalid { get; } = new(
        "range-invalid",
        Severity.Error,
        "`{0}` is not a range of absolute addresses, such as \"$2100-$21ff\"",
        "A range key is one absolute address, or two with a `-` between them.");

    internal static DiagnosticDescriptor RangesOverlap { get; } = new(
        "ranges-overlap",
        Severity.Error,
        "`{0}` overlaps `{1}-{2}`: an address is in one range at most",
        "Which banks an address is reachable from is one answer, so the ranges do not overlap.");

    internal static DiagnosticDescriptor BanksNotAList { get; } = new(
        "banks-not-a-list",
        Severity.Error,
        "`{0}` is a list of banks, such as [\"$00-$3f\", \"$80-$bf\"]",
        "Banks are written as a list, each item one bank or a range of them.");

    internal static DiagnosticDescriptor BankInvalid { get; } = new(
        "bank-invalid",
        Severity.Error,
        "`{0}`: {1} is not a bank or a range of banks",
        "A bank is a byte, and a range of banks is two with a `-` between them.");

    // Signatures: what a routine or a macro declares about itself, and what a set may hold.

    internal static DiagnosticDescriptor SignatureSetSelfReference { get; } = new(
        "signature-set-self-reference",
        Severity.Error,
        "`{0}` names `{1}`, which stands for `{2}` again: a signature set cannot stand for itself",
        "A signature set stands for the items it names, so naming itself would stand for itself.");

    internal static DiagnosticDescriptor AliasDistanceMismatch { get; } = new(
        "alias-distance-mismatch",
        Severity.Error,
        "`{0}` is declared {1}, and `{2}` is {3}",
        "Another name for a routine is reached the same way the routine is, because it is the same bytes.");

    internal static DiagnosticDescriptor AliasSignatureMismatch { get; } = new(
        "alias-signature-mismatch",
        Severity.Error,
        "`{0}` is declared `{1}`, and `{2}` is `{3}`: another name for a routine declares what the routine does",
        "Callers are checked against the signature on the name they write, so two names for one routine that "
            + "declare different things would check the same code two ways.");

    internal static DiagnosticDescriptor ArgsNotConstant { get; } = new(
        "args-not-constant",
        Severity.Error,
        "`{0}` needs a constant: it counts the bytes the caller pushes",
        "How many bytes the caller pushes decides where the routine finds them, so it is a number known while "
            + "nt65 builds.");

    internal static DiagnosticDescriptor ArgsOutOfRange { get; } = new(
        "args-out-of-range",
        Severity.Error,
        "`{0}` is out of range: it counts the bytes the caller pushes",
        "The bytes a caller pushes are on the stack, which is 256 bytes deep on the 6502 and one bank on the "
            + "65816.");

    internal static DiagnosticDescriptor NoreturnDeclaresAnExit { get; } = new(
        "noreturn-declares-an-exit",
        Severity.Error,
        "a routine that says `noreturn` never returns, and declares nothing after `->`",
        "What a routine leaves is what a caller reads after the call, and there is no after.");

    internal static DiagnosticDescriptor HandlerAssumesState { get; } = new(
        "handler-assumes-state",
        Severity.Error,
        "`{0}`: an interrupt handler is entered from anywhere, and assumes nothing but its mode: `interrupt, native` or `interrupt, emu`",
        "An interrupt arrives between any two instructions, so nothing about the state at the handler is the "
            + "caller's to promise. The mode is the one thing the processor itself decides.");

    internal static DiagnosticDescriptor HandlerDistance { get; } = new(
        "handler-distance",
        Severity.Error,
        "`{0}` describes how a routine is called, and an interrupt handler is entered by the processor, which makes it neither near nor far",
        "Near and far say how a routine is called and returned from. The processor enters a handler and `rti` "
            + "leaves it, which is neither.");

    internal static DiagnosticDescriptor HandlerNoreturn { get; } = new(
        "handler-noreturn",
        Severity.Error,
        "`{0}`: an interrupt handler leaves by `rti`, and never returns to a caller in any case",
        "`noreturn` is about a routine that never comes back to its caller, and a handler has none.");

    internal static DiagnosticDescriptor HandlerKeeps { get; } = new(
        "handler-keeps",
        Severity.Error,
        "`{0}`: an interrupt handler says which mode it is entered in, or nothing",
        "There is no caller for the handler to hand anything back to: the processor entered it, and `rti` "
            + "leaves it.");

    internal static DiagnosticDescriptor HandlerDeclaresAnExit { get; } = new(
        "handler-declares-an-exit",
        Severity.Error,
        "an interrupt handler leaves by `rti`, and declares nothing after `->`",
        "The processor restores what it saved, and there is no caller to read anything after the handler.");

    internal static DiagnosticDescriptor SignatureValueNotConstant { get; } = new(
        "signature-value-not-constant",
        Severity.Error,
        "`{0}` needs a constant: the analysis follows D and B by value",
        "The analysis follows the direct page and the data bank by value, and checks every call against them, so "
            + "a signature that names one names a number.");

    internal static DiagnosticDescriptor SignatureValueOutOfRange { get; } = new(
        "signature-value-out-of-range",
        Severity.Error,
        "`{0}` is out of range: {1}",
        "The direct page is a sixteen-bit address and the data bank is one byte.");

    internal static DiagnosticDescriptor SignatureSetNotFirst { get; } = new(
        "signature-set-not-first",
        Severity.Error,
        "`{0}` is a signature set, and comes first in its list: the items after it change what it gives",
        "A set stands for its items, and the items written after it change what it gives, so the set comes first "
            + "and the rest read as changes to it.");

    internal static DiagnosticDescriptor SignatureSetNotASet { get; } = new(
        "signature-set-not-a-set",
        Severity.Error,
        "`{0}` is {1}, and a name among a signature's items is a signature set",
        "A bare name among a signature’s items is a `.signature` set; anything else in a signature is written as "
            + "an item.");

    internal static DiagnosticDescriptor MacroKeeps { get; } = new(
        "macro-keeps",
        Severity.Error,
        "`{0}` is about a routine from entry to exit, and a macro is expanded into one: what its body keeps is part of what that routine keeps",
        "A macro is expanded into a routine, so what its body keeps is part of what that routine keeps, and the "
            + "routine is what declares it.");

    internal static DiagnosticDescriptor MacroNoreturn { get; } = new(
        "macro-noreturn",
        Severity.Error,
        "`noreturn` says a routine never returns, and a macro is expanded, ending where its body does",
        "A macro is expanded where it is called, and what follows the expansion is the routine’s.");

    internal static DiagnosticDescriptor MacroDistance { get; } = new(
        "macro-distance",
        Severity.Error,
        "`{0}` describes how a routine is called, and a macro is expanded",
        "A macro is not called and does not return: it is written out where it stands.");

    internal static DiagnosticDescriptor DistanceDisagrees { get; } = new(
        "distance-disagrees",
        Severity.Error,
        "`{0}` disagrees with `{1}`: a routine is called one way",
        "A routine is reached one way, so its signature says near or far once.");

    internal static DiagnosticDescriptor SignatureItemTwice { get; } = new(
        "signature-item-twice",
        Severity.Error,
        "`{0}` and `{1}` both describe the same part of the state",
        "Each part of the processor state is declared once, so what the signature says does not depend on the "
            + "order its items are read in.");

    internal static DiagnosticDescriptor UnchangedNeedsEntry { get; } = new(
        "unchanged-needs-entry",
        Severity.Error,
        "`{0}` after `->` needs `{1}` at entry too: a routine hands back unchanged only what it assumed nothing about",
        "A `*` after the arrow says the routine hands the part back as it found it, which is a promise only a "
            + "routine that assumed nothing about it can keep.");

    internal static DiagnosticDescriptor ItemBelongsAtEntry { get; } = new(
        "item-belongs-at-entry",
        Severity.Error,
        "`{0}` {1}, and belongs before `->`",
        "What a routine is and how it is called are true of it from entry to exit, so they are written once, "
            + "before the arrow. What comes after the arrow is what the routine leaves.");

    // Read off the class rather than listed again, so that an entry added above is in it. It
    // is worked out on first use rather than with the entries: reflecting on a type while its
    // own initializer is still running is a way to deadlock two threads that both ask at once.
    private static readonly Lazy<IReadOnlyList<DiagnosticDescriptor>> all = new(() =>
        [.. typeof(Catalogue).GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(DiagnosticDescriptor))
            .Select(property => (DiagnosticDescriptor)property.GetValue(null)!)
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)]);

    /// <summary>Every descriptor, in name order.</summary>
    public static IReadOnlyList<DiagnosticDescriptor> All => all.Value;

    /// <summary>The descriptor named <paramref name="id"/>, or null for a name nt65 has none for.</summary>
    public static DiagnosticDescriptor? Find(string id) =>
        All.FirstOrDefault(descriptor => descriptor.Id == id);
}
