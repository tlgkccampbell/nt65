namespace System.Runtime.CompilerServices;

/// <summary>
/// Provides the type the compiler uses to mark an <c>init</c> accessor, which records need. .NET 5
/// and later provide it, but this project targets netstandard2.0, which is the framework the
/// compiler loads a Roslyn component under, so it declares its own.
/// </summary>
internal static class IsExternalInit
{
}
