namespace Norristown;

/// <summary>Identifies what a <see cref="DiagnosticFix"/> changes.</summary>
public enum FixKind
{
    /// <summary>A <c>.next ?</c> after the statement reported, which says control goes somewhere nt65 is not told about.</summary>
    EndPath,

    /// <summary>
    /// The statement's mnemonic replaced with another of the other distance, such as <c>jsl</c>
    /// for <c>jsr</c> or <c>jmp</c> for <c>jml</c>.
    /// </summary>
    Mnemonic,

    /// <summary>An <c>.export</c> of the name, in the module that declares it.</summary>
    Export,

    /// <summary>A <c>.use</c> of the path, in the file reported.</summary>
    Use,

    /// <summary>A <c>.state</c> after the label, saying what reaches it.</summary>
    State,

    /// <summary>The label reported, and the data under it, as a <c>.data</c> declaration.</summary>
    DataDeclaration,

    /// <summary>The branch reported, replaced with the long branch that reaches any near target.</summary>
    Branch,

    /// <summary>
    /// The return reported, replaced with the instruction its routine must return with, such as
    /// <c>rti</c> in an interrupt handler.
    /// </summary>
    Return,

    /// <summary>A construct in ca65's syntax, rewritten in nt65's syntax.</summary>
    Spelling,

    /// <summary>The name reported, replaced with the declared name it most nearly matches.</summary>
    NearestName,

    /// <summary>
    /// The name reported, for the programmer to rename. The editor puts the caret on the name and
    /// starts a rename, since only the programmer knows what it should be called.
    /// </summary>
    Rename,

    /// <summary>
    /// ca65's assertion level, removed because an assertion that fails in nt65 is always an error.
    /// </summary>
    AssertLevel,

    /// <summary>The <c>.res</c> of a declaration, replaced with the storage it reserves.</summary>
    Storage,

    /// <summary>The label in mixed data, replaced with a member of the data or a position in it.</summary>
    DataMember,

    /// <summary>The address size an <c>.export</c> gives, widened to the one the declaration has.</summary>
    ExportSize,

    /// <summary>
    /// The expression reported, with parentheses added. There is one fix for each way the
    /// expression can be read.
    /// </summary>
    Parentheses,

    /// <summary>
    /// An <c>.ensure</c> before the immediate reported. There is one fix for each width the
    /// immediate may have.
    /// </summary>
    Width,

    /// <summary>The width item added to the signature of the routine that contains the immediate.</summary>
    Signature,

    /// <summary>The declaration nothing names, removed, or exported so that another module may name it.</summary>
    Unused,

    /// <summary>The <c>.use</c> item that imports a name nothing refers to, removed.</summary>
    UseItem,

    /// <summary>
    /// The bracket missing from the line, inserted where the syntax tree has a missing token for
    /// it. The bracket is either the <c>{</c> a block needs, or the <c>)</c>, <c>]</c> or
    /// <c>}</c> that closes an open bracket.
    /// </summary>
    MissingPiece,

    /// <summary>The declaration of the module a <c>.place</c> names, marked <c>placed</c>.</summary>
    Placed,

    /// <summary>The <c>.const</c> a constant is declared with, inserted before its name.</summary>
    Const,

    /// <summary>
    /// A <c>.fallthrough</c> naming the routine that comes next, as the last line of the body the
    /// fix's <see cref="DiagnosticFix.At"/> closes.
    /// </summary>
    Fallthrough,

    /// <summary>
    /// A <c>.next</c> naming the branch's own target after the conditional branch at the fix's
    /// <see cref="DiagnosticFix.At"/>, stating that the branch is always taken.
    /// </summary>
    AlwaysTaken,

    /// <summary>
    /// The decimal address reported, given the <c>#</c> that makes it a number, or written in
    /// hexadecimal as the address the fix's <see cref="DiagnosticFix.Text"/> spells. Both fixes
    /// are offered.
    /// </summary>
    Immediate,

    /// <summary>
    /// The call reported, made a jump with the mnemonic the fix's <see cref="DiagnosticFix.Text"/>
    /// names. The return after it, at the fix's <see cref="DiagnosticFix.At"/>, is removed where
    /// there is one to remove.
    /// </summary>
    TailCall,

