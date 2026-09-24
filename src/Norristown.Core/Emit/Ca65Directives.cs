using Norristown.Layout;
using Norristown.Syntax;

namespace Norristown.Emit;

/// <summary>
/// Chooses the ca65 directives the output writes data with. It maps nt65's element types to the
/// ca65 directives that hold them and splits a reservation into the <c>.res</c> directives ca65
/// accepts.
/// </summary>
internal static class Ca65Directives
{
    /// <summary>
    /// Returns the ca65 directive for one of nt65's element types. ca65's 24-bit directive is
    /// <c>.faraddr</c> and its one big-endian directive <c>.dbyt</c>; a wider big-endian value
    /// is written as its bytes. Every other element type keeps <paramref name="spelled"/>, the
    /// text it is written with.
    /// </summary>
    public static string ForCa65(DirectiveKind directive, string spelled) => directive switch
    {
        DirectiveKind.Long => ".faraddr",
        DirectiveKind.BeWord => ".dbyt",
        DirectiveKind.BeLong or DirectiveKind.BeDword => ".byte",
        _ => spelled,
    };

    /// <summary>
    /// Returns how wide one element of an element type is, and whether its bytes are written high
    /// first.
    /// </summary>
    public static (int Width, bool BigEndian) ElementFormat(DataDirectiveSyntax directive)
    {
        var kind = directive.Directive.DirectiveKind;
        return (SyntaxFacts.ElementSize(kind) ?? 1, kind is DirectiveKind.BeWord or DirectiveKind.BeLong or DirectiveKind.BeDword);
    }

    /// <summary>
    /// Returns how a run of reserved bytes is split across <c>.res</c> directives. ca65 reserves at
    /// most <see cref="DataLengths.MaxReservation"/> bytes in one of them, and that limit should
    /// not restrict what a program may declare, so a bigger reservation is written as several.
    /// </summary>
    public static IEnumerable<long> Reservations(long bytes)
    {
        if (bytes <= DataLengths.MaxReservation)
        {
            yield return bytes;
            yield break;
        }
        for (var left = bytes; left > 0; left -= DataLengths.MaxReservation)
            yield return Math.Min(left, DataLengths.MaxReservation);
    }
}
