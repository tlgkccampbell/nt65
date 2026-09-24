namespace Norristown;

/// <summary>
/// Represents the text of one diagnostic and the catalogue entry it is reported under. A reporting
/// site names a descriptor and passes it the arguments for its format. A descriptor whose format
/// has no placeholders converts implicitly to its message, so a site can pass the descriptor itself.
/// </summary>
/// <param name="Descriptor">The catalogue entry.</param>
/// <param name="Text">The message, with this site's arguments filled in.</param>
public readonly record struct DiagnosticMessage(DiagnosticDescriptor Descriptor, string Text)
{
    /// <summary>Converts a descriptor whose format takes no arguments to its message.</summary>
    public static implicit operator DiagnosticMessage(DiagnosticDescriptor descriptor) => descriptor.Message();

    /// <summary>
    /// Converts a descriptor whose format takes no arguments to its message, for a caller that
    /// would rather name the conversion.
    /// </summary>
    public static DiagnosticMessage FromDescriptor(DiagnosticDescriptor descriptor) => descriptor;
}
