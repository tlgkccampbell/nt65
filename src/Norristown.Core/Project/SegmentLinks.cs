using Norristown.Processor;
using Norristown.Semantics;

namespace Norristown.Project;

/// <summary>
/// Builds the segments a project declares from its <c>segments</c> and its <c>links</c>. Without
/// links, each entry of <c>segments</c> declares a segment. With them, the linked configurations
/// declare the segments, and <c>segments</c> adds only what a configuration has no word for.
/// <para>
/// A configuration gives a segment's size and its home bank. The segment is <c>zp</c> when its
/// <c>type</c> is <c>zp</c> or it runs wholly in page zero, and its home bank is the bank of the
/// address it runs at. A segment that several links place must agree on every fact nt65 uses,
/// because nt65 analyzes each module once, whichever links it goes into.
/// </para>
/// </summary>
public static class SegmentLinks
{
    /// <summary>
    /// Returns the segments that <paramref name="entries"/> and <paramref name="links"/> declare,
    /// ordered by name, and reports what is wrong with them.
    /// </summary>
    /// <param name="entries">The entries of the project file's <c>segments</c>.</param>
    /// <param name="links">The links of the build, ordered by name.</param>
    /// <param name="spaces">The address spaces the project declares.</param>
    /// <param name="diagnostics">Where problems are reported.</param>
    public static IReadOnlyList<Segment> Resolve(
        IReadOnlyList<ProjectSegment> entries, IReadOnlyList<Link> links, IReadOnlyList<AddressSpace> spaces,
        List<Diagnostic> diagnostics)
    {
        if (links.Count == 0)
            return [.. entries.Select(entry => Declared(entry, diagnostics)).OfType<Segment>()];

        var placed = new Dictionary<string, Segment>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (link.Config is not { } config)
            {
                diagnostics.Add(new Diagnostic(link.Declaration, Catalogue.LinkedConfigUnreadable.Message(link.ConfigPath)));
                continue;
            }
            diagnostics.AddRange(config.Diagnostics);
            var areas = Areas(link, config, spaces, diagnostics);
            foreach (var segment in config.Segments)
                Place(Derived(link, segment, config, areas), placed, diagnostics);
        }

