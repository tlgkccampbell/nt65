using System.Reflection;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// Tests the boundary around the green tree (the internal syntax nodes), checked on the compiled
/// assembly rather than on the source. Everything in <c>Norristown.Syntax.InternalSyntax</c>
/// belongs to the syntax layer alone, and analyzers and editor features are written against the
/// red tree (the public syntax API). The compiler catches most ways of leaking an internal type,
/// but not a type that has been made public again, so this test catches that.
/// </summary>
public sealed class ApiSurfaceTests
{
    /// <summary>The namespace whose types belong to the syntax layer and to nobody else.</summary>
    private const string Internal = "Norristown.Syntax.InternalSyntax";

    /// <summary>No green-tree type is one a consumer of the assembly can name.</summary>
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
    /// No signature a consumer can see names a type the consumer cannot name, whether a green node
    /// or anything else the assembly keeps to itself. A member that returns or takes such a type
    /// has to be internal, or the type it names has to become part of the API deliberately.
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
    /// The compiler, the language server and the CLI read the red tree. The syntax layer owns the
    /// green tree, and everything above it, including binding, layout, flow and emission, is
    /// written against the API a consumer has. This test therefore reads the source of every file
    /// in <c>Norristown.Core</c> outside <c>Syntax/</c> and <c>Generated/</c>, and fails if one of
    /// them uses the green tree.
    /// </summary>
    [Fact]
    public void NothingAboveTheSyntaxLayerReadsTheGreenTree()
    {
        string[] reaches = ["InternalSyntax", "tree.Green", "tree.Lines", "Tree.Lines", "tree.Statement("];
        var core = Repo.Path("src", "Norristown.Core");
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var named = Repo.Named(file);
            if (named.StartsWith("src/Norristown.Core/Syntax/", StringComparison.Ordinal)
                || named.StartsWith("src/Norristown.Core/Generated/", StringComparison.Ordinal))
            {
                continue;
            }
            var text = Repo.ReadText(file);
            problems.AddRange(reaches.Where(text.Contains).Select(reach => $"{named} names {reach}"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// A token, a trivia and a list are values. Reading the same one twice gives two equal copies,
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

        // The node a token was reached through is part of its value, so two missing tokens at the
        // same position can be told apart.
        var nop = tree.GetLine(1).Tokens[0];
        Assert.Equal(nop, tree.GetLine(1).Tokens[0]);
        var read = tree.Root.FindToken(nop.Span.Start);
        Assert.Equal(nop.Span, read.Span);
        Assert.NotEqual(nop, read);
        Assert.IsType<LineSyntax>(nop.Parent);
        Assert.IsType<InstructionStatementSyntax>(read.Parent);
    }

    /// <summary>
    /// Gets the binding flags that select every member a type declares, at every accessibility,
    /// so that each member can be judged.
    /// </summary>
    private static BindingFlags Everything =>
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Determines whether <paramref name="member"/> should be checked on its own. The accessor
    /// methods of a property or an event are covered by checking the property or event, so only
    /// the property or event is reported. An operator or a conversion has no other member that
    /// covers it.
    /// </summary>
    private static bool Own(MemberInfo member) => member is not MethodInfo { IsSpecialName: true } method
        || !(method.Name.StartsWith("get_", StringComparison.Ordinal)
            || method.Name.StartsWith("set_", StringComparison.Ordinal)
            || method.Name.StartsWith("add_", StringComparison.Ordinal)
            || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    /// <summary>Determines whether a consumer of the assembly can see <paramref name="member"/> at all.</summary>
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

    /// <summary>Returns the types that the signature of <paramref name="member"/> names.</summary>
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

    /// <summary>
    /// Records a problem for each type in <paramref name="types"/>, or type they are built from,
    /// that is a green type or that a consumer cannot name.
    /// </summary>
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
    /// Returns the types a signature naming <paramref name="type"/> depends on. For an array, a
    /// by-reference or a pointer, these are the types its element is built from. For a
    /// constructed generic type, they are its generic definition and the types its arguments are
    /// built from. A generic parameter yields nothing, and any other type yields itself.
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

    /// <summary>Determines whether a consumer of the assembly can name <paramref name="type"/>.</summary>
    private static bool Public(Type type) => type.IsNested
        ? type.IsNestedPublic && Public(type.DeclaringType!)
        : type.IsPublic;

    private static string Named(Type type) => type.FullName ?? type.Name;
}
