namespace Norristown.Syntax;

/// <summary>
/// Represents an edit that replaces <see cref="Length"/> characters at <see cref="Start"/> with
/// <see cref="NewText"/>.
/// </summary>
public readonly record struct TextChange(int Start, int Length, string NewText);
