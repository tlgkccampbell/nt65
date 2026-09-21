using System.Globalization;

namespace Norristown.Semantics;

/// <summary>
/// What an expression evaluates to: a number, a string, or nothing when the expression
/// names an address or needs a layer that is not online yet.
/// <para>
/// Arithmetic is 64-bit and signed while nt65 computes, and what the output carries has to
/// fit ca65's 32 bits. Where a value fits is a question about the place it is written — a
/// slot, an immediate, a declaration — so every range check belongs to the stage that writes
/// it rather than here.
/// </para>
/// </summary>
/// <param name="Kind">What the value holds.</param>
/// <param name="Number">The number, when the kind is <see cref="ValueKind.Number"/>.</param>
/// <param name="Text">The text, when the kind is <see cref="ValueKind.String"/>.</param>
public readonly record struct Value(ValueKind Kind, long Number, string? Text)
{
    /// <summary>No value.</summary>
    public static readonly Value Unknown;

    /// <summary>Whether the value is known at all.</summary>
    public bool IsKnown => Kind != ValueKind.Unknown;

    /// <summary>Whether the value is a number.</summary>
    public bool IsNumber => Kind == ValueKind.Number;

    /// <summary>Whether the value is a string.</summary>
    public bool IsString => Kind == ValueKind.String;

    /// <summary>Whether the value is a bare word, which only <c>==</c> and <c>!=</c> accept.</summary>
    public bool IsWord => Kind == ValueKind.Word;

    /// <summary>A number.</summary>
    public static Value Of(long number) => new(ValueKind.Number, number, null);

    /// <summary>A string.</summary>
    public static Value Of(string text) => new(ValueKind.String, 0, text);

    /// <summary>A condition, as the 1 or 0 that a comparison or a logical operator yields.</summary>
    public static Value Of(bool condition) => Of(condition ? 1L : 0L);

    /// <summary>A bare word, such as the <c>a</c> of <c>push!(a, x, y)</c>.</summary>
    public static Value Word(string word) => new(ValueKind.Word, 0, word);

    /// <summary>The number, or null when the value is not one.</summary>
    public long? AsNumber() => IsNumber ? Number : null;

    /// <summary>
    /// The address size a constant value implies: below <c>$100</c> zero page, below
    /// <c>$10000</c> absolute, otherwise far. A negative number is written to fill the width
    /// it is used at, so it says nothing about a size.
    /// </summary>
    public AddressSize? ImpliedAddressSize() => Kind == ValueKind.Number && Number >= 0
        ? Number < 0x100 ? AddressSize.ZeroPage : Number < 0x10000 ? AddressSize.Absolute : AddressSize.Far
        : null;

    /// <summary>The value as a programmer reads it: numbers in hexadecimal, strings quoted.</summary>
    public override string ToString() => Kind switch
    {
        ValueKind.Number => Format(Number),
        ValueKind.String => $"\"{Text}\"",
        ValueKind.Word => Text ?? "?",
        _ => "?",
    };

    /// <summary>
    /// A number as nt65 spells it: hexadecimal, padded to a byte, a word or a long, since
    /// every value a programmer hovers over is an address, a mask or a small count. Decimal
    /// where hexadecimal would not help, and negative numbers as themselves.
    /// </summary>
    private static string Format(long number) => number switch
    {
        < 10 => number.ToString(CultureInfo.InvariantCulture),
        <= 0xff => Hex(number, 2),
        <= 0xffff => Hex(number, 4),
        <= 0xffffff => Hex(number, 6),
        _ => Hex(number, 8),
    };

    private static string Hex(long number, int digits) =>
        "$" + number.ToString($"x{digits}", CultureInfo.InvariantCulture);
}
