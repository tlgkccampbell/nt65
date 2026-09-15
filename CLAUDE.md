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
