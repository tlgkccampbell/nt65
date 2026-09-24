namespace Norristown.SyntaxGenerator;

/// <summary>Specifies what a slot holds when it holds a list.</summary>
public enum ListShape
{
    /// <summary>Not a list. The slot holds one token or one node.</summary>
    None,

    /// <summary>Items with nothing between them.</summary>
    Nodes,

    /// <summary>Items with a separator between them, nearly always a comma.</summary>
    Separated,

    /// <summary>Tokens with nothing between them.</summary>
    Tokens,
}
