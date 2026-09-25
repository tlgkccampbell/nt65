namespace Norristown.Semantics;

/// <summary>
/// Specifies the kind of a <see cref="Scope"/>. Segment blocks and <c>.if</c> bodies do not start
/// a scope; their contents belong to the scope around them.
/// </summary>
public enum ScopeKind
{
    /// <summary>One file's top level.</summary>
    File,

    /// <summary>A <c>.proc</c> body.</summary>
    Proc,

    /// <summary>A <c>.scope</c> body, named or anonymous.</summary>
    Scope,

    /// <summary>
    /// A <c>.macro</c> body. The scope has the macro's name, so what it declares is named after
    /// the macro in the output, but nothing outside the body can reach any of it.
    /// </summary>
    Macro,

    /// <summary>
    /// A block argument of a macro call. It is the caller's code and reads the caller's
    /// names, but the same block may be spliced in more than one place, so what it declares
    /// is private to it and may only be a cheap local.
    /// </summary>
    BlockArgument,

    /// <summary>
    /// A <c>.repeat</c> or <c>.each</c> body, which holds the name the repetition binds. The body
    /// is emitted once per iteration, so what it declares is a different name on every iteration,
    /// and nothing outside the body can reach any of it.
    /// </summary>
    Repetition,

    /// <summary>
    /// A <c>.data name { }</c> block, which holds its named members and the <c>@</c> labels
    /// private to it.
    /// </summary>
    Data,

    /// <summary>
    /// The body of an <c>.enum</c>, <c>.struct</c> or <c>.union</c>, which is a scope of constants
    /// and offsets rather than of code.
    /// </summary>
    Type,

    /// <summary>
    /// One branch of an <c>.if</c> chain inside a macro body or a repetition, whose condition
    /// depends on the expansion or iteration. Each expansion takes one branch of the chain, so
    /// two branches may declare the same name, and a name in a branch means that branch's own
    /// declaration. What a branch declares is also seen by the rest of the body.
    /// </summary>
    Branch,
}
