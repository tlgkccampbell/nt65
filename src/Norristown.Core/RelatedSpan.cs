namespace Norristown;

/// <summary>A second place a diagnostic points at, such as the first of two declarations.</summary>
/// <param name="Span">Where it is.</param>
/// <param name="Message">What it has to do with the diagnostic.</param>
public sealed record RelatedSpan(Span Span, string Message);
