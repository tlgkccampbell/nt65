using System.Reflection;
using System.Text.RegularExpressions;
using Norristown.Syntax;

namespace Norristown.Tests;

/// <summary>
/// The order the layers of <c>Norristown.Core</c> stand in, and that none of them reaches back up
/// it. Two back-edges were found and cut here — the semantic layer leaned on layout for the
/// instruction tables and the register vocabulary, and layout held the flow analysis — and
/// nothing but this says they may not come back.
/// </summary>
public sealed class NamespaceOrderTests
{
    /// <summary>
    /// The layers, lowest first. Syntax knows nothing of the processor, so every operand form
    /// parses everywhere; the processor's tables know nothing of what a program means; meaning
    /// comes before the bytes it lays out; the project file is read as meaning, since its
    /// segments and addresses are checked the way a file's are; the flow analysis reads a
    /// layout; and emission is written from all of them.
    /// </summary>
    private static readonly string[] Order =
    [
        "Norristown.Syntax", "Norristown.Processor", "Norristown.Semantics", "Norristown.Project",
        "Norristown.Layout", "Norristown.Flow", "Norristown.Emit",
    ];

    /// <summary>Every member a type declares, at every accessibility.</summary>
    private static BindingFlags Everything =>
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>Nothing a type holds, takes or hands back is named in a layer above its own.</summary>
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
    /// The same for what only a method body names, which a signature does not show: a layer's
    /// folder writes neither a <c>using</c> of a layer above it nor a name qualified through one.
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

    /// <summary>Which layer a namespace is, or null for one the order does not name.</summary>
    private static int? Layer(string? name)
    {
        var found = Array.FindIndex(Order, layer => name == layer
            || name?.StartsWith(layer + ".", StringComparison.Ordinal) == true);
        return found < 0 ? null : found;
    }

    /// <summary>A namespace without the assembly's own name in front of it.</summary>
    private static string Short(string name) => name["Norristown.".Length..];

    /// <summary>The types <paramref name="member"/>'s signature names.</summary>
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
    /// <paramref name="type"/> and the types it is built out of: what an array or a by-reference
    /// is of, and the arguments a generic type is closed over.
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
