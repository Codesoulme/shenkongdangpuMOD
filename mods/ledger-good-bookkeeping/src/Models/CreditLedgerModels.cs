namespace AIWorldPawnshop.Models;

public sealed class CreditLedgerEntry
{
    public string EntryId { get; set; } = Guid.NewGuid().ToString("N");
    public int Day { get; set; }
    public string Direction { get; set; } = string.Empty;
    public int Amount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string TransactionType { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Negotiation { get; set; } = string.Empty;
}

public sealed class CreditLedgerData
{
    public List<CreditLedgerEntry> Entries { get; set; } = new();
    public Dictionary<string, List<CreditLedgerEntry>> SaveSnapshots { get; set; } =
        new(StringComparer.Ordinal);
}
