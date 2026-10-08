namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine that reaches memory through the direct page while D is not known.</summary>
/// <param name="Name">The routine's name.</param>
/// <param name="Declaration">Where its name is declared.</param>
/// <param name="Handler">Whether it is an interrupt handler.</param>
/// <param name="Interrupt">
/// Whether it runs in an interrupt, as a handler or a routine a handler reaches. It may run outside
/// one as well.
/// </param>
/// <param name="Main">Whether it runs outside interrupts, reached from where the rest of the program starts.</param>
/// <param name="Uses">The locations it names there.</param>
internal sealed record DirectPageUnknownRoutine(
    string Name, Location Declaration, bool Handler, bool Interrupt, bool Main, IReadOnlyList<DirectPageUnknownUse> Uses);
