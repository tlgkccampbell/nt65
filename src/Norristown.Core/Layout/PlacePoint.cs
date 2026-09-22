using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Where a <c>.place</c> stands in its file's layout: the point in the order the file's bytes
/// are written at which the placed module's bytes go.
/// </summary>
/// <param name="Directive">The <c>.place</c> itself.</param>
/// <param name="Step">How many of the file's steps come before it.</param>
/// <param name="Resumes">
/// The run of distances the file's bytes after it are measured in, from nothing: the file knows
/// no distance across the line, since what the placed module writes is between.
/// </param>
/// <param name="Segment">The segment the file is writing to there, or null before any region names one.</param>
public sealed record PlacePoint(PlaceDirectiveSyntax Directive, int Step, int Resumes, string? Segment);
