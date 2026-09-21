using System.Reflection;
using Norristown.LanguageServer;
using Norristown.Syntax;
using StreamJsonRpc;

namespace Norristown.Tests.LanguageServer;

/// <summary>
/// A request the person has moved on from should stop rather than finish. Every handler takes
/// the token the client's <c>$/cancelRequest</c> trips, and the places that walk a whole
/// program ask it between files, which is where a slow answer spends its time.
/// </summary>
public sealed class CancellationTests
{
    /// <summary>
    /// The four that take none: three are the server's own life, which a client does not
    /// cancel, and the fourth answers from a list the workspace already holds.
    /// </summary>
    [Fact]
    public void EveryRequestHandlerTakesACancellationToken()
    {
        var without = typeof(Server)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<JsonRpcMethodAttribute>() is not null)
            .Where(method => !method.GetParameters().Any(one => one.ParameterType == typeof(CancellationToken)))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["Configurations", "Exit", "Initialize", "Shutdown"], without);
    }

    /// <summary>A search across a workspace of hundreds of files stops at the next one.</summary>
    [Fact]
    public void ASearchAcrossTheWorkspaceStopsBetweenFiles()
    {
        using var given = new CancellationTokenSource();
        SyntaxTree[] files =
        [
            SyntaxTree.Parse("a.nt65", ".module a\nCOUNT = 1\n"),
            SyntaxTree.Parse("b.nt65", ".module b\nCOUNT = 2\n"),
        ];
        Assert.Equal(2, WorkspaceSymbols.Matching(files, "count", TestContext.Current.CancellationToken).Count);

        given.Cancel();
        Assert.Throws<OperationCanceledException>(() => WorkspaceSymbols.Matching(files, "count", given.Token));
    }
}
