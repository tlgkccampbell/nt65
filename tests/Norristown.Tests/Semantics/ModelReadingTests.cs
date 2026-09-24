using System.Collections.Concurrent;
using System.Text;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// A model is built once and is then read-only, which an editor relies on: it keeps one model
/// and queries it from whatever thread a request arrives on. So no query may be the first to
/// work something out, and no query may write to a symbol another thread is reading.
/// </summary>
public sealed class ModelReadingTests
{
    /// <summary>
    /// Gets a program with everything a question could walk into. It has types laid out from other
    /// types, data declared as one of those types, a macro whose body names another file's
    /// constant, and a repetition that writes a body out once per member.
    /// </summary>
    private static (string Path, string Text)[] Files =>
    [
        ("defs.nt65", """
            .module defs
            .export Point, Colour, SCREEN, plot

            SCREEN = $0400

            .struct Point {
            x:      .word
            y:      .word
            }

            .enum Colour {
                black
                white
            }

            .macro plot(at) {
                lda #<at
                sta SCREEN
            }

            """),
        ("main.nt65", """
            .module main
            .use defs::{Point, Colour, SCREEN, plot}

            .struct Line {
            from:   .type Point
            to:     .type Point
            }

            .union Word {
            whole:  .word
            parts:  .byte[2]
            }

            .segment BSS
            .data here:  .type Line
            .data cells: .byte[.sizeof(Line)]

            .segment CODE
            .proc main {
                lda #.countof(Colour)
                lda here + Line::to + Point::y
                plot!(SCREEN)
            .each c in Colour {
                lda #Colour::c
            }
                rts
            }

            """),
    ];

    /// <summary>
    /// Every declared type is laid out, and every <c>.type T</c> resolved, when the program's
    /// symbols are evaluated, whether or not anything names them. A query made later only reads
    /// those results. It is never the first to work one out, because that would write to a
    /// symbol.
    /// </summary>
    [Fact]
    public void EveryTypeIsLaidOutBeforeAnythingIsAsked()
    {
        var program = Fresh();
        var symbols = program.Files.SelectMany(file => file.Symbols).ToList();
        var types = symbols.Where(symbol => symbol.IsLayout).ToList();

        Assert.Equal(["Line", "Point", "Word"], types.Select(type => type.Name).Order(StringComparer.Ordinal));
        foreach (var type in types)
        {
            Assert.NotNull(type.Size);
            foreach (var member in type.Body?.Symbols ?? [])
                Assert.True(member.Value.IsKnown, $"{type.Name}::{member.Name} has no offset");
        }
        foreach (var named in symbols.Where(symbol => symbol.TypeExpression is not null))
            Assert.NotNull(named.Type);
    }

    /// <summary>
    /// A fresh model asked everything at once from many threads gives every one of them the
    /// same answers, and is itself exactly what it was before they asked.
    /// </summary>
    [Fact]
    public void AFreshModelIsAskedEverythingFromManyThreadsAtOnce()
    {
        var program = Fresh();
        var before = Snapshot(program);

        var answers = new ConcurrentBag<string>();
        Parallel.For(0, 8, _ => answers.Add(Everything(program)));

        Assert.Equal(8, answers.Count);
        Assert.Single(answers.Distinct(StringComparer.Ordinal));
        Assert.Equal(before, Snapshot(program));
    }

    /// <summary>
    /// Returns the program as the model alone builds it, before any layout has run or any query
    /// has been made.
    /// </summary>
    private static ProgramModel Fresh()
    {
        var trees = Files.Select(file => SyntaxTree.Parse(file.Path, file.Text.ReplaceLineEndings("\n"))).ToList();
        return ProgramModel.Create(trees, SegmentTable.Build(trees, []));
    }

    /// <summary>
    /// Returns everything held on a program's symbols that working something out later would
    /// change.
    /// </summary>
    private static string Snapshot(ProgramModel program) =>
        string.Join("\n", program.Files
            .SelectMany(file => file.Symbols)
            .Select(symbol => $"{symbol.PathName} {symbol.Kind} {symbol.Value} {symbol.Size} {symbol.Count} "
                + $"{symbol.AddressSize} {symbol.Type} {symbol.IsExported} {symbol.Calls.Count} {symbol.Uses.Count}"));

    /// <summary>Returns everything a model answers, asked of every place in every file.</summary>
    private static string Everything(ProgramModel program)
    {
        var text = new StringBuilder();
        foreach (var model in program.Files)
        {
            foreach (var start in model.Tree.LineStarts)
            {
                text.Append($"{model.ScopeAt(start)} ")
                    .AppendJoin(',', model.LookupNames(start).Select(found => $"{found.Name}={found.Means.Symbol}"))
                    .Append('\n');
            }
            foreach (var reference in model.References)
            {
                text.Append($"{reference.Symbol} {model.ReferencesTo(reference.Symbol).Count} ")
                    .Append($"{program.ReferencesTo(reference.Symbol).Count}\n");
            }
            foreach (var node in model.Tree.Root.DescendantNodes())
            {
                switch (node)
                {
                    case NameExpressionSyntax name:
                        text.Append($"{model.SymbolOf(name)} ")
                            .Append($"{model.GetSymbolInfo(name.Span.Start, [.. name.Names.Select(part => part.Text)])}\n");
                        break;
                    case DataDirectiveSyntax data:
                        text.Append($"{model.ElementsOf(data)} {model.RoomFor(data)}\n");
                        break;
                    case StatementSyntax statement:
                        text.Append($"{model.RoomFor(statement)} {model.DeclaredBy(statement, null)}\n");
                        break;
                    case ExpressionSyntax expression:
                        text.Append($"{model.ValueOf(expression)} {model.AddressSizeOf(expression)} ")
                            .Append($"{model.BytesOf(expression)?.Count}\n");
                        break;
                    default:
                        break;
                }
            }
        }
        return text.ToString();
    }
}
