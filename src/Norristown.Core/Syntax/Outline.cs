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

        // A block whose opener declares nothing the outline names — an `.if`, a macro body,
        // a proc whose name is missing — gives its contents to the enclosing item instead of
        // a level of its own, so an outline never hides a declaration behind a broken line.
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
            case ProcDeclarationSyntax { Name: { } name } proc:
                return new OutlineItem(OutlineKind.Proc, name.Text, proc.Signature?.GetText(), block.Span, name.Span, children);

            // One block, however many routines it declares: the enum it walks and the
            // signature they share are what a reader needs beside the name it binds.
            case MultiProcDeclarationSyntax { Name: { } bound } multiproc:
                return new OutlineItem(OutlineKind.Proc, bound.Text,
                    multiproc.GetText().Trim().TrimEnd('{').TrimEnd()[".multiproc".Length..].Trim(),
                    block.Span, bound.Span, children);

            case ScopeDeclarationSyntax scope:
                // `.scope { }` is anonymous, and stands under its own directive.
                return new OutlineItem(OutlineKind.Scope, scope.Name?.Text ?? ".scope", null,
                    block.Span, scope.Name?.Span ?? scope.Keyword.Span, children);

            case MacroDeclarationSyntax { Name: { } macro }:
                // The parameters are what a reader needs beside the name, and the signature
                // after them where there is one: together they are the whole header.
                return new OutlineItem(OutlineKind.Macro, macro.Text, TextAfter(opener, macro)?.TrimEnd('{').TrimEnd(),
                    block.Span, macro.Span, children);

            case SegmentBlockSyntax or SegmentRegionSyntax:
                // A name written in quotes is an error that still names the segment, and holds no
                // escapes, so the quotes come off by hand.
                var written = (SegmentStatementSyntax)opener;
                var segment = written.Name ?? written.Keyword;
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

            case FuncDeclarationSyntax { Name: { } function }:
                items.Add(new OutlineItem(OutlineKind.Function, function.Text, TextAfter(statement, function),
                    line.Span, function.Span, []));
                break;

            case ExternProcDeclarationSyntax { Name: var name }:
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
}
