using System.Reflection;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// The boundary around the green tree, read off the compiled assembly rather than off the source.
/// Everything in <c>Norristown.Syntax.InternalSyntax</c> is the syntax layer's own, and an
/// analyzer or an editor feature is written against the red tree; the compiler catches most ways
/// of leaking one of those types, but not a type that is public again, so this is what says so.
/// </summary>
public sealed class ApiSurfaceTests
{
    /// <summary>The namespace whose types belong to the syntax layer and to nobody else.</summary>
    private const string Internal = "Norristown.Syntax.InternalSyntax";

    /// <summary>Nothing of the green tree is a type a consumer of the assembly can name.</summary>
    [Fact]
    public void TheGreenTreeIsNotPartOfTheAssemblysTypes()
    {
        var exported = typeof(SyntaxTree).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == Internal)
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal);
        Assert.Equal([], exported);
    }

    /// <summary>
    /// No signature a consumer can see names a type it cannot: not a green node, and not anything
    /// else the assembly keeps to itself. A member that returns or takes one has to be internal,
    /// or the type it names has to become part of the API deliberately.
    /// </summary>
    [Fact]
    public void NoPublicSignatureNamesATypeTheConsumerCannot()
    {
        var problems = new List<string>();
        foreach (var type in typeof(SyntaxTree).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            Check($"{Named(type)} : base", problems, type.BaseType is null ? [] : [type.BaseType]);
            Check($"{Named(type)} : interfaces", problems, type.GetInterfaces());
            foreach (var parameter in type.IsGenericTypeDefinition ? type.GetGenericArguments() : [])
                Check($"{Named(type)}<{parameter.Name}> constraints", problems, parameter.GetGenericParameterConstraints());
            foreach (var member in type.GetMembers(Everything).Where(Visible).Where(Own))
                Check($"{Named(type)}.{member.Name}", problems, Mentioned(member));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// A token, a trivia and a list are values: reading the same one twice gives two equal copies,
    /// and reading a different one gives an unequal copy. The green token a token wraps is not part
    /// of its public surface, but it is still part of what makes two of them the same token.
    /// </summary>
    [Fact]
    public void ATokenIsAValue()
    {
        var tree = SyntaxTree.Parse("test.nt65", ".proc p {\n    nop\n}\n");
        var proc = tree.GetLine(0).Statement;

        Assert.Equal(proc.ChildTokens[0], proc.ChildTokens[0]);
        Assert.True(proc.ChildTokens[0] == proc.ChildTokens[0]);
        Assert.Equal(proc.ChildTokens[0].GetHashCode(), proc.ChildTokens[0].GetHashCode());
        Assert.NotEqual(proc.ChildTokens[0], proc.ChildTokens[1]);

        // Which node a token was read through is part of which value it is, which is what lets two
        // pieces the source left out at the same place be told apart.
        var nop = tree.GetLine(1).Tokens[0];
        Assert.Equal(nop, tree.GetLine(1).Tokens[0]);
        var read = tree.Root.FindToken(nop.Span.Start);
        Assert.Equal(nop.Span, read.Span);
        Assert.NotEqual(nop, read);
        Assert.IsType<LineSyntax>(nop.Parent);
        Assert.IsType<InstructionStatementSyntax>(read.Parent);
    }

    /// <summary>Every member a type declares, at every accessibility, so that each can be judged.</summary>
    private static BindingFlags Everything =>
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Whether <paramref name="member"/> is worth saying anything about on its own: the accessor
    /// methods of a property or an event say what the property does, so only the property is
    /// reported, while an operator and a conversion are members with nothing else to speak for them.
    /// </summary>
    private static bool Own(MemberInfo member) => member is not MethodInfo { IsSpecialName: true } method
        || !(method.Name.StartsWith("get_", StringComparison.Ordinal)
            || method.Name.StartsWith("set_", StringComparison.Ordinal)
            || method.Name.StartsWith("add_", StringComparison.Ordinal)
            || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    /// <summary>Whether a consumer of the assembly can see <paramref name="member"/> at all.</summary>
    private static bool Visible(MemberInfo member) => member switch
    {
        Type nested => (nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem)
            && Visible(nested.DeclaringType!),
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(Visible),
        EventInfo tell => new[] { tell.AddMethod, tell.RemoveMethod }.OfType<MethodInfo>().Any(Visible),
        _ => false,
    };

    /// <summary>The types <paramref name="member"/>'s signature names.</summary>
    private static IEnumerable<Type> Mentioned(MemberInfo member) => member switch
    {
        Type nested => [],
        FieldInfo field => [field.FieldType],
        PropertyInfo property => [property.PropertyType, .. property.GetIndexParameters().Select(p => p.ParameterType)],
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType)],
        MethodBase constructor => [.. constructor.GetParameters().Select(p => p.ParameterType)],
        EventInfo tell => tell.EventHandlerType is { } handler ? [handler] : [],
        _ => [],
    };

    /// <summary>Says so for each of <paramref name="types"/> that a consumer cannot name.</summary>
    private static void Check(string where, List<string> problems, IEnumerable<Type> types)
    {
        foreach (var type in types.SelectMany(Unwrapped).Distinct())
        {
            if (type.Namespace == Internal)
                problems.Add($"{where} names the green type {Named(type)}");
            else if (!Public(type))
                problems.Add($"{where} names the non-public type {Named(type)}");
        }
    }

    /// <summary>
    /// <paramref name="type"/> and the types it is built out of: what an array, a by-reference or
    /// a pointer is of, and the arguments a generic type is closed over.
    /// </summary>
    private static IEnumerable<Type> Unwrapped(Type type)
    {
        if (type.IsGenericParameter)
            yield break;
        if (type.HasElementType)
        {
            foreach (var inner in Unwrapped(type.GetElementType()!))
                yield return inner;
            yield break;
        }
        if (type.IsConstructedGenericType)
        {
            yield return type.GetGenericTypeDefinition();
            foreach (var argument in type.GetGenericArguments().SelectMany(Unwrapped))
                yield return argument;
            yield break;
        }
        yield return type;
    }

    /// <summary>Whether a consumer of the assembly can name <paramref name="type"/>.</summary>
    private static bool Public(Type type) => type.IsNested
        ? type.IsNestedPublic && Public(type.DeclaringType!)
        : type.IsPublic;

    private static string Named(Type type) => type.FullName ?? type.Name;
}
