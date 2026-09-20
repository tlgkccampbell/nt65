using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// One property of a node, as the table writes it: either a slot of the node's fixed layout,
/// which the property reads, or a property worked out from other ones, which says what it
/// returns in <see cref="Read"/>.
/// </summary>
/// <param name="Name">The property's name.</param>
/// <param name="Type">The slot's type, whose trailing <c>?</c> is what optional means.</param>
/// <param name="Summary">The property's summary, a line per line of it.</param>
/// <param name="Read">What a derived property returns, or the empty string for a slot.</param>
/// <param name="Role">Whether it is a slot of the node or a property derived from other ones.</param>
/// <param name="Kinds">The kinds a token slot may hold; empty for a slot that is not a token.</param>
public sealed record NodeSlot(
    string Name,
    string Type,
    ImmutableArray<string> Summary,
    string Read,
    SlotRole Role,
    ImmutableArray<string> Kinds)
{
    // The C# keywords a property's name could turn into once its first letter is lowered.
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "base", "bool", "byte", "case", "catch", "char", "class", "const", "default", "delegate",
        "do", "double", "else", "enum", "event", "false", "finally", "fixed", "float", "for",
        "goto", "if", "in", "int", "interface", "is", "lock", "long", "new", "null", "object",
        "operator", "out", "params", "ref", "return", "sealed", "short", "sizeof", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "ushort", "using", "void", "while",
    };

    /// <summary>Whether the property is a piece of the node rather than derived from other ones.</summary>
    public bool IsPiece => Role == SlotRole.Slot;

    /// <summary>Whether the slot must hold a node or a token, missing or not, in every node of its kind.</summary>
    public bool IsRequired => !Type.EndsWith("?", StringComparison.Ordinal);

    /// <summary>What kind of list the slot holds, or <see cref="ListShape.None"/> for a single piece.</summary>
    public ListShape List =>
        Type.StartsWith("SyntaxList<", StringComparison.Ordinal) ? ListShape.Nodes
        : Type.StartsWith("SeparatedSyntaxList<", StringComparison.Ordinal) ? ListShape.Separated
        : Type == "SyntaxTokenList" ? ListShape.Tokens
        : ListShape.None;

    /// <summary>The item type of a list slot, or null when it is not one.</summary>
    public string? ListItemType => List switch
    {
        ListShape.Nodes => Inside(Type, "SyntaxList<"),
        ListShape.Separated => Inside(Type, "SeparatedSyntaxList<"),
        _ => null,
    };

    /// <summary>Whether the slot holds a token rather than a node or a list.</summary>
    public bool IsToken => Type is "SyntaxToken" or "SyntaxToken?";

    /// <summary>The type without its trailing <c>?</c>.</summary>
    public string BareType => Type.TrimEnd('?');

    /// <summary>The constructor parameter a slot arrives as, named after the property.</summary>
    public string Field
    {
        get
        {
            var name = char.ToLowerInvariant(Name[0]) + Name.Substring(1);
            return Keywords.Contains(name) ? "@" + name : name;
        }
    }

    /// <summary>What <paramref name="type"/> holds between <paramref name="opening"/> and its <c>&gt;</c>.</summary>
    private static string Inside(string type, string opening) =>
        type.Substring(opening.Length, type.Length - opening.Length - 1);
}
