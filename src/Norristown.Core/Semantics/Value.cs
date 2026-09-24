using System.Globalization;
using System.Text;
using Norristown.Processor;

namespace Norristown.Semantics;

/// <summary>
/// Represents what an expression evaluates to: a number, a string, or nothing when the
/// expression names an address or needs something only a later stage knows, such as layout.
/// <para>
/// Arithmetic is 64-bit and signed while nt65 computes, and values in the output must fit
/// ca65's 32 bits. Whether a value fits depends on where it is emitted, such as a data
/// field, an immediate or a declaration, so every range check belongs to the stage that emits
/// the value rather than here.
/// </para>
/// </summary>
/// <param name="Kind">The kind of value held.</param>
/// <param name="Number">The number, when the kind is <see cref="ValueKind.Number"/>.</param>
/// <param name="Text">The text, when the kind is <see cref="ValueKind.String"/>.</param>
public readonly record struct Value(ValueKind Kind, long Number, string? Text)
{
    /// <summary>Represents no value.</summary>
    public static readonly Value Unknown;

    /// <summary>Gets a value indicating whether the value is known at all.</summary>
    public bool IsKnown => Kind != ValueKind.Unknown;

    /// <summary>Gets a value indicating whether the value is a number.</summary>
    public bool IsNumber => Kind == ValueKind.Number;

    /// <summary>Gets a value indicating whether the value is a string.</summary>
    public bool IsString => Kind == ValueKind.String;

    /// <summary>
    /// Gets a value indicating whether the value is a bare word, which only <c>==</c> and
    /// <c>!=</c> accept.
    /// </summary>
    public bool IsWord => Kind == ValueKind.Word;

    /// <summary>Returns a number value.</summary>
    public static Value Of(long number) => new(ValueKind.Number, number, null);

    /// <summary>Returns a string value.</summary>
    public static Value Of(string text) => new(ValueKind.String, 0, text);

    /// <summary>
    /// Returns a condition as the 1 or 0 that a comparison or a logical operator yields.
    /// </summary>
    public static Value Of(bool condition) => Of(condition ? 1L : 0L);

    /// <summary>Returns a bare word value, such as the <c>a</c> of <c>push!(a, x, y)</c>.</summary>
    public static Value Word(string word) => new(ValueKind.Word, 0, word);

    /// <summary>Returns the number, or null when the value is not a number.</summary>
    public long? AsNumber() => IsNumber ? Number : null;

    /// <summary>
    /// Returns the address size a constant value implies: zero page below <c>$100</c>, absolute
    /// below <c>$10000</c>, and far otherwise. A negative number is emitted to fill the width it
    /// is used at, so it implies no size.
    /// </summary>
    public AddressSize? ImpliedAddressSize() => Kind == ValueKind.Number && Number >= 0
        ? Number < 0x100 ? AddressSize.ZeroPage : Number < 0x10000 ? AddressSize.Absolute : AddressSize.Far
        : null;

    /// <summary>
    /// Returns the value as a programmer reads it, with numbers in hexadecimal and strings quoted.
    /// </summary>
    public override string ToString() => Kind switch
    {
        ValueKind.Number => Format(Number),
        ValueKind.String => Quoted(Text ?? ""),
        ValueKind.Word => Text ?? "?",
        _ => "?",
    };

    /// <summary>
    /// Formats a number as nt65 displays it. Numbers are in hexadecimal, padded to a byte, a word
    /// or a long, because every value a programmer hovers over is an address, a mask or a small
    /// count. Small numbers, where hexadecimal would not help, and negative numbers are in
    /// decimal.
    /// </summary>
    private static string Format(long number) => number switch
    {
        < 10 => number.ToString(CultureInfo.InvariantCulture),
        <= 0xff => Hex(number, 2),
        <= 0xffff => Hex(number, 4),
        <= 0xffffff => Hex(number, 6),
        _ => Hex(number, 8),
    };

    /// <summary>
    /// Formats text as a string literal. A byte that is not a printable ASCII character, such as
    /// one with bit 7 set that a function built, is shown as <c>\xHH</c>, so that what is shown
    /// can be typed back in. A character above <c>$ff</c>, which only a charmap can map, is kept
    /// as it was typed.
    /// </summary>
    private static string Quoted(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c is '"' or '\\')
                quoted.Append('\\').Append(c);
            else if (c is < ' ' or (>= '\x7f' and <= '\xff'))
                quoted.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}");
            else
                quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }

    private static string Hex(long number, int digits) =>
        "$" + number.ToString($"x{digits}", CultureInfo.InvariantCulture);
}