    /// <summary>The statement reported, removed because it changes nothing.</summary>
    Redundant,

    /// <summary>
    /// The operand of the <c>rep</c> or <c>sep</c> reported, narrowed to the flags the fix's
    /// <see cref="DiagnosticFix.Text"/> spells.
    /// </summary>
    Flags,

    /// <summary>
    /// The branch reported, replaced by the instruction the fix's <see cref="DiagnosticFix.Text"/>
    /// spells, with the <c>jmp</c> it branched over, at the fix's <see cref="DiagnosticFix.At"/>,
    /// removed. The label the branch went to goes too where nothing else names it.
    /// </summary>
    BranchOver,

    /// <summary>
    /// The operand reported, replaced by the one the fix's <see cref="DiagnosticFix.Text"/>
    /// spells, with the <c>clc</c> or <c>sec</c> before it, at the fix's
    /// <see cref="DiagnosticFix.At"/>, removed.
    /// </summary>
    CarryFolded,

    /// <summary>The register added to the <c>reads</c> item of the routine's signature.</summary>
    Reads,

    /// <summary>
    /// The register the fix's <see cref="DiagnosticFix.Text"/> names, added to the <c>keeps</c>
    /// item of the signature of the routine declared at the fix's <see cref="DiagnosticFix.At"/>.
    /// </summary>
    Keeps,

    /// <summary>
    /// The register reported, saved on the stack before the call with the push the fix's
    /// <see cref="DiagnosticFix.Text"/> names, and restored after it with the matching pull.
    /// </summary>
    SaveAround,

    /// <summary>
    /// The constant reported, declared instead as data found elsewhere at its value, with
    /// <c>.data</c> or, for a hardware register, <c>.mmio</c>.
    /// </summary>
    AddressData,

    /// <summary>
    /// The <c>phk</c> of the relative call reported, inserted or removed. Where the fix's
    /// <see cref="DiagnosticFix.Text"/> is <c>phk</c>, it is inserted before the call's <c>per</c>
    /// at <see cref="DiagnosticFix.At"/>. Otherwise the <c>phk</c> at <see cref="DiagnosticFix.At"/>
    /// is removed.
    /// </summary>
    BankPush,

    /// <summary>
    /// The item reported, removed from its list. Where the fix's <see cref="DiagnosticFix.Text"/>
    /// names a register, only that register is removed from the item. An item, list or directive
    /// left with nothing in it goes too.
    /// </summary>
    Item,

    /// <summary>The item reported, moved from the exit of a signature to the end of its entry.</summary>
    ToEntry,

    /// <summary>
    /// The segment block reported, removed along with its closing brace. Its contents stay where
    /// they are, one level less indented.
    /// </summary>
    SegmentBlock,

    /// <summary>
    /// The branch reported, replaced with a <c>jml</c> to its target. Where the fix's
    /// <see cref="DiagnosticFix.Text"/> names the opposite branch, that branch skips over the
    /// <c>jml</c>. An unconditional branch becomes the <c>jml</c> alone.
    /// </summary>
    FarBranch,

    /// <summary>
    /// An <c>.ensure</c> of the width or flag item the fix's <see cref="DiagnosticFix.Text"/> names,
    /// such as <c>a16</c> or <c>c = 0</c>, inserted before the statement reported.
    /// </summary>
    Ensure,

    /// <summary>
    /// The item the fix's <see cref="DiagnosticFix.Text"/> gives, declared in the exit of the
    /// routine at <see cref="DiagnosticFix.At"/>. It replaces the exit item for the same part, or
    /// is added where the exit has none.
    /// </summary>
    Exit,

    /// <summary>
    /// The <c>.state</c> item reported, replaced with the fix's <see cref="DiagnosticFix.Text"/>,
    /// which states what the analysis finds there.
    /// </summary>
    StateItem,

    /// <summary>
    /// The instruction reported, replaced with the one the fix's <see cref="DiagnosticFix.Text"/>
    /// spells, such as <c>tax</c> in place of <c>ldx #0</c>.
    /// </summary>
    Instruction,
}
