# nt65

## C# style

`.editorconfig` holds the style rules and their severities. `EnforceCodeStyleInBuild` is on
in `Directory.Build.props` and warnings are errors, so any rule set to `warning` there
fails the build.

### One type per file

Every `.cs` file holds one top-level type, and the file is named after it. Nested types
stay with their parent. As with member ordering, nothing enforces this: the rules that
would (StyleCop's SA1402 and SA1649) need a dependency we do not take, so this section is
the only enforcement there is.

### Member ordering

No analyzer enforces member ordering: first-party has no such rule,
Roslynator's member sort is a manual IDE refactoring rather than a
diagnostic, and StyleCop — which does have one — is deliberately not a
dependency. So this section is the only enforcement there is.

Within a type, order members by kind:

1. Constants
2. Static fields
3. Instance fields
4. Constructors (static constructor first)
5. Properties
6. Methods
7. Nested types

Within each kind, order by accessibility — `public`, then `internal`,
then `protected`, then `private` — and put `static` members before
instance members of the same kind. When a member only makes sense next to
its partner (a property and the method that drives it, an overload set),
keep them together: ordering exists to make types scannable, not to win
an argument.

### Comments

Write doc comments and inline comments in the plain voice of the .NET documentation. No
analyzer checks this either.

- A method summary opens with a third-person verb: *Returns*, *Checks*, *Reports*,
  *Builds*. A property opens with *Gets*, and a bool property with *Gets a value
  indicating whether*. A type summary says in a full sentence what the type represents or
  does. Enum members and record parameters may stay noun phrases.
- Write complete sentences with articles and explicit subjects. Do not follow a colon
  with a chain of parallel fragments ("A thing. The X verbs: this, that, the other").
  Write sentences, or use `<list>`, `<returns>` and `<param>`.
- Every overload says what it does. Never write "The same, for…".
- Use "rather than", "not X" and em-dash asides only when the contrast is information the
  reader needs.
- Define each project term (expansion, placed, family, settled, piece and slot) once, on
  the type that owns it, and link to it with `<see cref>` elsewhere. Use "write" only for
  emitting output.
- Keep sentences under about 35 words. Keep every *why*: change a comment's shape, not its
  substance.

Comments in the examples ported from ca65 projects we do not own, `examples/msbasic` and
`examples/lorom-template`, come from upstream sources; leave them as they are. The monitor
example is nt65's own program, and its comments follow this section like any other code.
