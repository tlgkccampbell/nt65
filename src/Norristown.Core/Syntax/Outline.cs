namespace Norristown.Syntax;

/// <summary>
/// A file's declarations, nested the way its blocks are: what an editor shows as the
/// document's outline. This is a reading of the syntax and nothing more — a name is
/// whatever the line writes, and no name is resolved or checked.
/// </summary>
public static class Outline
{
    /// <summary>The outline of <paramref name="tree"/>, in source order.</summary>
    public static IReadOnlyList<OutlineItem> Build(SyntaxTree tree)
    {
        var items = new List<OutlineItem>();
        Walk(tree.Root, items);
        return items;
    }

    private static void Walk(SyntaxNode node, List<OutlineItem> items)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.Green is GreenBlock)
                WalkBlock(child, items);
            else if (child.Statement is { } statement)
                AddStatement(child, statement, items);
        }
    }

    private static void WalkBlock(SyntaxNode block, List<OutlineItem> items)
    {
        // The opener and closer lines are children of the block, and neither declares
        // anything a label or constant line would, so walking the block whole is enough.
        var children = new List<OutlineItem>();
        Walk(block, children);

        // A block whose opener declares nothing the outline names — an `.if`, a macro body,
        // a proc whose name is missing — gives its contents to the enclosing item instead of
        // a level of its own, so an outline never hides a declaration behind a broken line.
        var item = block.ChildNodes[0].Statement is { } opener ? Describe(opener, block, children) : null;
        if (item is null)
            items.AddRange(children);
        else
            items.Add(item);
    }

    private static OutlineItem? Describe(SyntaxNode opener, SyntaxNode block, List<OutlineItem> children)
    {
        switch (opener.Kind)
        {
            case SyntaxKind.ProcDeclaration when NameToken(opener) is { } name:
                var signature = opener.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.ProcSignature);
                return new OutlineItem(OutlineKind.Proc, name.Text, signature?.GetText(), block.Span, name.Span, children);

            case SyntaxKind.ScopeDeclaration:
                // `.scope { }` is anonymous, and stands under its own directive.
                var scope = NameToken(opener);
                return new OutlineItem(OutlineKind.Scope, scope?.Text ?? ".scope", null,
                    block.Span, scope?.Span ?? opener.ChildTokens[0].Span, children);

            case SyntaxKind.MacroDeclaration when NameToken(opener) is { } macro:
                // The parameters are what a reader needs beside the name, and the signature
                // after them where there is one: together they are the whole header.
                return new OutlineItem(OutlineKind.Macro, macro.Text, TextAfter(opener, macro)?.TrimEnd('{').TrimEnd(),
                    block.Span, macro.Span, children);

            case SyntaxKind.SegmentBlock or SyntaxKind.SegmentRegion:
                // A name written in quotes is an error that still names the segment, and holds no
                // escapes, so the quotes come off by hand.
                var segment = opener.ChildTokens.Length > 1 ? opener.ChildTokens[1] : opener.ChildTokens[0];
                return new OutlineItem(OutlineKind.Segment, segment.Text.Trim('"'), null, block.Span, segment.Span, children);

            case SyntaxKind.DataDeclaration when NameToken(opener) is { } data:
                return new OutlineItem(OutlineKind.Data, data.Text, TextAfter(opener, data)?.TrimStart(':').Trim().TrimEnd('{').TrimEnd() is { Length: > 0 } detail ? detail : null,
                    block.Span, data.Span, children);

            case SyntaxKind.EnumDeclaration or SyntaxKind.StructDeclaration or SyntaxKind.UnionDeclaration
                when NameToken(opener) is { } type:
                return new OutlineItem(OutlineKind.Type, type.Text, opener.ChildTokens[0].Text, block.Span, type.Span, children);

            default:
                return null;
        }
    }

    private static void AddStatement(SyntaxNode line, SyntaxNode statement, List<OutlineItem> items)
    {
        switch (statement.Kind)
        {
            case SyntaxKind.LabeledLine:
                var label = statement.ChildNodes.FirstOrDefault(c => c.Kind == SyntaxKind.Label);
                if (label is not null && label.ChildTokens.Length > 0)
                {
                    // What follows a label on the same line is its detail: `.byte 1, 2` says
                    // more about a data label than its name alone does.
                    var rest = statement.ChildNodes.FirstOrDefault(c => c.Kind != SyntaxKind.Label);
                    items.Add(new OutlineItem(OutlineKind.Label, label.ChildTokens[0].Text, rest?.GetText(),
                        line.Span, label.ChildTokens[0].Span, []));
                }
                break;

            case SyntaxKind.ConstantDeclaration when statement.ChildTokens.Length > 0:
                var value = statement.ChildNodes.FirstOrDefault();
                items.Add(new OutlineItem(OutlineKind.Constant, statement.ChildTokens[0].Text, value?.GetText(),
                    line.Span, statement.ChildTokens[0].Span, []));
                break;

            case SyntaxKind.DataDeclaration when NameToken(statement) is { } data:
                items.Add(new OutlineItem(OutlineKind.Data, data.Text, TextAfter(statement, data)?.TrimStart(':').Trim(),
                    line.Span, data.Span, []));
                break;

            case SyntaxKind.FuncDeclaration when NameToken(statement) is { } function:
                items.Add(new OutlineItem(OutlineKind.Function, function.Text, TextAfter(statement, function),
                    line.Span, function.Span, []));
                break;

            case SyntaxKind.ExternProcDeclaration when NameToken(statement) is { } name:
                items.Add(new OutlineItem(OutlineKind.Proc, name.Text, TextAfter(statement, name),
                    line.Span, name.Span, []));
                break;
        }
    }

    /// <summary>What a declaration writes after its name: an extern proc's address and signature.</summary>
    private static string? TextAfter(SyntaxNode statement, SyntaxToken name)
    {
        var end = statement.Span.End;
        var start = name.Span.End;
        var text = start < end ? statement.Tree.Text[start..end].Trim() : "";
        return text.Length == 0 ? null : text;
    }

    /// <summary>The name a declaration writes after its directive, or null when it has none.</summary>
    private static SyntaxToken? NameToken(SyntaxNode statement)
    {
        foreach (var token in statement.ChildTokens)
        {
            if (token.Kind is SyntaxKind.Identifier or SyntaxKind.CheapLocal
                or SyntaxKind.Register or SyntaxKind.Mnemonic)
            {
                return token;
            }
        }
        return null;
    }

    private static SyntaxToken? FirstToken(SyntaxNode statement, SyntaxKind kind)
    {
        foreach (var token in statement.ChildTokens)
        {
            if (token.Kind == kind)
                return token;
        }
        return null;
    }
}
