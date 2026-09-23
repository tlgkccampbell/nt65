using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// The block layer: a pass over the lines' brace values (+1, −1 or 0) that never looks inside a
/// line. When the braces balance, the nesting follows directly from the running total of those
/// values. When they do not, two recovery rules keep the damage local:
/// <list type="bullet">
/// <item>a <c>}</c> with no open block is reported and treated as an ordinary line;</item>
/// <item>a <c>.proc</c>, <c>.multiproc</c> or <c>.macro</c> opener inside one of those closes
/// every block back to outside the enclosing one, since none of them may be nested, and a
/// <c>.segment NAME</c> region line closes every block, since it may appear only at file
/// level.</item>
/// </list>
/// <para>
/// A region line at file level opens a block with no brace, which holds every line up to the
/// next region line or the end of the file. A region line anywhere else is an ordinary line,
/// and the binder reports it as misplaced.
/// </para>
/// </summary>
internal static class Blocks
{
    public static GreenFile Build(ImmutableArray<GreenLine> lines, List<Error> errors)
    {
        var balanced = IsBalanced(lines);
        var root = ImmutableArray.CreateBuilder<GreenNode>();
        var stack = new List<Frame>();
        ImmutableArray<GreenNode>.Builder? region = null;

        ImmutableArray<GreenNode>.Builder Current() => stack.Count > 0 ? stack[^1].Children : region ?? root;

        void CloseRegion()
        {
            if (region is not null)
                root.Add(new GreenBlock(region.ToImmutable(), hasCloser: false));
            region = null;
        }

        void Pop(bool hasCloser)
        {
            var frame = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            Current().Add(new GreenBlock(frame.Children.ToImmutable(), hasCloser));
        }

        void PopUnclosed()
        {
            var opener = stack[^1].Line;
            errors.Add(new Error(opener, lines[opener].Tokens.Length - 2, Catalogue.BlockNotClosed));
            Pop(hasCloser: false);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.OpensBlockKind == BlockKind.Region && (stack.Count == 0 || !balanced))
            {
                while (stack.Count > 0)
                    PopUnclosed();
                CloseRegion();
                region = ImmutableArray.CreateBuilder<GreenNode>();
                region.Add(line);
                continue;
            }
            var consumed = false;
            if (line.Closes)
            {
                if (stack.Count == 0)
                {
                    errors.Add(new Error(i, 0, Catalogue.UnmatchedBrace));
                }
                else if (line.Opens)
                {
                    // A continuation: it ends this block and opens the next.
                    Pop(hasCloser: false);
                }
                else
                {
                    stack[^1].Children.Add(line);
                    Pop(hasCloser: true);
                    consumed = true;
                }
            }

            if (line.Opens)
            {
                if (!balanced && IsAnchor(line))
                {
                    var outer = stack.FindIndex(frame => IsAnchor(lines[frame.Line]));
                    while (outer >= 0 && stack.Count > outer)
                        PopUnclosed();
                }
                var children = ImmutableArray.CreateBuilder<GreenNode>();
                children.Add(line);
                stack.Add(new Frame(i, children));
            }
            else if (!consumed)
            {
                Current().Add(line);
            }
        }
        while (stack.Count > 0)
            PopUnclosed();
        CloseRegion();
        return new GreenFile(root.ToImmutable());
    }

    private static bool IsAnchor(GreenLine line) =>
        line.OpensBlockKind is BlockKind.Proc or BlockKind.MultiProc or BlockKind.Macro;

    private static bool IsBalanced(ImmutableArray<GreenLine> lines)
    {
        var depth = 0;
        foreach (var line in lines)
        {
            if (line.Closes && --depth < 0)
                return false;
            if (line.Opens)
                depth++;
        }
        return depth == 0;
    }

    public readonly record struct Error(int Line, int Token, DiagnosticMessage Message);

    private sealed record Frame(int Line, ImmutableArray<GreenNode>.Builder Children);
}
