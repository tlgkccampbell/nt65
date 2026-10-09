namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Represents the answer to an <c>nt65/directPages</c> request, which says how the program's
/// routines share its data, on the zero page or on the 65816 each direct page, and in every other segment.
/// </summary>
/// <param name="Cpu">The processor, as <c>.cpu</c> spells it.</param>
/// <param name="Pages">The pages by base, then the hardware pages, then the page whose D is not known.</param>
/// <param name="Segments">The data on no page, by segment, then the fixed addresses, then the hardware registers.</param>
/// <param name="Build">The build whose debug file gave the built addresses, or null when there is none.</param>
internal sealed record DirectPagesResult(string Cpu, IReadOnlyList<DirectPageItem> Pages, IReadOnlyList<DirectPageSegment> Segments, DirectPageBuild? Build);
