namespace Norristown;

/// <summary>
/// What one diagnostic says, and the catalogue entry it says it under. A reporting site names a
/// descriptor and hands it the pieces of the sentence; a descriptor whose sentence has no holes
/// stands for its own message, which is what the conversion is for.
/// </summary>
/// <param name="Descriptor">The catalogue entry.</param>
/// <param name="Text">The message, with this site's pieces in it.</param>
public readonly record struct DiagnosticMessage(DiagnosticDescriptor Descriptor, string Text)
{
    /// <summary>The message of a descriptor whose sentence takes no pieces.</summary>
    public static implicit operator DiagnosticMessage(DiagnosticDescriptor descriptor) => descriptor.Says();

    /// <summary>The same, for a caller that would rather name the conversion.</summary>
    public static DiagnosticMessage FromDescriptor(DiagnosticDescriptor descriptor) => descriptor;
}
