using Norristown.Syntax;

namespace Norristown.Layout;

/// <summary>
/// Represents where a <c>.place</c> falls in its file's layout, which is the point, in the order
/// the file's bytes are emitted, at which the placed module's bytes go.
/// </summary>
/// <param name="Directive">The <c>.place</c> itself.</param>
/// <param name="Step">How many of the file's steps come before it.</param>
/// <param name="Resumes">
/// The new run in which the file's bytes after the directive are measured, starting from
/// offset zero. The file knows no distance across the directive, because the placed module's
/// bytes come in between.
/// </param>
/// <param name="Segment">
/// The segment the file is emitting to at the directive, or null before any region names one.
/// </param>
public sealed record PlacePoint(PlaceDirectiveSyntax Directive, int Step, int Resumes, string? Segment);
