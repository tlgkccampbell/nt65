using Norristown.Flow;
using Norristown.Layout;
using Norristown.Semantics;

namespace Norristown;

/// <summary>
/// Represents what analyzing one file of a program on its own found: its model, what its lines
/// assemble to, its control flow and, on the 65816, the processor state through it.
/// </summary>
/// <param name="Model">The file's model, which says what its names refer to.</param>
/// <param name="Layout">The file's layout, which says what its lines assemble to.</param>
/// <param name="Flow">The file's control flow.</param>
/// <param name="State">
/// The processor state through the file on the 65816, or null on the processors that have no
/// state to track.
/// </param>
public sealed record FileAnalysis(SemanticModel Model, CodeLayout Layout, ControlFlow Flow, StateAnalysis? State)
{
    /// <summary>Gets the logical path of the file.</summary>
    public string Path => Model.Tree.Path;
}
