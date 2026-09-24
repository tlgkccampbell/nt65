using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Tests the numbers behind <see cref="ExitCode"/>, which scripts and build tools test and which
/// the other tests name only symbolically.
/// </summary>
public sealed class ExitCodeTests
{
    /// <summary>Each exit code keeps the number nt65 has always returned for it.</summary>
    [Fact]
    public void EachExitCodeKeepsItsNumber()
    {
        Assert.Equal(
            [(0, ExitCode.Success), (1, ExitCode.InputError), (2, ExitCode.UsageError), (70, ExitCode.InternalError)],
            Enum.GetValues<ExitCode>().Select(code => ((int)code, code)));
    }
}
