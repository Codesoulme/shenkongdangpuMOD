using AIWorldPawnshop.Models;
using System.Text.RegularExpressions;

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
        public int UnitPrice;
        public long? BalanceAfter;
        public string ItemName = string.Empty;
        public string ItemIdentifier = string.Empty;
        public string ItemSpecification = string.Empty;
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
        var folded = new List<LedgerDisplayEntry>();
        foreach (var entry in entries)
        {
            var itemName = EntryItemName(entry);
            var detail = IsItem(entry) ? (string.IsNullOrWhiteSpace(entry.ItemName) ? "" : entry.Detail) : entry.Detail;
            var previous = folded.LastOrDefault();
            // Only adjacent identical item transactions can share a row. This
            // preserves chronology and makes the displayed closing balance
            // the balance after the last transaction in that group.
            if (IsTradeItem(entry) && previous is not null && previous.ItemName == itemName &&
                previous.ItemIdentifier == entry.ItemIdentifier && previous.ItemSpecification == entry.ItemSpecification &&
                previous.UnitPrice == entry.Amount && previous.Direction == entry.Direction &&
                previous.CustomerName == entry.CustomerName && previous.TransactionType == entry.TransactionType &&
                previous.Detail == detail && previous.Negotiation == entry.Negotiation &&
                CanJoinBalances(previous, entry))
            {
                previous.Amount += entry.Amount;
                previous.FoldCount++;
                continue;
            }
            folded.Add(new LedgerDisplayEntry
            {
                Direction = entry.Direction, Amount = entry.Amount, UnitPrice = entry.Amount,
                CustomerName = entry.CustomerName, TransactionType = entry.TransactionType,
                Detail = detail, Negotiation = entry.Negotiation, FoldCount = 1,
                BalanceAfter = entry.BalanceAfter, ItemName = itemName,
                ItemIdentifier = entry.ItemIdentifier, ItemSpecification = entry.ItemSpecification
            });
        }
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

    private static bool CanJoinBalances(LedgerDisplayEntry newer, CreditLedgerEntry older)
    {
        if (!newer.BalanceAfter.HasValue || !older.BalanceAfter.HasValue)
            return newer.BalanceAfter == older.BalanceAfter;
        // Wholesale entries can share the same balance (one payment).
        if (newer.BalanceAfter == older.BalanceAfter) return true;
        var delta = newer.Direction == "收入" ? newer.Amount : -newer.Amount;
        return older.BalanceAfter + delta == newer.BalanceAfter;
    }

    private static bool IsItem(CreditLedgerEntry entry) => !string.IsNullOrWhiteSpace(entry.ItemName) ||
        IsTradeItem(entry);

    private static bool IsTradeItem(CreditLedgerEntry entry) =>
        entry.TransactionType is "向顾客购入" or "售予顾客" or "购买物品" or "出售物品";

    private static string EntryItemName(CreditLedgerEntry entry)
    {
        if (!IsItem(entry)) return string.Empty;
        if (!string.IsNullOrWhiteSpace(entry.ItemName)) return entry.ItemName;
        // Older records appended an internal identifier. Remove only that
        // suffix, never meaningful parentheses such as 瓶装水（大）.
        return Regex.Replace(entry.Detail ?? "", @"（[^（）]+）$", match =>
            Regex.IsMatch(match.Value, @"[a-zA-Z0-9_]{8,}") ? "" : match.Value);
    }

    internal static string ItemText(LedgerDisplayEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ItemName)) return "—";
        var specification = string.IsNullOrWhiteSpace(entry.ItemSpecification) ? "" : $"（{entry.ItemSpecification}）";
        var count = entry.FoldCount > 1 ? $" ×{entry.FoldCount}" : "";
        return entry.ItemName + specification + count;
    }

    internal static string DetailText(LedgerDisplayEntry entry)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.ItemName))
        {
            var label = entry.TransactionType switch
            {
                "鉴定服务收入" => "鉴定服务费",
                "自动贩卖机收入" => "提取收入",
                "市场委托收入" => "委托奖励",
                _ => "成交单价"
            };
            lines.Add($"{label} {entry.UnitPrice:N0}");
        }
        if (!string.IsNullOrWhiteSpace(entry.Detail))
            lines.Add(entry.Detail.Contains("CreditLedgerRuntime") && entry.Detail.Contains("DescribeModCashSource")
                ? "历史记录未保存信用点来源" : entry.Detail);
        if (!string.IsNullOrWhiteSpace(entry.Negotiation)) lines.Add(entry.Negotiation);
        return Value(string.Join("\n", lines));
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
