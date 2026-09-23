using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// A declaration whose name is the binding of an <c>.each</c> over a named enum: one
/// declaration per member, under the member's name, in the scope around the repetition.
/// <c>.multiproc E, b: signature { }</c> is the same thing with the repetition and the routine
/// folded into one line.
/// <para>
/// The names come from two headers — the repetition's line and the enum's member list — and
/// nothing is concatenated, so which names a family declares is read from the source without
/// evaluating anything. What the body declares stays local to each turn, as in any
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
    /// The statement that declares the instances: the <c>.proc b</c> or <c>.data b:</c> line, or
    /// the <c>.multiproc</c> line, which is both that and the repetition's.
    /// </summary>
    public StatementSyntax Declaration { get; }

    /// <summary>The block each turn writes out: the <c>.each</c>'s, or the <c>.multiproc</c>'s own.</summary>
    public BlockSyntax Block { get; }

    /// <summary>The name the repetition binds, which each instance is named from.</summary>
    public Symbol Binding { get; }

    /// <summary>The enum whose members the instances are named after.</summary>
    public Symbol Enumeration { get; }

    /// <summary>The declarations the family makes, in the order the enum lists its members.</summary>
    public IReadOnlyList<Symbol> Instances { get; }

    /// <summary>The members they are named after, in the same order.</summary>
    public IReadOnlyList<Symbol> Members { get; }

    /// <summary>Whether it is written as <c>.multiproc</c> rather than as an <c>.each</c> with a body.</summary>
    public bool IsFolded => Declaration is MultiProcDeclarationSyntax;

    /// <summary>What the family is called where a message names it, <c>.multiproc</c> or <c>.each</c>.</summary>
    public string Directive => IsFolded ? ".multiproc" : ".each";

    /// <summary>The instance named after <paramref name="member"/>, or null when it declares none.</summary>
    public Symbol? InstanceFor(Symbol? member) =>
        member is not null && byMember.TryGetValue(member, out var instance) ? instance : null;

    /// <summary>
    /// The instance being written out at <paramref name="on"/>: the one for the member of the
    /// enclosing turn that writes this family's block, or null outside such a turn.
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
    /// The same problem found on more than one instance, reported once. A family's body is
    /// written once, so a mistake in it is found once per instance, and each report names the
    /// instance it was found on. When two or more reports are otherwise identical — the same
    /// place, the same severity, the same words apart from the instance named — they are
    /// replaced by a single message that names the repetition's binding instead.
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

        var said = new HashSet<(Span, Severity, string)>();
        var kept = new List<Diagnostic>(found.Count);
        for (var i = 0; i < found.Count; i++)
        {
            var key = (found[i].Span, found[i].Severity, general[i]);
            if (counted.GetValueOrDefault(key) < 2)
                kept.Add(found[i]);
            else if (said.Add(key))
                kept.Add(found[i] with { Message = general[i] });
        }
        return kept;
    }

    /// <summary>The message with each instance's name written as the name its repetition binds.</summary>
    private static string Generalized(IReadOnlyList<Family> families, string message)
    {
        foreach (var family in families)
        {
            foreach (var instance in family.Instances)
                message = message.Replace($"`{instance.Name}`", $"`{family.Binding.Name}`", StringComparison.Ordinal);
        }
        return message;
    }

    /// <summary>What the family declares, for debugging.</summary>
    public override string ToString() => $"{Directive} {Enumeration.Name}, {Binding.Name}: {Instances.Count} instances";
}
