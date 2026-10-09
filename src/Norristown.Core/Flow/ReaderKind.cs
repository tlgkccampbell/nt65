namespace Norristown.Flow;

/// <summary>Specifies how a line that <see cref="OutputReaders"/> reports reads an output.</summary>
public enum ReaderKind
{
    /// <summary>An instruction in the routine reads the value.</summary>
    Instruction,

    /// <summary>
    /// An instruction from a macro expansion reads the value. The reader is the outermost macro call
    /// in the caret's file.
    /// </summary>
    Macro,

    /// <summary>A call reads the value, because the routine it calls does.</summary>
    Call,

    /// <summary>
    /// The value leaves the routine here, by a return, a tail call or a run into the next routine.
    /// Whatever the routine hands control back to may read it, so an exit counts as a reader.
    /// </summary>
    Exit,
}
