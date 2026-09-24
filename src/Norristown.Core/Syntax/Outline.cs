namespace Norristown.Syntax;

/// <summary>
/// Builds a file's declarations, nested the way its blocks are, which an editor shows as the
/// document's outline. The outline reads only the syntax. Each name is taken as it appears on the
/// line, and no name is resolved or checked.
/// </summary>
public static class Outline
{
    /// <summary>Returns the outline of <paramref name="tree"/>, in source order.</summary>
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
            if (child is BlockSyntax block)
                WalkBlock(block, items);
            else if (child is LineSyntax line)
                AddStatement(line, items);
        }
    }

    private static void WalkBlock(BlockSyntax block, List<OutlineItem> items)
    {
        // The opener and closer lines are children of the block, and neither declares
        // anything a label or constant line would, so walking the block whole is enough.
        var children = new List<OutlineItem>();
        Walk(block, children);

        // A block whose opener declares nothing the outline names, such as an `.if`, a macro
        // body or a proc whose name is missing, gives its contents to the enclosing item instead
        // of a level of its own. That way an outline never hides a declaration behind a broken
        // line.
        var item = Describe(block.Opener.Statement, block, children);
        if (item is null)
            items.AddRange(children);
        else
            items.Add(item);
    }

    private static OutlineItem? Describe(StatementSyntax opener, BlockSyntax block, List<OutlineItem> children)
    {
        switch (opener)
        {
            case ProcDeclarationSyntax { Name: { IsMissing: false } name } proc:
                return new OutlineItem(OutlineKind.Proc, name.Text, proc.Signature?.GetText(), block.Span, name.Span, children);

            // The block gets one item, regardless of how many routines it declares. Its detail
            // is the enum it iterates over and the signature the routines share.
            case MultiProcDeclarationSyntax { Name: { IsMissing: false } bound } multiproc:
                return new OutlineItem(OutlineKind.Proc, bound.Text,
                    multiproc.GetText().Trim().TrimEnd('{').TrimEnd()[".multiproc".Length..].Trim(),
                    block.Span, bound.Span, children);

            case ScopeDeclarationSyntax scope:
                // `.scope { }` is anonymous, so the item is labelled with the directive itself.
                return new OutlineItem(OutlineKind.Scope, scope.Name?.Text ?? ".scope", null,
                    block.Span, scope.Name?.Span ?? scope.Keyword.Span, children);

            case MacroDeclarationSyntax { Name: { IsMissing: false } macro }:
                // Besides the name, a reader needs the parameters and, if there is one, the
                // signature after them. Together they make up the whole header.
                return new OutlineItem(OutlineKind.Macro, macro.Text, TextAfter(opener, macro)?.TrimEnd('{').TrimEnd(),
                    block.Span, macro.Span, children);

            case SegmentBlockSyntax or SegmentRegionSyntax:
                // A segment name in quotes is an error, but it still names the segment.
                // It cannot contain escapes, so removing the quotes is enough.
                var written = (SegmentStatementSyntax)opener;
                var segment = written.Name.IsMissing ? written.Keyword : written.Name;
                return new OutlineItem(OutlineKind.Segment, segment.Text.Trim('"'), null, block.Span, segment.Span, children);

            case DataDeclarationSyntax { Name: { IsMissing: false } data }:
                return new OutlineItem(OutlineKind.Data, data.Text, TextAfter(opener, data)?.TrimStart(':').Trim().TrimEnd('{').TrimEnd() is { Length: > 0 } detail ? detail : null,
                    block.Span, data.Span, children);

            case TypeDeclarationSyntax { Name: { } type } declaration
                when declaration is EnumDeclarationSyntax or StructDeclarationSyntax or UnionDeclarationSyntax:
                return new OutlineItem(OutlineKind.Type, type.Text, declaration.Keyword.Text, block.Span, type.Span, children);

            default:
                return null;
        }
    }

    private static void AddStatement(LineSyntax line, List<OutlineItem> items)
    {
        var statement = line.Statement;
        switch (statement)
        {
            case LabeledLineSyntax labeled:
                // What follows a label on the same line is its detail: `.byte 1, 2` says
                // more about a data label than its name alone does.
                var rest = labeled.Statement;
                items.Add(new OutlineItem(OutlineKind.Label, labeled.Label.Name.Text, rest?.GetText(),
                    line.Span, labeled.Label.Name.Span, []));
                break;

            case ConstantDeclarationSyntax constant:
                items.Add(new OutlineItem(OutlineKind.Constant, constant.Name.Text, constant.Value.GetText(),
                    line.Span, constant.Name.Span, []));
                break;

            case DataDeclarationSyntax { Name: { IsMissing: false } data }:
                items.Add(new OutlineItem(OutlineKind.Data, data.Text, TextAfter(statement, data)?.TrimStart(':').Trim(),
                    line.Span, data.Span, []));
                break;

            case FuncDeclarationSyntax { Name: { IsMissing: false } function }:
                items.Add(new OutlineItem(OutlineKind.Function, function.Text, TextAfter(statement, function),
                    line.Span, function.Span, []));
                break;

            case ExternProcDeclarationSyntax { Name: var name }:
                items.Add(new OutlineItem(OutlineKind.Proc, name.Text, TextAfter(statement, name),
                    line.Span, name.Span, []));
                break;
        }
    }

    /// <summary>
    /// Returns the declaration's text after its name, trimmed, or null if there is none. For
    /// example, this is an extern proc's address and signature.
    /// </summary>
    private static string? TextAfter(SyntaxNode statement, SyntaxToken name)
    {
        var end = statement.Span.End;
        var start = name.Span.End;
        var text = start < end ? statement.Tree.Text[start..end].Trim() : "";
        return text.Length == 0 ? null : text;
    }
}
