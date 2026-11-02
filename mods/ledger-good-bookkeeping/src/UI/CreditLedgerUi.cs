using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.UI;

internal static class CreditLedgerUi
{
    internal sealed class LedgerDisplayEntry
    {
        public string Direction = string.Empty;
        public long Amount;
        public string CustomerName = string.Empty;
        public string TransactionType = string.Empty;
        public string Detail = string.Empty;
        public string Negotiation = string.Empty;
        public int FoldCount;
    }

    internal sealed class PageData
    {
        public int Page;
        public int PageCount;
        public int TotalCount;
        public long Income;
        public long Expense;
        public List<LedgerDisplayEntry> Entries = new();
    }

    internal static PageData Read(IEnumerable<CreditLedgerEntry>? ledger, int day, int page, int pageSize)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        // Persisted list order is chronological; random GUIDs are not timestamps.
        var chronological = (ledger ?? Enumerable.Empty<CreditLedgerEntry>()).Where(x => x.Day == day).ToList();
        var normalized = new List<CreditLedgerEntry>(chronological.Count);
        for (var i = 0; i < chronological.Count; i++)
        {
            var current = chronological[i];
            if (i + 1 < chronological.Count && IsLegacyDoubleEntry(current, chronological[i + 1]))
            {
                // Before 0.4.5, buying an item triggered both callbacks and
                // was saved as an income+expense pair. Keep the real expense.
                normalized.Add(current.Direction == "支出" ? current : chronological[i + 1]);
                i++;
                continue;
            }
            normalized.Add(current);
        }
        var entries = normalized.AsEnumerable().Reverse().ToList();
        var folded = entries
            .GroupBy(x => (x.Direction, x.CustomerName, x.TransactionType,
                ItemName: ItemName(x.Detail), x.Negotiation))
            .Select(group => new LedgerDisplayEntry
            {
                Direction = group.Key.Direction,
                Amount = group.Sum(x => (long)x.Amount),
                CustomerName = group.Key.CustomerName,
                TransactionType = group.Key.TransactionType,
                Detail = group.Key.ItemName,
                Negotiation = group.Key.Negotiation,
                FoldCount = group.Count()
            })
            .ToList();
        var count = Math.Max(1, (folded.Count + pageSize - 1) / pageSize);
        page = Math.Max(0, Math.Min(page, count - 1));
        return new PageData
        {
            Page = page, PageCount = count, TotalCount = normalized.Count,
            Income = entries.Where(x => x.Direction == "收入").Sum(x => (long)x.Amount),
            Expense = entries.Where(x => x.Direction == "支出").Sum(x => (long)x.Amount),
            Entries = folded.Skip(page * pageSize).Take(pageSize).ToList()
        };
    }

    private static string ItemName(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "—";
        var identifierStart = detail.IndexOf('（');
        return identifierStart > 0 ? detail[..identifierStart] : detail;
    }

    private static bool IsLegacyDoubleEntry(CreditLedgerEntry first, CreditLedgerEntry second) =>
        first.Day == second.Day &&
        first.Amount > 0 && first.Amount == second.Amount &&
        string.Equals(first.CustomerName, second.CustomerName, StringComparison.Ordinal) &&
        string.Equals(first.Detail, second.Detail, StringComparison.Ordinal) &&
        ((first.Direction == "收入" && second.Direction == "支出" &&
          first.TransactionType == "出售物品" && second.TransactionType == "购买物品") ||
         (first.Direction == "支出" && second.Direction == "收入" &&
          first.TransactionType == "购买物品" && second.TransactionType == "出售物品"));

    internal static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
