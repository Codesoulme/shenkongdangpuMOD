using System.Globalization;
using System.Text.RegularExpressions;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

internal static class TradeProposalResolver
{
    private const int MaxPercent = 50;
    private static readonly Regex FoldRegex = new(
        @"(?<value>[零〇一二两三四五六七八九十0-9]+(?:[点\.][零〇一二三四五六七八九0-9]+)?)\s*折",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PercentRegex = new(
        @"(?:(?<number>\d{1,3}(?:\.\d+)?)\s*%|百分之\s*(?<chinese>[零〇一二两三四五六七八九十百]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AmountRegex = new(
        @"(?<amount>\d[\d,]*)\s*(?:€|元|信用点|credits?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BareAmountRegex = new(
        @"(?<!\d)(?<amount>\d{1,6})(?!\d|\.\d|\s*(?:%|折|天|件|个|岁|号|轮))|" +
        @"(?<![零〇一二两三四五六七八九十百])(?<chinese_amount>[零〇一二两三四五六七八九十百]+)" +
        @"(?![零〇一二两三四五六七八九十百]|\s*(?:%|折|天|件|个|岁|号|轮))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static TradeProposal Resolve(
        string playerText,
        NativeTradeContext trade,
        TradeProposal? generated,
        int currentItemCount = 1)
    {
        if (!trade.Available || string.IsNullOrWhiteSpace(playerText))
            return None("当前没有可议价的原版交易");

        var text = playerText.Trim();
        var deterministic = ParseDeterministic(text, trade);
        if (deterministic.Kind != "none")
            return WithScope(deterministic, text, currentItemCount);

        if (generated is null || !generated.Explicit)
            return None("玩家没有提出明确的价格变动");

        var kind = NormalizeKind(generated.Kind);
        if (!IsCompatible(kind, trade.Direction))
            return None("生成的议价方向与当前原版交易不一致");

        var percent = generated.Percent;
        long? targetAmount = null;
        if (percent <= 0 && generated.TargetAmount is > 0 &&
            ContainsNumber(text, generated.TargetAmount.Value))
        {
            targetAmount = generated.TargetAmount;
            (kind, percent) = ResolveTargetAmount(
                generated.TargetAmount.Value,
                trade.FinalOfferValue,
                trade.Direction);
        }

        if (!ContainsAnyPriceNumber(text) || percent is < 1 or > MaxPercent)
            return None("议价幅度没有在玩家原话中得到确认，或超出 1% 到 50% 的限制");

        return WithScope(new TradeProposal
        {
            Kind = kind,
            Percent = percent,
            TargetAmount = targetAmount,
            Explicit = true,
            Reason = "已由确定性规则核对玩家原话"
        }, text, currentItemCount);
    }

    private static TradeProposal ParseDeterministic(
        string text,
        NativeTradeContext trade)
    {
        if (text.Contains("半价", StringComparison.OrdinalIgnoreCase))
            return CreateDirectional("discount", 50, null, trade.Direction, "玩家明确提出半价");

        var fold = FoldRegex.Match(text);
        if (fold.Success && TryParseFold(fold.Groups["value"].Value, out var discountPercent))
        {
            return discountPercent is >= 1 and <= MaxPercent
                ? CreateDirectional("discount", discountPercent, null, trade.Direction, "玩家明确提出折扣")
                : None("折扣幅度超出 1% 到 50% 的限制");
        }

        var percentMatch = PercentRegex.Match(text);
        var percent = ReadPercent(percentMatch);
        if (percent is >= 1 and <= MaxPercent)
        {
            if (ContainsAny(text, "优惠", "便宜", "少收", "降价", "减价", "打折", "折扣"))
                return CreateDirectional("discount", percent, null, trade.Direction, "玩家明确提出降价");
            if (ContainsAny(text, "加价", "多给", "多出", "多付", "多收", "提高", "涨价", "再加"))
                return CreateDirectional("markup", percent, null, trade.Direction, "玩家明确提出加价");
        }

        var amountMatch = AmountRegex.Match(text);
        if (amountMatch.Success &&
            long.TryParse(
                amountMatch.Groups["amount"].Value.Replace(",", string.Empty),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var target) &&
            target > 0)
        {
            var (kind, targetPercent) = ResolveTargetAmount(
                target,
                trade.FinalOfferValue,
                trade.Direction);
            if (targetPercent is >= 1 and <= MaxPercent && kind != "none")
            {
                return new TradeProposal
                {
                    Kind = kind,
                    Percent = targetPercent,
                    TargetAmount = target,
                    Explicit = true,
                    Reason = "玩家明确提出目标金额"
                };
            }
        }

        var bareTarget = ExtractBareTargetAmount(text);
        if (bareTarget is > 0)
        {
            var (bareKind, barePercent) = ResolveTargetAmount(
                bareTarget.Value,
                trade.FinalOfferValue,
                trade.Direction);
            if (barePercent is >= 1 and <= MaxPercent && bareKind != "none")
            {
                return new TradeProposal
                {
                    Kind = bareKind,
                    Percent = barePercent,
                    TargetAmount = bareTarget,
                    Explicit = true,
                    Reason = "玩家在成交语境中明确提出目标金额"
                };
            }
        }

        return None("未发现可安全执行的明确议价");
    }

    private static long? ExtractBareTargetAmount(string text)
    {
        if (!ContainsAny(
                text,
                "成交",
                "报价",
                "价格",
                "出价",
                "便宜",
                "加价",
                "降价",
                "给你",
                "卖你",
                "买",
                "卖",
                "就"))
        {
            return null;
        }

        var candidates = BareAmountRegex.Matches(text)
            .Cast<Match>()
            .Select(match =>
            {
                if (long.TryParse(
                        match.Groups["amount"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var arabic))
                {
                    return (Index: match.Index, Value: arabic);
                }

                return TryParseChineseInteger(
                           match.Groups["chinese_amount"].Value,
                           out var chinese)
                    ? (Index: match.Index, Value: (long)chinese)
                    : (Index: match.Index, Value: 0L);
            })
            .Where(candidate => candidate.Value > 0)
            .ToList();
        if (candidates.Count == 0)
            return null;

        var cueIndex = new[] { "成交", "报价", "价格", "出价" }
            .Select(cue => text.IndexOf(cue, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        var beforeCue = cueIndex >= 0
            ? candidates.Where(candidate => candidate.Index < cueIndex).ToList()
            : new List<(int Index, long Value)>();
        return (beforeCue.Count > 0 ? beforeCue : candidates)[^1].Value;
    }

    private static TradeProposal CreateDirectional(
        string change,
        int percent,
        long? target,
        string direction,
        string reason)
    {
        var kind = (direction, change) switch
        {
            ("player_sells_to_client", "discount") => "offer_discount",
            ("player_sells_to_client", "markup") => "ask_markup",
            ("player_buys_from_client", "discount") => "ask_buying_discount",
            ("player_buys_from_client", "markup") => "offer_buying_markup",
            _ => "none"
        };

        return new TradeProposal
        {
            Kind = kind,
            Percent = percent,
            TargetAmount = target,
            Explicit = kind != "none",
            Reason = reason
        };
    }

    private static (string Kind, int Percent) ResolveTargetAmount(
        long target,
        long? currentOffer,
        string direction)
    {
        if (currentOffer is null || currentOffer <= 0 || target == currentOffer.Value)
            return ("none", 0);

        var percent = (int)Math.Round(
            Math.Abs(target - currentOffer.Value) * 100d / currentOffer.Value,
            MidpointRounding.AwayFromZero);
        var change = target < currentOffer.Value ? "discount" : "markup";
        return (CreateDirectional(change, percent, target, direction, string.Empty).Kind, percent);
    }

    private static bool TryParseFold(string raw, out int discountPercent)
    {
        discountPercent = 0;
        if (!TryParseFoldValue(raw, out var fold) || fold <= 0 || fold >= 10)
            return false;

        discountPercent = (int)Math.Round(100d - fold * 10d, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryParseFoldValue(string raw, out double value)
    {
        value = 0;
        raw = raw.Replace('点', '.');
        if (double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            if (value > 10 && value < 100 && !raw.Contains('.'))
                value /= 10d;
            return true;
        }

        var dotIndex = raw.IndexOf('.');
        if (dotIndex >= 0)
        {
            var integerText = raw[..dotIndex];
            var fractionText = raw[(dotIndex + 1)..];
            if (!TryParseChineseInteger(integerText, out var integer) ||
                !TryParseChineseDigits(fractionText, out var fraction, out var digits))
                return false;

            value = integer + fraction / Math.Pow(10, digits);
            return true;
        }

        if (raw.Length == 2 && raw.All(IsChineseDigit))
        {
            value = ChineseDigit(raw[0]) + ChineseDigit(raw[1]) / 10d;
            return true;
        }

        if (raw.Length == 1 && IsChineseDigit(raw[0]))
        {
            value = ChineseDigit(raw[0]);
            return true;
        }

        return TryParseChineseInteger(raw, out var whole) && (value = whole) >= 0;
    }

    private static int ReadPercent(Match match)
    {
        if (!match.Success)
            return 0;
        if (double.TryParse(
                match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number))
            return (int)Math.Round(number, MidpointRounding.AwayFromZero);
        return TryParseChineseInteger(match.Groups["chinese"].Value, out var chinese)
            ? chinese
            : 0;
    }

    private static bool TryParseChineseInteger(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text == "十")
        {
            value = 10;
            return true;
        }

        var hundred = text.IndexOf('百');
        if (hundred >= 0)
        {
            var hundreds = hundred == 0 ? 1 : ChineseDigit(text[hundred - 1]);
            if (hundreds < 0)
                return false;
            value += hundreds * 100;
            text = text[(hundred + 1)..];
        }

        var ten = text.IndexOf('十');
        if (ten >= 0)
        {
            var tens = ten == 0 ? 1 : ChineseDigit(text[ten - 1]);
            if (tens < 0)
                return false;
            value += tens * 10;
            text = text[(ten + 1)..];
        }

        if (text.Length > 0)
        {
            var units = ChineseDigit(text[^1]);
            if (units < 0)
                return false;
            value += units;
        }

        return true;
    }

    private static bool TryParseChineseDigits(string text, out int value, out int digits)
    {
        value = 0;
        digits = 0;
        foreach (var character in text)
        {
            var digit = ChineseDigit(character);
            if (digit < 0)
                return false;
            value = value * 10 + digit;
            digits++;
        }
        return digits > 0;
    }

    private static bool IsChineseDigit(char value) => ChineseDigit(value) >= 0;

    private static int ChineseDigit(char value) => value switch
    {
        '零' or '〇' => 0,
        '一' => 1,
        '二' or '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => -1
    };

    private static bool IsCompatible(string kind, string direction) =>
        direction switch
        {
            "player_sells_to_client" => kind is "offer_discount" or "ask_markup",
            "player_buys_from_client" => kind is "offer_buying_markup" or "ask_buying_discount",
            _ => false
        };

    private static string NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "discount" or "offer_discount" => "offer_discount",
        "markup" or "ask_markup" => "ask_markup",
        "buying_markup" or "offer_buying_markup" => "offer_buying_markup",
        "buying_discount" or "ask_buying_discount" => "ask_buying_discount",
        _ => "none"
    };

    private static bool ContainsAnyPriceNumber(string text) =>
        FoldRegex.IsMatch(text) || PercentRegex.IsMatch(text) || AmountRegex.IsMatch(text) ||
        text.Contains("半价", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNumber(string text, long number)
    {
        var normalized = text.Replace(",", string.Empty);
        return Regex.IsMatch(
            normalized,
            $@"(?<!\d){Regex.Escape(number.ToString(CultureInfo.InvariantCulture))}(?!\d)");
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static TradeProposal WithScope(
        TradeProposal proposal,
        string playerText,
        int currentItemCount)
    {
        var decision = BargainScopeResolver.Resolve(playerText, currentItemCount);
        proposal.Scope = decision.Scope;
        proposal.ScopeReason = decision.Reason;
        return proposal;
    }

    private static TradeProposal None(string reason) => new()
    {
        Kind = "none",
        Explicit = false,
        Reason = reason,
        Scope = BargainScopes.Unspecified
    };
}
