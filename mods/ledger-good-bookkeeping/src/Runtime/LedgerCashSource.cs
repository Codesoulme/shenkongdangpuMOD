using AIWorldPawnshop.Compatibility;

namespace AIWorldPawnshop.Runtime;

internal sealed class LedgerCashSource
{
    [ThreadStatic] internal static LedgerCashSource? Current;
    internal string Type = string.Empty;
    internal string Detail = string.Empty;
    internal string Customer = string.Empty;
    internal LedgerTradeDetails? Item;
}
