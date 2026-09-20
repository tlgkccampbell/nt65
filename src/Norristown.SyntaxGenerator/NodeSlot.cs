using System.Collections.Immutable;

namespace Norristown.SyntaxGenerator;

/// <summary>
/// One property of a node, as the table writes it: a slot of the node's fixed layout, a
/// property derived from other ones, or a property that only exists until the kind is
/// converted. <see cref="Form"/> is the table keyword that says how it is read today, and
/// <see cref="Read"/> the expression that keyword takes, if any.
/// </summary>
/// <param name="Name">The property's name.</param>
/// <param name="Type">The slot's target type, whose trailing <c>?</c> is what optional means.</param>
/// <param name="Summary">The property's summary, a line per line of it.</param>
/// <param name="Form">The keyword the table read it with: <c>read</c>, <c>nodes</c>, <c>cache</c>, or none.</param>
/// <param name="Read">The expression the keyword took, or the empty string for <c>nodes</c> and none.</param>
/// <param name="Role">Whether it is a slot, a derived property, or one that goes on conversion.</param>
/// <param name="Kinds">The kinds a token slot may hold; empty for a slot that is not a token.</param>
/// <param name="Today">The property's type until the kind is converted, when it differs from <see cref="Type"/>.</param>
public sealed record NodeSlot(
    string Name,
    string Type,
    ImmutableArray<string> Summary,
    string Form,
    string Read,
    SlotRole Role,
    ImmutableArray<string> Kinds,
    string? Today)
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

    /// <summary>The property's type until the kind is converted.</summary>
    public string PropertyType => Today ?? Type;

    /// <summary>Whether the parser writes this slot today, and so whether the red class has the property.</summary>
    public bool IsWritten => Form != "none";

    /// <summary>The item type of a property that is an <c>ImmutableArray</c> today, or null.</summary>
    public string? ItemType =>
        PropertyType.StartsWith("ImmutableArray<", StringComparison.Ordinal) ? Inside(PropertyType, "ImmutableArray<") : null;

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

    /// <summary>
    /// Whether the red property can read the slot as well as search for it while the kind is
    /// unconverted: it can when the property's type is the slot's, or the slot's made nullable.
    /// </summary>
    public bool ReadsEitherWay => IsPiece && Form == "read" && (Today is null || Today == Type + "?");

    /// <summary>The field a slot or a kept property is held in, named after the property.</summary>
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
