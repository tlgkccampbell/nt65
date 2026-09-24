using System.Reflection;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Tests.Semantics;

/// <summary>
/// The semantic half of the analysis API, used as an analyzer or an editor feature would use
/// it. Each test is written the way a caller outside nt65 would write it: nothing here reaches
/// for a test helper, and nothing reaches inside the semantic layer.
/// </summary>
public sealed class AnalysisApiTests
{
    private const string Vic = """
        .module hw::vic
        .export BORDER
        BORDER = $d020

        """;

    private const string Main = """
        .module main
        .use hw::vic::BORDER as EDGE

        .export Point, plot

        .struct Point {
        x:      .word
        y:      .word
        }

        .enum Colour {
            black
            white
        }

        .segment BSS
        .data here:  .type Point
        .data cells: .byte[4]

        .segment CODE
        .macro plot(value: Colour) {
            lda #value
            sta EDGE
        }

        .proc main: a8, i8 {
            ldx #.sizeof(Point)
        @loop:
            lda cells,x
            plot!(Colour::white)
            dex
            bne @loop
            rts
        }

        """;

    /// <summary>Checks analyzing a program, and what one file's model contains.</summary>
    [Fact]
    public void TheModelOfAFile()
    {
        var analysis = Compile();
        var model = analysis.ModelFor("main.nt65")!;

        Assert.Equal("main.nt65", model.Tree.Path);
        Assert.Equal([], model.Diagnostics.Select(d => d.Message));
        Assert.Equal(ScopeKind.File, model.FileScope.Kind);
        Assert.Contains(model.Symbols, symbol => symbol.Name == "Point");
        Assert.Contains(model.References, reference => reference.IsDeclaration);
        Assert.Contains(model.Segments.Segments, segment => segment.Name == "CODE");
        Assert.True(model.Configuration.Omitted(model.Tree).Count == 0);
        Assert.Empty(model.Families);
        Assert.Null(model.FamilyAt(model.Tree.GetLine(0).Statement));

        // The model lists what the file's output brings in from elsewhere, and what it uses of
        // its own.
        Assert.Equal(["BORDER"], model.ExternalSymbols.Select(symbol => symbol.Name));
        Assert.Contains(model.Used, symbol => symbol.Name == "cells");
        Assert.Equal(["EDGE"], model.Brought.Keys);
        Assert.Equal("BORDER", model.Brought["EDGE"].Symbol!.Name);
        Assert.Empty(model.Globs);

        // A model made for one file on its own sees no other file, which is how a scratch buffer
        // is analyzed.
        var alone = SemanticModel.Create(SyntaxTree.Parse("alone.nt65", ".module alone\nK = 1\n"), SegmentTable.Standard);
        Assert.Equal(["K"], alone.Symbols.Select(symbol => symbol.Name));
    }

    /// <summary>
    /// Checks what a name in the file refers to, and what a name at a caret could refer to.
    /// </summary>
    [Fact]
    public void WhatANameMeans()
    {
        var model = Compile().ModelFor("main.nt65")!;
        var caret = model.Tree.Text.IndexOf("lda cells,x", StringComparison.Ordinal) + 4;

        // For a name already in the file, the model gives the reference at a position and the
        // symbol it refers to.
        var reference = model.ReferenceAt(caret)!;
        Assert.Equal("cells", reference.Symbol.Name);
        Assert.False(reference.IsDeclaration);
        Assert.Equal(reference.Symbol, model.SymbolAt(model.Tree.Root.FindToken(caret)));
        Assert.Equal(reference.Symbol, model.GetSymbolInfo(
            model.Tree.Root.FindToken(caret).Parent.FirstAncestorOrSelf<NameExpressionSyntax>()!).Symbol);
        Assert.Contains(model.ReferencesTo(reference.Symbol), found => found.IsDeclaration);

        // A name being typed is text rather than a node, and the same lookup answers it.
        Assert.Equal(ScopeKind.Proc, model.ScopeAt(caret).Kind);
        Assert.Equal(reference.Symbol, model.GetSymbolInfo(caret, ["cells"]).Symbol);
        Assert.Equal("y", model.GetSymbolInfo(caret, ["Point", "y"]).Symbol!.Name);
        Assert.Equal(new SymbolInfo(null, "hw"), model.GetSymbolInfo(caret, ["hw"], fromRoot: true));
        Assert.True(model.GetSymbolInfo(caret, ["nothing"]).IsNone);
        Assert.True(SymbolInfo.None.IsNone);

        // The model offers every name that may be used there, each under the spelling that
        // reaches it.
        Assert.Contains(model.LookupNames(caret), found => found.Name == "EDGE");
        Assert.Contains(model.LookupSymbols(caret), symbol => symbol.Name == "main");
        Assert.Equal("@loop", Assert.Single(model.LookupSymbols(caret, "@loop")).DisplayName);
        Assert.Equal("BORDER", Assert.Single(model.LookupSymbols(caret, "EDGE")).Name);
    }

