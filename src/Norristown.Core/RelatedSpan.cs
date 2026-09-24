namespace Norristown;

/// <summary>
/// Represents a second place a diagnostic points at, such as the first of two declarations.
/// </summary>
/// <param name="Span">The span of the place.</param>
/// <param name="Message">A message that says how the place relates to the diagnostic.</param>
public sealed record RelatedSpan(Span Span, string Message);
