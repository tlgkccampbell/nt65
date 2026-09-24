using System.Text.RegularExpressions;
using Norristown.LanguageServer;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Scopes lines with a TextMate grammar the way VS Code's tokenizer, vscode-textmate, runs it, so
/// that tests can check what a grammar colours without an editor. It supports the features the
/// nt65 grammar uses, which are match rules, blocks, captures and includes of the repository. A
/// grammar that relies on something it would scope differently from vscode-textmate is refused
/// with <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class TextMateTokenizer
{
    /// <summary>The tokenizer for the grammar VS Code colours nt65 with.</summary>
    public static readonly TextMateTokenizer Nt65 = new(TextMateGrammar.Root, TextMateGrammar.Repository);

    private readonly IReadOnlyList<TextMateRule> root;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TextMateRule>> repository;
    private readonly Dictionary<TextMateRule, Compiled> compiled = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Initializes a tokenizer for the grammar whose top level is <paramref name="root"/> and whose
    /// includes name lists in <paramref name="repository"/>.
    /// </summary>
    public TextMateTokenizer(IReadOnlyList<TextMateRule> root, IReadOnlyDictionary<string, IReadOnlyList<TextMateRule>> repository)
    {
        this.root = root;
        this.repository = repository;
        Compile(root);
        foreach (var rules in repository.Values)
            Compile(rules);
    }

    /// <summary>
    /// Scopes each character of each line the way a TextMate tokenizer runs the grammar. From the
    /// current position, the end of the innermost open block and every rule it holds are tried.
    /// The leftmost match wins. On a tie the rule listed earlier wins, and the block's end beats
    /// every rule. A block's end may be <c>$</c>, which matches at the end of a line.
    /// </summary>
    /// <returns>
    /// For each line, the scope of each character, or null for a character no rule scopes. Where
    /// groups nest, the innermost group's scope is the one kept, as it is the one a theme colours.
    /// </returns>
    public string?[][] Scope(IReadOnlyList<string> lines)
    {
        var open = new Stack<TextMateRule>();
        var result = new string?[lines.Count][];
        for (var l = 0; l < lines.Count; l++)
        {
            var line = lines[l];
            var scopes = result[l] = new string?[line.Length];
            var pos = 0;
            while (true)
            {
                // The end of the innermost block is tried even at the end of the line, where `$` matches.
                Match? best = null;
                TextMateRule? chosen = null;
                if (open.TryPeek(out var inside) && compiled[inside].End!.Match(line, pos) is { Success: true } end)
                    best = end;
                if (pos < line.Length)
                {
                    foreach (var rule in Flatten(open.Count > 0 ? open.Peek().Patterns : root))
                    {
                        var m = compiled[rule].Regex.Match(line, pos);
                        if (m.Success && (best is null || m.Index < best.Index))
                            (best, chosen) = (m, rule);
                    }
                }
                if (best is null)
                    break;
                if (chosen is null)
                {
                    open.Pop();
                }
                else
                {
                    // vscode-textmate gives up on the rest of the line when a match rule matches
                    // nothing, so a grammar that does this would colour differently in the editor.
                    if (best.Length == 0 && chosen.Begin is null)
                        throw new NotSupportedException($"`{chosen.Match}` matches nothing at {l + 1}:{best.Index + 1}");
                    if (chosen.Captures is { } captures)
                    {
                        for (var g = 1; g < captures.Count; g++)
                            Paint(scopes, best.Groups[g], captures[g]);
                    }
                    else
                    {
                        Paint(scopes, best.Groups[0], chosen.Name);
                    }
                    if (chosen.Begin is not null)
                        open.Push(chosen);
                }
                pos = best.Index + best.Length;
            }
        }
        return result;
    }

    private static void Paint(string?[] scopes, Group group, string? scope)
    {
        if (!group.Success || scope is null)
            return;
        for (var i = group.Index; i < group.Index + group.Length; i++)
            scopes[i] = scope;
    }

    private static Regex ToRegex(string pattern) =>
        pattern.Contains(@"\G", StringComparison.Ordinal)
            ? throw new NotSupportedException($"`{pattern}` uses \\G, which vscode-textmate reads differently from .NET")
            : new Regex(pattern, RegexOptions.CultureInvariant);

    private void Compile(IEnumerable<TextMateRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Include is not null || compiled.ContainsKey(rule))
                continue;
            if (rule.Begin is not null && rule.Name is not null)
                throw new NotSupportedException($"the block `{rule.Begin}` has a name, which would scope its whole body");
            compiled[rule] = new Compiled(ToRegex(rule.Match ?? rule.Begin!), rule.End is null ? null : ToRegex(rule.End));
            Compile(rule.Patterns);
        }
    }

    private IEnumerable<TextMateRule> Flatten(IEnumerable<TextMateRule> rules) =>
        rules.SelectMany(rule => rule.Include is { } name ? Flatten(repository[name]) : [rule]);

    private sealed record Compiled(Regex Regex, Regex? End);
}
