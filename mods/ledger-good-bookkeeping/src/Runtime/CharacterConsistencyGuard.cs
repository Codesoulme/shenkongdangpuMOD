using System.Text.RegularExpressions;
using AIWorldPawnshop.Models;

namespace AIWorldPawnshop.Runtime;

internal static class CharacterConsistencyGuard
{
    private static readonly Regex NameClaimPattern = new(
        @"(?:我叫|我的名字是|my name is)\s*[：:]?\s*([^，。,.!?！？\n]{1,24})",
        RegexOptions.IgnoreCase |
        RegexOptions.CultureInvariant |
        RegexOptions.Compiled);

    private static readonly string[] KnownCharacterNames =
    {
        "乔治奥", "乔治欧", "georgio",
        "伦警官", "伦官员", "officer lun",
        "胡安", "juan",
        "埃莉诺", "艾莉诺", "eleanor",
        "杰克逊", "jackson"
    };

    public static bool IsConsistent(
        ConversationRequest request,
        ConversationResponse response,
        out string reason)
    {
        reason = string.Empty;
        var character = request.GameContext.Character;
        if (!character.IsNamedSpecialCharacter || string.IsNullOrWhiteSpace(response.NpcReply))
            return true;

        var aliases = new[]
        {
            character.DisplayName,
            character.OriginalRealName,
            character.OriginalIdentifier
        }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

        foreach (Match match in NameClaimPattern.Matches(response.NpcReply))
        {
            var claimed = match.Groups[1].Value.Trim();
            if (aliases.Any(alias => NamesOverlap(claimed, alias)))
                continue;

            reason = $"回复自称为“{claimed}”，与原版人物 {character.DisplayName} 不一致";
            return false;
        }

        foreach (var knownName in KnownCharacterNames)
        {
            if (!SelfClaimContains(response.NpcReply, knownName))
                continue;
            if (aliases.Any(alias => NamesOverlap(knownName, alias)))
                continue;

            reason = $"回复借用了另一名原版人物“{knownName}”的身份";
            return false;
        }

        return true;
    }

    private static bool SelfClaimContains(string reply, string name)
    {
        return reply.Contains($"我是{name}", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains($"我叫{name}", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains($"我的名字是{name}", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains($"I am {name}", StringComparison.OrdinalIgnoreCase) ||
               reply.Contains($"I'm {name}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesOverlap(string left, string right)
    {
        var a = NormalizeName(left);
        var b = NormalizeName(right);
        return a.Length > 0 && b.Length > 0 &&
               (a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
                b.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeName(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

}
