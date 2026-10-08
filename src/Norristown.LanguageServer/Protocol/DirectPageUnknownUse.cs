namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one location a routine names through the direct page while D is not known.</summary>
/// <param name="Name">The location's name.</param>
/// <param name="Home">The name of the page it lives on, or null.</param>
/// <param name="Offset">Its offset on its own page, or null when that is not known.</param>
/// <param name="Role">What the routine does with it.</param>
/// <param name="Accesses">The instructions that name it.</param>
/// <param name="Hazard">Whether the use is a hazard, as it is in an interrupt.</param>
/// <param name="Hazards">
/// The facts that make up the hazard, such as where the access lands on each page the interrupted
/// code holds D at, or none.
/// </param>
internal sealed record DirectPageUnknownUse(
    string Name, string? Home, long? Offset, string Role, IReadOnlyList<DirectPageAccess> Accesses, bool Hazard,
    IReadOnlyList<DirectPageNote> Hazards);
