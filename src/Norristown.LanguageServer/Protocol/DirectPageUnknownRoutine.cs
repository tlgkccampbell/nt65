namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine that reaches memory through the direct page while D is not known.</summary>
/// <param name="Name">The routine's name.</param>
/// <param name="Declaration">Where its name is declared.</param>
/// <param name="Handler">Whether it is an interrupt handler.</param>
/// <param name="Interrupt">Whether it runs only in an interrupt.</param>
/// <param name="Uses">The locations it names there.</param>
internal sealed record DirectPageUnknownRoutine(
    string Name, Location Declaration, bool Handler, bool Interrupt, IReadOnlyList<DirectPageUnknownUse> Uses);
