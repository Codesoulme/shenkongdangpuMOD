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
    // Nullable for records written before balances were captured.
    public long? BalanceAfter { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string ItemIdentifier { get; set; } = string.Empty;
    public string ItemSpecification { get; set; } = string.Empty;
}

public sealed class CreditLedgerData
{
    // Checkpoints and their incremental journal share this crash-safe epoch.
    public string JournalGeneration { get; set; } = string.Empty;
    public List<CreditLedgerEntry> Entries { get; set; } = new();
    public Dictionary<string, List<CreditLedgerEntry>> SaveSnapshots { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed class CreditLedgerJournalBatch
{
    public string Generation { get; set; } = string.Empty;
    public List<CreditLedgerEntry> Entries { get; set; } = new();
}
