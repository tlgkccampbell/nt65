using System.Reflection;

namespace Norristown.Tests;

/// <summary>
/// Reads the types that the signatures of a compiled assembly name, for the tests that check
/// the assembly's layering and its public surface.
/// </summary>
internal static class TypeSignatures
{
    /// <summary>
    /// Gets the binding flags that select every member a type declares, at every accessibility,
    /// so that each member can be judged.
    /// </summary>
    public static BindingFlags Everything =>
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Returns the types a signature naming <paramref name="type"/> depends on. For an array, a
    /// by-reference or a pointer, these are the types its element is built from. For a
    /// constructed generic type, they are its generic definition and the types its arguments are
    /// built from. A generic parameter yields nothing, and any other type yields itself.
    /// </summary>
    public static IEnumerable<Type> Unwrapped(Type type)
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
}
