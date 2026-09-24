using System.Reflection;
using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// Tests the order of the namespace layers in <c>Norristown.Core</c>, and checks that no layer
/// depends on one above it. Two such upward dependencies were found and removed. The semantic
/// layer relied on layout for the instruction tables and the register vocabulary, and layout
/// contained the flow analysis. Nothing but this test stops them from coming back.
/// </summary>
public sealed class NamespaceOrderTests
{
    /// <summary>
    /// The layers, lowest first. Syntax knows nothing of the processor, so every operand form
    /// parses under every CPU. The modules that come with nt65 are only source it has parsed. The
    /// processor's tables know nothing of what a program means. Semantics comes before layout,
    /// which assigns the bytes. The project layer sits above semantics, because the project file's
    /// segments and addresses are checked the same way a source file's are. The flow analysis
    /// reads a layout, and emission draws on all of the layers.
    /// </summary>
    private static readonly string[] Order =
    [
        "Norristown.Syntax", "Norristown.Standard", "Norristown.Processor", "Norristown.Semantics", "Norristown.Project",
        "Norristown.Layout", "Norristown.Flow", "Norristown.Emit",
    ];

    /// <summary>Gets the binding flags that select every member a type declares, at every accessibility.</summary>
    private static BindingFlags Everything =>
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// No type's fields, parameters, return types, base type or interfaces come from a layer above
    /// its own.
    /// </summary>
    [Fact]
    public void NoTypeNamesALayerAboveItsOwn()
    {
        var problems = new List<string>();
        foreach (var type in typeof(SyntaxTree).Assembly.GetTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (Layer(type.Namespace) is not { } layer)
                continue;
            var names = Mentioned(type).Concat(type.GetMembers(Everything).SelectMany(Mentioned));
            foreach (var named in names.SelectMany(Unwrapped).Distinct())
            {
                if (Layer(named.Namespace) is { } above && above > layer)
                    problems.Add($"{type.FullName} names {named.FullName}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Applies the same check to names used only inside method bodies, which signatures do not
    /// show. No file in a layer's folder has a <c>using</c> for a layer above it or a name
    /// qualified with one.
    /// </summary>
    [Fact]
    public void NoFolderNamesALayerAboveItsOwn()
    {
        var problems = new List<string>();
        for (var layer = 0; layer < Order.Length; layer++)
        {
            var folder = Repo.Path("src", "Norristown.Core", Short(Order[layer]));
            foreach (var file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                var text = Repo.ReadText(file);
                problems.AddRange(Order.Skip(layer + 1)
                    .Where(above => text.Contains($"using {above};", StringComparison.Ordinal)
                        || Regex.IsMatch(text, $@"(?<![\w.]){Short(above)}\."))
                    .Select(above => $"{Repo.Named(file)} names {above}"));
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Returns the index of the layer a namespace belongs to, or null for a namespace that is not
    /// in the order.
    /// </summary>
    private static int? Layer(string? name)
    {
        var found = Array.FindIndex(Order, layer => name == layer
            || name?.StartsWith(layer + ".", StringComparison.Ordinal) == true);
        return found < 0 ? null : found;
    }

    /// <summary>Returns a namespace name without the assembly's own name in front of it.</summary>
    private static string Short(string name) => name["Norristown.".Length..];

    /// <summary>
    /// Returns the types that the signature of <paramref name="member"/> names. For a nested type,
    /// these are its base type and its interfaces.
    /// </summary>
    private static IEnumerable<Type> Mentioned(MemberInfo member) => member switch
    {
        FieldInfo field => [field.FieldType],
        PropertyInfo property => [property.PropertyType, .. property.GetIndexParameters().Select(p => p.ParameterType)],
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType)],
        MethodBase constructor => [.. constructor.GetParameters().Select(p => p.ParameterType)],
        Type nested => [nested.BaseType ?? typeof(object), .. nested.GetInterfaces()],
        _ => [],
    };

    /// <summary>
    /// Returns <paramref name="type"/>, or its generic definition when it is a constructed generic
    /// type, followed by the types its generic arguments are built from. For an array or a
    /// by-reference, it returns the types its element is built from instead. A generic parameter
    /// yields nothing.
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
        yield return type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        foreach (var argument in type.GenericTypeArguments.SelectMany(Unwrapped))
            yield return argument;
    }
}