    /// <summary>
    /// Checks what the model knows of one declaration, which is its kind, its location, its value
    /// and what it holds.
    /// </summary>
    [Fact]
    public void WhatADeclarationIs()
    {
        var model = Compile().ModelFor("main.nt65")!;
        var point = model.Symbols.First(symbol => symbol.Name == "Point");
        var white = model.Symbols.First(symbol => symbol.Name == "white");
        var loop = model.Symbols.First(symbol => symbol.DisplayName == "@loop");

        Assert.Equal(SymbolKind.Struct, point.Kind);
        Assert.Equal("structure", point.KindText);
        Assert.Equal("a structure", point.KindPhrase);
        Assert.Equal("main::Point", point.PathName);
        Assert.Equal("Point", point.QualifiedName);
        Assert.Equal("Point", point.FlatName);
        Assert.Equal("main__Point", point.OutputName);
        Assert.Equal("main", point.Module);
        Assert.True(point.IsExported);
        Assert.True(point.IsLayout);
        Assert.True(point.IsReachableByPath);
        Assert.Equal(4, point.Size);
        Assert.Equal(2, point.Count);
        Assert.Equal("main.nt65", point.Tree.Path);
        Assert.Equal(6, point.DeclarationSpan.Line);
        Assert.Equal(model.Tree.Text.IndexOf(".struct Point", StringComparison.Ordinal) + 8, point.NameSpan.Start);

        // A member of a layout is its offset. An enum member is its number.
        var y = point.Body!.FindMember("y")!;
        Assert.Equal(SymbolKind.Member, y.Kind);
        Assert.Equal(2, y.Value.AsNumber());
        Assert.Equal(1, white.Value.AsNumber());
        Assert.True(white.IsEnumMember);

        // A label is an address, and a cheap local belongs to the routine it is declared in.
        Assert.True(loop.IsCheapLocal);
        Assert.True(loop.IsAddress);
        Assert.False(loop.IsReachableByPath);
        Assert.Equal("main", loop.Routine!.Name);
        Assert.Equal(AddressSize.Absolute, loop.AddressSizeIn(model.Tree));
        Assert.Equal(ScopeKind.Proc, loop.Scope.Kind);
        Assert.Equal("label @loop", loop.ToString());

        // A scope is a level of naming, and a name is looked for from the inside out.
        var body = loop.Scope;
        Assert.Equal("main", body.Name);
        Assert.Equal(body, body.Enclosing(ScopeKind.Proc));
        Assert.Equal(body, body.NearestNamed());
        Assert.Equal(model.FileScope, body.Parent);
        Assert.Equal(point, body.Lookup("Point"));
        Assert.Equal(loop, body.LookupCheapLocal("loop"));
        Assert.Null(body.FindMember("Point"));
        Assert.Equal(loop, body.FindCheapLocal("loop"));
        Assert.Contains(body.Symbols, symbol => symbol == loop);
    }

