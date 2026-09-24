using System.Globalization;

namespace Norristown.Emit;

/// <summary>
/// Formats the numbers nt65 works out for itself as the output writes them, in lower-case
/// hexadecimal.
/// </summary>
internal static class Ca65Numbers
{
    /// <summary>Formats a number as the output writes it, in hexadecimal at the width it is used at.</summary>
    public static string Hex(long value, int digits) =>
        "$" + value.ToString($"x{digits}", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a known value, written in place of the name that represented it, at the narrowest
    /// width that holds it. A negative number is written in decimal, because in hexadecimal it
    /// would be sixteen digits wide, a width nt65 never intended.
    /// </summary>
    public static string Constant(long value) => value < 0
        ? value.ToString(CultureInfo.InvariantCulture)
        : Hex(value, value < 0x100 ? 2 : value < 0x10000 ? 4 : 8);
}
