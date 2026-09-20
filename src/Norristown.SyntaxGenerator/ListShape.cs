namespace Norristown.SyntaxGenerator;

/// <summary>What a slot holds when it holds a list.</summary>
public enum ListShape
{
    /// <summary>Not a list: one token or one node.</summary>
    None,

    /// <summary>Items with nothing between them.</summary>
    Nodes,

    /// <summary>Items with a separator between them, nearly always a comma.</summary>
    Separated,

    /// <summary>Tokens with nothing between them.</summary>
    Tokens,
}
