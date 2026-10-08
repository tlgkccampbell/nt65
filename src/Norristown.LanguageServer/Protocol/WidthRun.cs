namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents lines of a document on which the register widths and the mode are the same.</summary>
/// <param name="First">The first line of the run.</param>
/// <param name="Last">The last line of the run.</param>
/// <param name="A">The width of the accumulator in bits, 8 or 16, or null where it is not known.</param>
/// <param name="Index">The width of X and Y in bits, 8 or 16, or null where it is not known.</param>
/// <param name="Emulation">
/// Whether the processor is in emulation mode, where both widths are pinned at 8 bits. The client
/// then draws the mode rather than the widths.
/// </param>
internal sealed record WidthRun(int First, int Last, int? A, int? Index, bool Emulation);
