namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents one call to the routine at the caret, as an <c>nt65/processor</c> answer offers it.</summary>
/// <param name="Name">The routine that makes the call.</param>
/// <param name="At">The call.</param>
internal sealed record ProcessorCaller(string Name, Location At);
