namespace Norristown.Syntax;

/// <summary>Replaces <see cref="Length"/> characters at <see cref="Start"/> with <see cref="NewText"/>.</summary>
public readonly record struct TextChange(int Start, int Length, string NewText);
