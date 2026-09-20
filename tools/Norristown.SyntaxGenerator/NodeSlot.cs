using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// One property of a node, as the table writes it: a piece of the node, or a property derived
/// from other ones. <see cref="Form"/> is the table keyword that says how it is read, and
/// <see cref="Read"/> the expression that keyword takes, if any.
/// </summary>
/// <param name="Name">The property's name.</param>
/// <param name="Type">The property's type, whose trailing <c>?</c> is what optional means.</param>
/// <param name="Summary">The property's summary, a line per line of it.</param>
/// <param name="Form">The keyword the table read it with: <c>read</c>, <c>nodes</c> or <c>cache</c>.</param>
/// <param name="Read">The expression the keyword took, or the empty string for <c>nodes</c>.</param>
/// <param name="IsPiece">Whether it reads a piece of the node rather than being derived from others.</param>
public sealed record NodeSlot(
    string Name,
    string Type,
    ImmutableArray<string> Summary,
    string Form,
    string Read,
    bool IsPiece)
{
    /// <summary>Whether the property is optional: today, whether it reads as null when unwritten.</summary>
    public bool IsOptional => Type.EndsWith('?');

    /// <summary>The item type of a list-valued property, or null when it is not one.</summary>
    public string? ItemType =>
        Type.StartsWith("ImmutableArray<", StringComparison.Ordinal) ? Type[15..^1] : null;

    /// <summary>The field a kept property is worked out into, named after the property.</summary>
    public string Field => char.ToLowerInvariant(Name[0]) + Name[1..];
}
