namespace System.Runtime.CompilerServices;

/// <summary>
/// What the compiler marks an <c>init</c> accessor with, and so what a record needs. It is part
/// of .NET 5 and later; this project targets netstandard2.0, because that is what the compiler
/// loads a Roslyn component as, so it declares its own.
/// </summary>
internal static class IsExternalInit
{
}
