namespace Norristown.Processor;

/// <summary>
/// Specifies how wide an address is. Each value is the width in bytes, which is the value
/// <c>.addrsize</c> yields.
/// </summary>
public enum AddressSize
{
    /// <summary>Direct page or zero page: one byte, with the prefix <c>z:</c>.</summary>
    ZeroPage = 1,

    /// <summary>Absolute: two bytes, with the prefix <c>a:</c>.</summary>
    Absolute = 2,

    /// <summary>Long: three bytes, with the prefix <c>f:</c>.</summary>
    Far = 3,
}