        foreach (var entry in entries)
        {
            if (!placed.TryGetValue(entry.Name, out var segment))
            {
                diagnostics.Add(new Diagnostic(entry.Declaration, Catalogue.SegmentNotLinked.Message(entry.Name)));
                continue;
            }
            placed[entry.Name] = Added(segment, entry, diagnostics);
        }
        return [.. placed.Values.OrderBy(segment => segment.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Returns the segment an entry of <c>segments</c> declares in a project without links, or
    /// null after reporting why it declares none.
    /// </summary>
    private static Segment? Declared(ProjectSegment entry, List<Diagnostic> diagnostics)
    {
        if (entry.Size is not { } size)
        {
            diagnostics.Add(new Diagnostic(entry.Declaration, Catalogue.ProjectSegmentSizeMissing.Message(entry.Name)));
            return null;
        }
        var segment = new Segment(entry.Name, size, entry.Declaration) { Space = entry.Space, Mirrors = entry.Mirrors ?? [] };
        segment = Registers(segment, entry, bank: true, diagnostics);
        if (segment is { Mirrors.Count: > 0, Bank: null })
        {
            diagnostics.Add(new Diagnostic(entry.Declaration, SegmentTable.MirrorsNeedABank(entry.Name)));
            segment = segment with { Mirrors = [] };
        }
        return segment;
    }

    /// <summary>
    /// Returns what the project says about each memory area of a link's configuration, by name,
    /// after checking that each names an area the configuration has and a space the project
    /// declares.
    /// </summary>
    private static Dictionary<string, Link.Area> Areas(
        Link link, LinkerConfig config, IReadOnlyList<AddressSpace> spaces, List<Diagnostic> diagnostics)
    {
        if (link.Space is { } linkSpace && !spaces.Any(space => space.Name == linkSpace))
            diagnostics.Add(new Diagnostic(link.Declaration, Catalogue.SpaceUndeclared.Message(linkSpace)));

        var areas = new Dictionary<string, Link.Area>(StringComparer.Ordinal);
        foreach (var area in link.Memory)
        {
            if (config.Area(area.Name) is null)
            {
                diagnostics.Add(new Diagnostic(area.Declaration, Catalogue.LinkMemoryUnknown.Message(config.Path, area.Name)));
                continue;
            }
            if (area.Space is { } named && !spaces.Any(space => space.Name == named))
            {
                diagnostics.Add(new Diagnostic(area.Declaration, Catalogue.SpaceUndeclared.Message(named)));
                areas[area.Name] = area with { Space = null };
                continue;
            }
            areas[area.Name] = area;
        }
        return areas;
    }

    /// <summary>
    /// Returns the segment one link's configuration places, with the size its type gives, the
    /// bank it runs in, and the space and mirrors of the memory area it runs in.
    /// </summary>
    private static Placed Derived(
        Link link, LinkerConfig.PlacedSegment placed, LinkerConfig config, Dictionary<string, Link.Area> areas)
    {
        var runsIn = placed.RunsIn;
        var area = runsIn is null ? null : config.Area(runsIn);
        var note = runsIn is null ? null : areas.GetValueOrDefault(runsIn);
        var size = placed.Type == "zp" || RunsInPageZero(placed, area) ? AddressSize.ZeroPage : AddressSize.Absolute;
        var segment = new Segment(placed.Name, size, placed.Declaration, Bank: Bank(placed, area))
        {
            Space = note?.Space ?? link.Space,
            Mirrors = note?.Mirrors ?? [],
            Placements = [placed.Declaration],
            IsDefined = placed.Defines,
        };
        return new Placed(segment, config.Path, note);
    }

    /// <summary>
    /// Returns the bank a segment runs in: that of its own <c>start</c> when it gives one, and
    /// otherwise that of its memory area when the whole area is in one bank. Returns null when
    /// nt65 cannot tell, rather than guessing.
    /// </summary>
    private static long? Bank(LinkerConfig.PlacedSegment placed, LinkerConfig.MemoryArea? area)
    {
        if (placed.Start is { } start)
            return start.Value is { } address ? address >> 16 : null;
        if (area is not { Start: { } first, Size: { } size } || size <= 0)
            return null;
        var bank = first >> 16;
        return (first + size - 1) >> 16 == bank ? bank : null;
    }

    /// <summary>
    /// Returns a value indicating whether a segment runs wholly within $0000-$00FF, where every
    /// address is a zero-page address, whatever its <c>type</c>. Code that the program copies into
    /// page zero and runs there, such as BASIC's <c>CHRGET</c>, loads with <c>type = rw</c>.
    /// </summary>
    private static bool RunsInPageZero(LinkerConfig.PlacedSegment placed, LinkerConfig.MemoryArea? area) =>
        placed.Start is null && placed.Offset is null
        && area is { Start: >= 0 and < 0x100, Size: > 0 } && area.Start + area.Size <= 0x100;

    /// <summary>
    /// Adds a segment one link places to those the other links place. When another link already
    /// places it, the two must agree on its size, its space, its bank and its mirrors.
    /// </summary>
    private static void Place(Placed derived, Dictionary<string, Segment> placed, List<Diagnostic> diagnostics)
    {
        var segment = derived.Segment;
        if (derived.Area is { Mirrors.Count: > 0 } && segment.Bank is null)
        {
            diagnostics.Add(new Diagnostic(derived.Area.Declaration, SegmentTable.MirrorsNeedABank(segment.Name)));
            segment = segment with { Mirrors = [] };
        }
        if (!placed.TryGetValue(segment.Name, out var first))
        {
            placed[segment.Name] = segment;
            return;
        }

        var there = first.Placements[0];
        var difference = segment.Size != first.Size ? $"is `{Format(segment.Size)}` here and `{Format(first.Size)}`"
            : segment.Space != first.Space ? $"is in {AddressSpace.Format(segment.Space)} here and in {AddressSpace.Format(first.Space)}"
            : segment.Bank is { } bank && first.Bank is { } other && bank != other
                ? $"is in bank {StateValue.Hex(bank, 2)} here and in bank {StateValue.Hex(other, 2)}"
            : !segment.Mirrors.SequenceEqual(first.Mirrors) ? "has other mirrors here than"
            : null;
        if (difference is not null)
        {
            diagnostics.Add(new Diagnostic(segment.Placements[0],
                Catalogue.LinkedSegmentsDisagree.Message(segment.Name, difference, there.File),
                [new RelatedSpan(there, "placed here")]));
            return;
        }
        placed[segment.Name] = first with
        {
            Bank = first.Bank ?? segment.Bank,
            Placements = [.. first.Placements, .. segment.Placements],
            IsDefined = first.IsDefined || segment.IsDefined,
        };
    }

    /// <summary>
    /// Returns a linked segment with what its entry in <c>segments</c> adds: <c>far</c>, a
    /// <c>dp</c>, or a <c>bank</c> the configuration cannot give. Anything the configuration or a
    /// memory area already says is reported where the entry says it again.
    /// </summary>
    private static Segment Added(Segment segment, ProjectSegment entry, List<Diagnostic> diagnostics)
    {
        var config = segment.Placements[0].File;
        void FromConfig(string key, string source) =>
            diagnostics.Add(new Diagnostic(entry.Declaration, Catalogue.ProjectSegmentFromLink.Message(entry.Name, key, source)));

        segment = segment with { Addition = entry.Declaration };
        if (entry.Size is { } size)
        {
            if (size == AddressSize.Far && segment.Size == AddressSize.Absolute)
                segment = segment with { Size = AddressSize.Far };
            else
                FromConfig("size", $"comes from `type` in `{config}`, and only an absolute segment may be made `far`");
        }
        if (entry.Mirrors is not null)
            FromConfig("mirrors", "is given on the memory area it runs in, under `links`");
        if (entry.Space is not null)
            FromConfig("space", "is given on its link or the memory area it runs in, under `links`");
        if (entry.Bank is not null && segment.Bank is { } bank)
            FromConfig("bank", $"comes from `{config}`, which runs it in bank {StateValue.Hex(bank, 2)}");
        return Registers(segment, entry, bank: segment.Bank is null, diagnostics);
    }

    /// <summary>
    /// Returns a segment with the <c>dp</c> its entry gives, and the <c>bank</c> when
    /// <paramref name="bank"/> allows one, after checking each.
    /// </summary>
    private static Segment Registers(Segment segment, ProjectSegment entry, bool bank, List<Diagnostic> diagnostics)
    {
        if (entry.DirectPage is { } directPage
            && SegmentTable.Check(entry.Name, segment.Size, StateRegister.DirectPage, directPage.Value, entry.Declaration,
                (null, null), diagnostics) is { } page)
        {
            segment = segment with { DirectPage = page };
        }
        if (bank && entry.Bank is { } given
            && SegmentTable.Check(entry.Name, segment.Size, StateRegister.DataBank, given.Value, entry.Declaration,
                (null, null), diagnostics) is { } home)
        {
            segment = segment with { Bank = home };
        }
        return segment;
    }

    private static string Format(AddressSize size) => size switch
    {
        AddressSize.ZeroPage => "zp",
        AddressSize.Absolute => "abs",
        _ => "far",
    };

    /// <summary>Represents a segment as one link places it.</summary>
    /// <param name="Segment">The segment, with what that link's configuration says about it.</param>
    /// <param name="Config">The logical path of the link's configuration.</param>
    /// <param name="Area">What the project says about the memory area it runs in, if anything.</param>
    private sealed record Placed(Segment Segment, string Config, Link.Area? Area);
}
