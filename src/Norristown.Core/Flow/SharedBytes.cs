using Norristown.Semantics;

namespace Norristown.Flow;

/// <summary>
/// Represents a run of addresses that a location on one <see cref="DirectPage"/> and a location on
/// another both take.
/// </summary>
/// <param name="Here">The location on the page that reports the run.</param>
/// <param name="There">The location on the other page.</param>
/// <param name="Page">The other page.</param>
/// <param name="First">The first address both take.</param>
/// <param name="Last">The last address both take.</param>
public sealed record SharedBytes(Symbol Here, Symbol There, DirectPage Page, long First, long Last);
