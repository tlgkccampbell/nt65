namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/directPages</c> request, which says how the program's
/// routines share the zero page, or on the 65816 each direct page.
/// </summary>
/// <param name="Cpu">The processor, as <c>.cpu</c> spells it.</param>
/// <param name="Predicted">Whether some offsets are predicted from the layout rather than fixed by the source.</param>
/// <param name="Pages">The pages by base, then the hardware pages, then the page whose D is not known.</param>
internal sealed record DirectPagesResult(string Cpu, bool Predicted, IReadOnlyList<DirectPageItem> Pages);
