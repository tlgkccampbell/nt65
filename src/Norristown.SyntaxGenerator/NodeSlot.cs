using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// Represents one property of a node, as the table describes it. The property either reads a
/// slot in the node's fixed layout, or is computed from other properties by the expression given
/// in <see cref="Read"/>.
/// </summary>
/// <param name="Name">The property's name.</param>
/// <param name="Type">The slot's type. A trailing <c>?</c> marks the slot optional.</param>
/// <param name="Summary">The property's summary, one string per line.</param>
/// <param name="Read">The expression a derived property returns, or the empty string for a slot.</param>
/// <param name="Role">Whether the property is a slot of the node or derived from other properties.</param>
/// <param name="Kinds">The kinds a token slot may hold, or empty for a slot that is not a token.</param>
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

    /// <summary>
    /// Gets a value indicating whether the property is a slot of the node rather than derived from
    /// other properties.
    /// </summary>
    public bool IsPiece => Role == SlotRole.Slot;

    /// <summary>
    /// Gets a value indicating whether the slot must hold a node or a token, missing or not, in
    /// every node of its kind.
    /// </summary>
    public bool IsRequired => !Type.EndsWith("?", StringComparison.Ordinal);

    /// <summary>
    /// Gets the kind of list the slot holds, or <see cref="ListShape.None"/> for a single node or
    /// token.
    /// </summary>
    public ListShape List =>
        Type.StartsWith("SyntaxList<", StringComparison.Ordinal) ? ListShape.Nodes
        : Type.StartsWith("SeparatedSyntaxList<", StringComparison.Ordinal) ? ListShape.Separated
        : Type == "SyntaxTokenList" ? ListShape.Tokens
        : ListShape.None;

    /// <summary>
    /// Gets the item type of a node list or separated list slot, or null for any other slot.
    /// </summary>
    public string? ListItemType => List switch
    {
        ListShape.Nodes => Inside(Type, "SyntaxList<"),
        ListShape.Separated => Inside(Type, "SeparatedSyntaxList<"),
        _ => null,
    };

    /// <summary>
    /// Gets a value indicating whether the slot holds a single token rather than a node or a list.
    /// </summary>
    public bool IsToken => Type is "SyntaxToken" or "SyntaxToken?";

    /// <summary>Gets the type without its trailing <c>?</c>.</summary>
    public string BareType => Type.TrimEnd('?');

    /// <summary>
    /// Gets the constructor parameter name for the slot, which is the property's name with its
    /// first letter lowered, prefixed with <c>@</c> when that is a C# keyword.
    /// </summary>
    public string Field
    {
        get
        {
            var name = char.ToLowerInvariant(Name[0]) + Name.Substring(1);
            return Keywords.Contains(name) ? "@" + name : name;
        }
    }

    /// <summary>Gets the parameter's name as a <c>param</c> tag gives it, without the <c>@</c>.</summary>
    public string DocName => Field.TrimStart('@');

    /// <summary>
    /// Returns the text of <paramref name="type"/> between <paramref name="opening"/> and its
    /// closing <c>&gt;</c>.
    /// </summary>
    private static string Inside(string type, string opening) =>
        type.Substring(opening.Length, type.Length - opening.Length - 1);
}
