namespace Norristown.Tests;

/// <summary>
/// Pins the area of every catalogue entry. The area decides the heading that
/// <c>nt65 explain --markdown</c> prints an entry under, so moving an entry to another area
/// should be a change made on purpose, here as well as in the catalogue.
/// </summary>
public sealed class CatalogueAreaTests
{
    // The areas in the order the catalogue declares them, each with its entries in name order.
    private static readonly (string Area, string[] Ids)[] expected =
    [
        ("Reading a line",
        [
            "assert-level", "block-brace-ends-the-line", "block-not-closed", "byte-operator-needs-parentheses",
            "ca65-block-end", "ca65-spelling", "ca65-tag", "character-empty", "character-too-long", "const-missing",
            "continuation-outside-expression",
            "data-body-holds-values", "data-body-needs-a-count", "data-needs-a-name", "data-values-need-braces",
            "digits-missing", "directive-after-label", "directive-unknown", "elseif-misplaced",
            "escape-hex-digits", "escape-unknown", "expected-address-size", "expected-brace", "expected-bracket",
            "expected-colon", "expected-comma", "expected-cpu", "expected-data-type", "expected-dot-dot",
            "expected-element-index", "expected-equals", "expected-expression", "expected-kept-registers",
            "expected-label", "expected-member-value", "expected-name", "expected-parameter-kind",
            "expected-parenthesis", "expected-placement", "expected-segment-attribute", "expected-state-item",
            "expected-statement", "expected-text", "export-declares-nothing", "import-holds-no-values",
            "import-needs-an-element-type", "module-name-quoted", "name-after-at", "nesting-too-deep",
            "not-a-function", "number-invalid", "number-separator", "operators-need-parentheses",
            "segment-name-quoted", "state-item-unknown", "stray-dot", "text-unterminated", "unexpected-character",
            "unexpected-token", "unmatched-brace", "unnamed-label",
        ]),
        ("Names",
        [
            "cheap-local-in-a-path", "cheap-local-outside-a-scope", "declaration-in-a-block-argument",
            "declared-in-another-module", "export-ambiguous",
            "export-narrows-address-size", "family-declares-too-much", "family-member-collides",
            "family-misplaced", "family-not-over-an-enum", "fields-need-a-stated-type", "ident-parameter-declared", "label-in-data",
            "label-outside-a-routine", "linker-name-is-an-instruction", "macro-misplaced", "mnemonic-name",
            "module-declared-twice", "module-missing", "module-name-reserved", "module-name-taken",
            "module-names-differ-in-case", "module-not-first", "module-not-in-the-build", "module-unknown",
            "module-used-as-a-name", "multiproc-misplaced", "name-alone-on-a-line", "name-already-declared",
            "name-is-a-module-path", "not-a-macro", "not-a-scope", "not-declared", "not-declared-in",
            "not-exported", "place-misplaced", "place-not-placeable", "placed-nowhere", "placed-twice",
            "placement-cycle", "reexport-module", "reexport-needed", "reexport-star", "register-name",
            "segment-region-misplaced", "signature-item-needs-65816", "signature-missing",
            "signature-set-name-is-an-item", "unused-symbol", "unused-use-item", "use-brings-in-twice",
            "use-collides-with-declaration", "use-misplaced", "use-star-not-a-module",
        ]),
        ("Values",
        [
            "arithmetic-overflow", "binding-not-over-an-enum", "builtin-arguments", "charmap-has-no-entry",
            "condition-is-text", "condition-names-the-program", "condition-uses-a-conditional-declaration",
            "condition-uses-a-measurement", "constant-names-an-address", "countof-has-no-elements",
            "cpu-disagrees", "cpu-under-a-condition", "cycles-needs-a-position", "cycles-span-has-no-bound",
            "data-elsewhere-overruns", "data-has-no-element-type",
            "declaration-in-a-repetition", "defined-in-terms-of-itself", "defined-too-deep", "division-by-zero",
            "each-not-over-a-list", "element-index-not-constant", "element-index-out-of-range", "else-without-if",
            "enum-member-is-not-an-address", "family-member-missing", "function-argument-count", "has-argument",
            "incbin-unreadable", "measures-a-declaration", "member-count-not-a-number", "member-has-no-value",
            "member-reserves-nothing", "not-indexable", "not-measurable", "nothing-to-measure", "number-too-wide",
            "operator-on-text", "repeat-count-negative", "repeat-count-not-constant", "scope-has-no-address",
            "select-arguments", "select-condition-is-text", "select-condition-not-constant", "set-expected",
            "set-item-is-text", "set-out-of-place", "setting-ambiguous", "setting-default-undecided",
            "setting-is-text", "setting-misplaced", "setting-unknown",
            "shift-count-out-of-range", "sizeof-depends-on-alignment", "sizeof-depends-on-expansion",
            "sqrt-of-a-negative", "strcat-not-a-byte", "strsub-out-of-range", "switch-arguments",
            "switch-no-arm", "switch-set-not-constant", "switch-value-not-constant", "target-argument",
            "turn-or-scale-out-of-range",
        ]),
        ("Macros",
        [
            "argument-after-a-named-one", "argument-count", "argument-given-twice", "argument-missing",
            "block-argument-in-parentheses", "block-argument-unexpected", "block-changes-state",
            "block-continues-nothing", "block-parameter-unknown", "comparison-never-holds",
            "const-argument-out-of-range", "declaration-in-a-macro-body", "enum-argument-not-a-member",
            "expansion-limit", "expression-argument-braced", "ident-argument-not-a-name", "macro-names-unexported",
            "macro-recursive", "operand-argument-mode", "operand-argument-parenthesized", "operand-mode-unknown",
            "parameter-after-block", "parameter-after-list", "parameter-kind-not-an-enum",
            "parameter-range-invalid", "parameter-unknown", "repeat-too-many", "word-argument-ambiguous",
            "word-argument-not-listed",
        ]),
        ("Data",
        [
            "address-does-not-fit", "address-negative", "align-boundary-not-constant",
            "align-boundary-not-power-of-two", "align-not-a-declaration", "charmap-value-not-a-byte",
            "element-count-empty", "element-count-mismatch", "element-count-negative",
            "element-count-not-constant", "element-is-one-value", "element-not-a-record", "element-not-a-value",
            "far-address-in-word", "member-count-mismatch", "member-given-twice", "member-needs-a-list",
            "member-needs-a-record", "member-not-text", "member-takes-one-value", "member-text-too-long",
            "member-unknown", "res-count-not-constant", "res-count-out-of-range", "res-not-a-declaration",
            "strz-not-text", "strz-zero-in-text", "text-not-ascii", "union-many-members-given", "value-too-wide",
        ]),
        ("Placement",
        [
            "code-in-a-data-space", "far-needs-65816", "instruction-in-data", "instruction-outside-a-routine",
            "linked-segments-disagree", "operand-in-another-space", "outside-every-segment",
            "padding-outside-a-routine", "segment-attribute-not-constant", "segment-attribute-out-of-range",
            "segment-attribute-twice", "segment-block-redundant", "segment-declared-twice", "segment-dp-not-zp",
            "segment-mirror-invalid", "segment-mirrors-need-a-bank", "segment-not-defined", "segment-not-linked",
            "segment-standard-size", "segment-undeclared", "space-not-a-name", "space-undeclared",
            "transfer-to-another-space",
        ]),
        ("Instructions",
        [
            "address-size-unreachable", "addressing-mode-missing", "addressing-mode-too-narrow",
            "assertion-failed", "branch-operand-not-taken", "branch-out-of-reach", "config-refused",
            "config-warned", "direct-page-form-missing", "direct-page-needs-65816", "direct-page-only",
            "direct-page-prefix-on-symbol", "immediate-missing", "immediate-too-wide", "instruction-not-on-cpu",
            "operand-has-no-next-byte", "operand-is-text", "operand-missing", "operand-not-taken",
            "target-too-far", "target-too-near", "transfer-prefix",
        ]),
        ("Control flow",
        [
            "annotation-about-nothing", "code-label-as-data", "code-unreachable", "computed-jump-unchecked",
            "entry-not-declared", "exported-entry-not-declared", "fallthrough-misplaced",
            "fallthrough-not-a-routine", "fallthrough-not-adjacent", "fallthrough-not-placed",
            "fallthrough-other-segment", "handler-called", "handler-returns-not-rti", "indirect-call-unchecked",
            "indirect-jump-unchecked", "inline-count-not-constant", "inline-data-missing", "jump-into-data",
            "jump-target-not-a-label", "keeps-broken", "keeps-redundant", "label-unreachable",
            "next-not-the-branch-target", "next-successors-known", "next-table-has-no-labels",
            "next-target-not-a-table", "next-target-not-code", "noreturn-returns", "pushed-return-unchecked",
            "reads-undeclared", "routine-runs-off-the-end", "runs-into-data", "saves-not-a-store",
            "self-modifying-unchecked",
            "tail-call-distance-mismatch", "tail-call-to-handler",
        ]),
        ("Processor state",
        [
            "args-not-pushed", "asserted-item-not-restored", "bank-mismatch", "call-distance-mismatch",
            "call-state-mismatch", "call-target-not-a-routine", "call-target-unknown", "direct-page-mismatch",
            "direct-page-out-of-reach", "direct-page-unknown", "ensure-item-not-a-width", "ensure-needs-native",
            "frame-depth-unknown", "frame-gone", "frame-member-not-stack-relative", "frame-not-a-record",
            "frame-past-the-stack", "immediate-in-emulation", "jump-across-banks", "jump-distance-mismatch",
            "jump-leaves-bank", "mirror-bank-mismatch", "range-bank-mismatch", "relative-call-extra-phk",
            "relative-call-needs-phk", "return-distance-mismatch", "return-state-mismatch",
            "state-item-not-a-point", "state-mode-mismatch", "state-outside-a-routine", "state-value-mismatch",
            "state-value-not-constant", "state-value-out-of-range", "state-width-mismatch", "width-in-emulation",
            "width-unknown",
        ]),
        ("Output",
        [
            "c-header-name-left-out", "c-header-untyped", "cannot-be-translated", "export-name-taken",
            "long-line", "omitted-branch", "output-name-collision",
        ]),
        ("The project file",
        [
            "bank-invalid", "banks-not-a-list", "configuration-key-unknown", "configuration-name-invalid",
            "configuration-not-an-object", "configuration-unknown",
            "diagnostic-name-unknown", "diagnostic-not-turned-down", "diagnostic-severity-unknown",
            "link-key-unknown", "link-memory-unknown", "link-not-an-object", "linked-config-invalid",
            "linked-config-unreadable", "project-cpu-unknown", "project-json-invalid", "project-key-unknown",
            "project-not-a-list", "project-not-a-string", "project-not-an-object", "project-segment-from-link",
            "project-segment-key-unknown", "project-segment-not-an-object", "project-segment-size-missing",
            "project-space-holds-unknown", "project-value-not-an-object", "range-invalid", "ranges-overlap",
            "setting-name-invalid", "setting-not-a-number",
        ]),
        ("Signatures",
        [
            "alias-distance-mismatch", "alias-signature-mismatch", "args-not-constant", "args-out-of-range",
            "distance-disagrees", "handler-assumes-state", "handler-declares-an-exit", "handler-distance",
            "handler-keeps", "handler-noreturn", "item-belongs-at-entry", "macro-distance", "macro-keeps",
            "macro-noreturn", "noreturn-declares-an-exit", "saves-in-signature", "signature-item-twice",
            "signature-set-not-a-set",
            "signature-set-not-first", "signature-set-self-reference", "signature-value-not-constant",
            "signature-value-out-of-range", "state-banks-invalid", "state-banks-not-dbr", "unchanged-needs-entry",
        ]),
        ("Suggestions", ["tail-call", "width-already-set"]),
    ];

    [Fact]
    public void EveryEntryIsInItsArea()
    {
        Assert.Equal(expected.Select(area => area.Area), Catalogue.Areas.Select(area => area.Name));
        foreach (var (area, ids) in expected)
        {
            var actual = Catalogue.All.Where(entry => entry.Area.Name == area).Select(entry => entry.Id);
            Assert.Equal(ids, actual);
        }
    }
}
