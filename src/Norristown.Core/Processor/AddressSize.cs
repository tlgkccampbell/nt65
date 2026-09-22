namespace Norristown.Processor;

/// <summary>
/// How wide an address is. The values are the width in bytes, which is what
/// <c>.addrsize</c> yields.
/// </summary>
public enum AddressSize
{
    /// <summary>Direct page or zero page: one byte, written <c>z:</c>.</summary>
    ZeroPage = 1,

    /// <summary>Absolute: two bytes, written <c>a:</c>.</summary>
    Absolute = 2,

    /// <summary>Long: three bytes, written <c>f:</c>.</summary>
    Far = 3,
}
