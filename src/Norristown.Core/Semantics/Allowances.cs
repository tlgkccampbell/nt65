namespace Norristown.Semantics;

/// <summary>
/// Represents every <see cref="Allowance"/> in a program. It hides the warnings each one names on
/// the lines it covers, and reports each one that hides nothing.
/// </summary>
internal sealed class Allowances
{
    private readonly Dictionary<string, List<Allowance>> byFile = new(StringComparer.Ordinal);

    private Allowances(IEnumerable<Allowance> allowances)
    {
        foreach (var allowance in allowances)
        {
            var path = allowance.Directive.Tree.Path;
            if (!byFile.TryGetValue(path, out var list))
                byFile[path] = list = [];
            list.Add(allowance);
        }
    }

    /// <summary>
    /// Returns the allowances of every file of <paramref name="program"/>. One in a branch that the
    /// build configuration leaves out applies to nothing, so it is left out too.
    /// </summary>
    public static Allowances Of(ProgramModel program) =>
        new(program.Files.SelectMany(model =>
        {
            var omitted = model.Configuration.Omitted(model.Tree);
            return model.Allowances.Where(allowance => !omitted.Any(span => span.Contains(allowance.Directive.Span)));
        }));

    /// <summary>
    /// Returns <paramref name="found"/> without the warnings an allowance hides, and adds each
    /// allowance that hides one to <paramref name="used"/>. Only a diagnostic reported as a warning
    /// is hidden, so a warning that a processor makes an error stays an error.
    /// </summary>
    public IEnumerable<Diagnostic> Apply(IEnumerable<Diagnostic> found, ISet<Allowance> used)
    {
        if (byFile.Count == 0)
            return found;
        return found.Where(diagnostic => diagnostic.Severity != Severity.Warning || !Hides(diagnostic, used));
    }

    /// <summary>
    /// Returns a warning for each allowance not in <paramref name="used"/>, other than one in a
    /// macro body, which another call may need.
    /// </summary>
    public IEnumerable<Diagnostic> Unused(ISet<Allowance> used) =>
        byFile.Values.SelectMany(list => list)
            .Where(allowance => !allowance.InMacroBody && !used.Contains(allowance))
            .Select(allowance => new Diagnostic(
                allowance.Directive.Tree.GetSpan(allowance.Directive.Span),
                Catalogue.AllowUnused.Message(
                    allowance.Name,
                    allowance.Covered.StartLine == allowance.Covered.EndLine ? "the line below it" : "the block below it"))
            {
                Fix = new DiagnosticFix(FixKind.Redundant),
                IsUnnecessary = true,
            });

    /// <summary>
    /// Returns a value indicating whether an allowance hides <paramref name="diagnostic"/>, and
    /// adds every allowance that does to <paramref name="used"/>.
    /// </summary>
    private bool Hides(Diagnostic diagnostic, ISet<Allowance> used)
    {
        var hidden = false;
        foreach (var allowance in Covering(diagnostic.Span, diagnostic.Id)
            .Concat(diagnostic.Related
                .Where(related => related.Message == Expansion.InTheMacroBody)
                .SelectMany(related => Covering(related.Span, diagnostic.Id))))
        {
            used.Add(allowance);
            hidden = true;
        }
        return hidden;
    }

    /// <summary>Returns the allowances of <paramref name="id"/> whose lines hold <paramref name="span"/>.</summary>
    private IEnumerable<Allowance> Covering(Span span, string id) =>
        span.File is { } path && byFile.TryGetValue(path, out var list)
            ? list.Where(allowance => allowance.Name == id
                && span.LineIndex >= allowance.Covered.StartLine && span.LineIndex <= allowance.Covered.EndLine)
            : [];
}