    /// <summary>Checks an expression's value, and how much room a declaration takes.</summary>
    [Fact]
    public void WhatAnExpressionIsWorth()
    {
        var model = Compile().ModelFor("main.nt65")!;
        var sizeof_ = model.Tree.Root.DescendantNodes().OfType<CallExpressionSyntax>().First();
        var cells = model.Tree.Root.DescendantNodes().OfType<DataDeclarationSyntax>()
            .First(data => data.Name.Text == "cells");
        var element = cells.DescendantNodes().OfType<DataDirectiveSyntax>().Single();

        Assert.Equal(4, model.ValueOf(sizeof_).AsNumber());
        Assert.Equal(new DataSize(4, 4), model.RoomFor(element));
        Assert.Equal((4, null), model.ElementsOf(element));
        Assert.Equal(AddressSize.ZeroPage, model.AddressSizeOf(sizeof_));
        Assert.Equal(model.Symbols.First(symbol => symbol.Name == "Point"), model.SymbolOf(sizeof_.Arguments.Arguments[0]));
        Assert.Null(model.ItemsOf(sizeof_));

        // A value with bytes of its own, and an operand nothing else would ever evaluate.
        var problems = new List<Diagnostic>();
        model.Check(sizeof_, problems);
        Assert.Empty(problems);
        Assert.Null(model.BytesOf(sizeof_));
    }

    /// <summary>Checks a macro call, and what one expansion of a body gives its parameters.</summary>
    [Fact]
    public void WhatACallExpandsTo()
    {
        var model = Compile().ModelFor("main.nt65")!;
        var call = model.Tree.Root.DescendantNodes().OfType<MacroCallSyntax>().Single();

        var plot = model.MacroAt(call)!;
        Assert.Equal(SymbolKind.Macro, plot.Kind);
        Assert.Equal(["value"], plot.Parameters.Select(parameter => parameter.Symbol.Name));
        Assert.Contains(plot.Uses, used => used.Used.Name == "BORDER");
        Assert.Empty(plot.Calls);

        var invocation = model.InvocationAt(call)!;
        var value = plot.Parameters[0].Symbol;
        Assert.Equal("Colour::white", invocation.For(value)!.Value!.GetText());

        // A line of a body is read under one expansion of the macro, and that expansion answers
        // what a name's value is there. The declaration a header makes is the one that expansion
        // writes out.
        var on = Expansion.Of(null, call, (BlockSyntax)plot.Definition!);
        Assert.Equal(1, model.ValueOf(model.ArgumentFor(value, on)!.Value!).AsNumber());
        Assert.Equal(on.Call, model.GivenAt(value, on)!.Value.Argument.Value!.Tree.Root.DescendantNodes()
            .OfType<MacroCallSyntax>().Single());
        Assert.Equal("white", model.BindingsOf(on)![value].Member!.Name);
        Assert.Equal("Colour", model.EnumOf(plot.Parameters[0].Accepts)!.Name);
        Assert.Equal("white", model.MemberFor(invocation.For(value)!, null)!.Name);
        Assert.Equal("white", model.MemberOf(plot.Parameters[0].Accepts, invocation.For(value)!.Value, null)!.Name);
        Assert.Null(model.BindingsOf(null));
        Assert.Equal(plot, model.DeclaredBy(((BlockSyntax)plot.Definition!).Opener.Statement, null));
    }

    /// <summary>
    /// Checks what <c>.exprof(p)</c> in a macro body evaluates to at one expansion of the body.
    /// </summary>
    [Fact]
    public void WhatAnOperandsExpressionIs()
    {
        var model = Analysis.Model("""
            .module main
            .macro put(src: operand) {
                .byte .exprof(src)
            }

            .segment RODATA
            .data bytes {
                put!({#5})
            }
            """);
        var call = model.Tree.Root.DescendantNodes().OfType<MacroCallSyntax>().Single();
        var put = model.MacroAt(call)!;
        var exprOf = ((BlockSyntax)put.Definition!).DescendantNodes().OfType<CallExpressionSyntax>().Single();

        var on = Expansion.Of(null, call, (BlockSyntax)put.Definition!);
        Assert.Equal("5", model.ExprOf(exprOf, on)!.GetText().Trim());
        Assert.Null(model.ExprOf(exprOf, null));
    }

