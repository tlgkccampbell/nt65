using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Where a <c>.place</c> falls in its file's layout: the point, in the order the file's bytes
/// are written, at which the placed module's bytes go.
/// </summary>
/// <param name="Directive">The <c>.place</c> itself.</param>
/// <param name="Step">How many of the file's steps come before it.</param>
/// <param name="Resumes">
/// The new run in which the file's bytes after the directive are measured, starting from
/// offset zero: the file knows no distance across the line, because the placed module's bytes
/// come in between.
/// </param>
/// <param name="Segment">The segment the file is writing to there, or null before any region names one.</param>
public sealed record PlacePoint(PlaceDirectiveSyntax Directive, int Step, int Resumes, string? Segment);
