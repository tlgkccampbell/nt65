using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents a family: the routines or data one <c>.each</c> declaration produces. The
/// declaration's name is the binding of an <c>.each</c> over a named enum, and the family holds
/// one declaration per member, named after that member, in the scope around the repetition.
/// <c>.multiproc E, b: signature { }</c> declares a family too, with the repetition and the
/// routine folded into one line.
/// <para>
/// The names come from two headers, the repetition's line and the enum's member list, and
/// nothing is concatenated. The names a family declares can therefore be read from the source
/// without evaluating anything. What the body declares stays local to each iteration, as in any
/// repetition; only the instances become names in the file.
/// </para>
/// </summary>
public sealed class Family
{
    private readonly Dictionary<Symbol, Symbol> byMember = [];

    internal Family(
        StatementSyntax declaration, BlockSyntax block, Symbol binding, Symbol enumeration,
        IReadOnlyList<(Symbol Member, Symbol Instance)> instances)
    {
        Declaration = declaration;
        Block = block;
        Binding = binding;
        Enumeration = enumeration;
        Instances = [.. instances.Select(pair => pair.Instance)];
        Members = [.. instances.Select(pair => pair.Member)];
        foreach (var (member, instance) in instances)
            byMember[member] = instance;
    }

    /// <summary>
    /// Gets the statement that declares the instances. This is the <c>.proc b</c> or
    /// <c>.data b:</c> line, or the <c>.multiproc</c> line, which is both the declaration and the
    /// repetition's line.
    /// </summary>
    public StatementSyntax Declaration { get; }

    /// <summary>
    /// Gets the block each iteration emits, which is the <c>.each</c>'s block or the
    /// <c>.multiproc</c>'s own.
    /// </summary>
    public BlockSyntax Block { get; }

    /// <summary>Gets the name the repetition binds, from which each instance is named.</summary>
    public Symbol Binding { get; }

    /// <summary>Gets the enum whose members the instances are named after.</summary>
    public Symbol Enumeration { get; }

    /// <summary>Gets the declarations the family makes, in the order the enum lists its members.</summary>
    public IReadOnlyList<Symbol> Instances { get; }

    /// <summary>
    /// Gets the enum members the instances are named after, in the same order as
    /// <see cref="Instances"/>.
    /// </summary>
    public IReadOnlyList<Symbol> Members { get; }

    /// <summary>
    /// Gets a value indicating whether the family is declared with <c>.multiproc</c> rather than
    /// with an <c>.each</c> that has a body.
    /// </summary>
    public bool IsFolded => Declaration is MultiProcDeclarationSyntax;

    /// <summary>
    /// Gets the directive that names the family in messages, either <c>.multiproc</c> or
    /// <c>.each</c>.
    /// </summary>
    public string Directive => IsFolded ? ".multiproc" : ".each";

    /// <summary>
    /// Returns the instance named after <paramref name="member"/>, or null if the family declares
    /// no instance for it.
    /// </summary>
    public Symbol? InstanceFor(Symbol? member) =>
        member is not null && byMember.TryGetValue(member, out var instance) ? instance : null;

    /// <summary>
    /// Returns the instance being emitted at <paramref name="on"/>. This is the instance for the
    /// member of the enclosing iteration that emits this family's block, or null outside such an
    /// iteration.
    /// </summary>
    public Symbol? InstanceAt(Expansion? on)
    {
        for (var level = on; level is not null; level = level.Outer)
        {
            if (level.Body == Block)
                return InstanceFor(level.Member);
        }
        return null;
    }

    /// <summary>
    /// Returns <paramref name="found"/> with each problem found on more than one instance reported
    /// once. A family's body appears once in the source, so a mistake in it is found once per
    /// instance, and each report names the instance it was found on. Two or more reports that are
    /// otherwise identical, with the same span, the same severity and the same words apart from
    /// the instance named, are replaced by a single message that names the repetition's binding
    /// instead.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Collapsed(IReadOnlyList<Family> families, IReadOnlyList<Diagnostic> found)
    {
        if (families.Count == 0 || found.Count < 2)
            return found;

        var counted = new Dictionary<(Span, Severity, string), int>();
        var general = new string[found.Count];
        for (var i = 0; i < found.Count; i++)
        {
            general[i] = Generalized(families, found[i].Message);
            if (general[i] != found[i].Message)
                counted[(found[i].Span, found[i].Severity, general[i])] = counted.GetValueOrDefault((found[i].Span, found[i].Severity, general[i])) + 1;
        }

        var reported = new HashSet<(Span, Severity, string)>();
        var kept = new List<Diagnostic>(found.Count);
        for (var i = 0; i < found.Count; i++)
        {
            var key = (found[i].Span, found[i].Severity, general[i]);
            if (counted.GetValueOrDefault(key) < 2)
                kept.Add(found[i]);
            else if (reported.Add(key))
                kept.Add(found[i] with { Message = general[i] });
        }
        return kept;
    }

    /// <summary>
    /// Returns <paramref name="message"/> with each instance's name replaced by the name its
    /// repetition binds.
    /// </summary>
    private static string Generalized(IReadOnlyList<Family> families, string message)
    {
        foreach (var family in families)
        {
            foreach (var instance in family.Instances)
                message = message.Replace($"`{instance.Name}`", $"`{family.Binding.Name}`", StringComparison.Ordinal);
        }
        return message;
    }

    /// <summary>Returns a description of what the family declares, for debugging.</summary>
    public override string ToString() => $"{Directive} {Enumeration.Name}, {Binding.Name}: {Instances.Count} instances";
}