    /// <summary>Checks the program the files are part of, and what it can be asked as a whole.</summary>
    [Fact]
    public void TheProgramTheFilesArePartOf()
    {
        var analysis = Compile();
        var program = analysis.Program;
        var border = program.Files.First(file => file.Tree.Path == "hw/vic.nt65").Symbols[0];

        Assert.Equal(["hw/vic.nt65", "main.nt65"], program.Files.Select(file => file.Tree.Path).Order(StringComparer.Ordinal));
        Assert.Empty(program.Diagnostics);
        Assert.Equal(border, program.Current(border));
        Assert.Equal("hw::vic", program.Symbols.ModuleNamed("hw::vic")!.Name);
        Assert.True(program.Symbols.IsModulePath("hw"));
        Assert.Contains(program.Symbols.Modules, module => module.Name == "main");
        Assert.Empty(program.Symbols.Defines);
        Assert.Null(program.Symbols.Define("DEBUG"));
        Assert.Equal(["hw::vic"], program.Symbols.ModulesExporting("BORDER"));
        Assert.Equal(border, program.Symbols.Member(program.Symbols.ModuleNamed("hw::vic")!, "BORDER"));
        Assert.Equal("BSS", program.Segments.Find("BSS")!.Name);

        // These are the references to a name in every file, which are what a rename replaces.
        var everywhere = program.ReferencesTo(border);
        Assert.Equal(
            ["hw/vic.nt65", "hw/vic.nt65", "main.nt65", "main.nt65", "main.nt65"],
            everywhere.Select(found => found.File.Tree.Path));
        Assert.Equal(everywhere, program.ReferencesTo([border]));

        // A program can also be built from trees alone, without laying anything out.
        var trees = analysis.Program.Files.Select(file => file.Tree).ToList();
        Assert.Equal(2, ProgramModel.Create(trees, SegmentTable.Build(trees, [])).Files.Count);
    }

    /// <summary>
    /// Every public member of <see cref="SemanticModel"/> and <see cref="ProgramModel"/> is called
    /// here, so a member these tests do not show being used is either untested or should not be
    /// public.
    /// </summary>
    [Fact]
    public void TheseTestsCallEveryPublicMember()
    {
        var tests = Repo.ReadText(Repo.Path("tests", "Norristown.Tests", "Semantics", "AnalysisApiTests.cs"));
        var missing = new List<string>();
        foreach (var type in new[] { typeof(SemanticModel), typeof(ProgramModel) })
        {
            foreach (var name in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.DeclaredOnly).Select(Named).OfType<string>().Distinct().Order(StringComparer.Ordinal))
            {
                if (!tests.Contains(name, StringComparison.Ordinal))
                    missing.Add($"AnalysisApiTests does not call {type.Name}.{name}");
            }
        }
        Assert.Equal([], missing);
    }

    /// <summary>
    /// Returns the name a member is looked for under, or null for a member that is never called
    /// by name, such as a constructor or an accessor.
    /// </summary>
    private static string? Named(MemberInfo member) => member switch
    {
        MethodInfo { IsSpecialName: true } => null,
        ConstructorInfo => null,
        _ => member.Name,
    };

    /// <summary>Compiles the program every test here asks about.</summary>
    private static ProgramAnalysis Compile() =>
        Compiler.Analyze(
            [new SourceFile("hw/vic.nt65", Vic.ReplaceLineEndings("\n")), new SourceFile("main.nt65", Main.ReplaceLineEndings("\n"))],
            ProjectSettings.None);
}
