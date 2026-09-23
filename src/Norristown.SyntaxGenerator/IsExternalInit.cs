namespace System.Runtime.CompilerServices;

/// <summary>
/// The type the compiler marks an <c>init</c> accessor with, which records need. .NET 5 and later
/// provide it, but this project targets netstandard2.0, the framework the compiler loads a Roslyn
/// component as, so it declares its own.
/// </summary>
internal static class IsExternalInit
{
}
