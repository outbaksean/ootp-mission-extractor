using MissionExtractor.dto;
using System.Text.RegularExpressions;

namespace mission_extractor.Services;

public class RewardMappingService
{
    private readonly CardMappingService _cardMapping;

    private static readonly Dictionary<string, PackType> PackNameToType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard Pack"]          = PackType.Standard,
            ["Silver Pack"]            = PackType.Silver,
            ["Gold Pack"]              = PackType.Gold,
            ["Diamond Pack"]           = PackType.Diamond,
            ["Perfect Pack"]           = PackType.Perfect,
            ["Rainbow Pack"]           = PackType.Rainbow,
            ["Historical Silver Pack"] = PackType.HistSilver,
            ["Historical Gold Pack"]   = PackType.HistGold,
            ["Historical Diamond Pack"]= PackType.HistDiamond,
            ["Historical Perfect Pack"]= PackType.HistPerfect,
            ["Historical Rainbow Pack"]= PackType.HistRainbow,
            ["All-Perfect Pack"]              = PackType.AllPerfect,
            ["Historical All-Perfect Pack"]   = PackType.HistAllPerfect,
            ["All-Diamond Pack"]              = PackType.AllDiamond,
            ["Historical All-Diamond Pack"]   = PackType.HistAllDiamond,
            ["Moneyball Era Diamond Pack"] = PackType.MoneyballDiamond,
            ["Analytics Era Diamond Pack"] = PackType.AnalyticsDiamond,
            ["Power Era Diamond Pack"] = PackType.PowerDiamond,
            ["Defensive Era Diamond Pack"] = PackType.DefensiveDiamond,
            ["Spotlight #Immortals Pack"] = PackType.SpotlightImmortals,
        };

    private static readonly Dictionary<string, string> PackTypeToDisplayName =
        PackNameToType.ToDictionary(kvp => kvp.Value.ToString(), kvp => kvp.Key);

    private static readonly Regex PackCountPrefix =
        new(@"^(\d+)x\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RewardMappingService(CardMappingService cardMapping)
    {
        _cardMapping = cardMapping;
    }

    /// <summary>
    /// Parses mission.Reward into structured MissionReward entries and sets mission.Rewards.
    /// Returns error strings for any unrecognized or unmapped tokens.
    /// </summary>
    public List<string> ParseAndSet(Mission mission)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(mission.Reward))
            return errors;

        var rewards = new List<MissionReward>();

        foreach (var raw in mission.Reward.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            var reward = ParseToken(token, errors);
            if (reward != null)
                rewards.Add(reward);
        }

        mission.Rewards = rewards;
        return errors;
    }

    /// <summary>
    /// Generates a human-readable reward string from structured MissionReward entries.
    /// This is the reverse of ParseAndSet.
    /// </summary>
    public string GenerateRewardString(List<MissionReward> rewards)
    {
        var parts = new List<string>();

        foreach (var r in rewards)
        {
            if (r.Type.Equals("Park", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"Park: {r.Park}");
            }
            else if (r.Type.Equals("Pack", StringComparison.OrdinalIgnoreCase))
            {
                var name = r.PackType != null && PackTypeToDisplayName.TryGetValue(r.PackType, out var display)
                    ? display
                    : r.PackType ?? "Pack";
                parts.Add(r.Count > 1 ? $"{r.Count}x {name}" : name);
            }
            else if (r.Type.Equals("Card", StringComparison.OrdinalIgnoreCase) && r.CardId.HasValue)
            {
                parts.Add(_cardMapping.TryLookupById(r.CardId.Value, out var title)
                    ? title
                    : r.CardId.Value.ToString());
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Parses mission.Reward and compares the result to mission.Rewards already in the mission.
    /// Returns error strings if parsing fails or the result does not match.
    /// Does not mutate the mission.
    /// </summary>
    public List<string> ValidateMatchesExisting(Mission mission)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(mission.Reward))
            return errors;

        var parseErrors = new List<string>();
        var parsed = new List<MissionReward>();

        foreach (var raw in mission.Reward.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;

            var reward = ParseToken(token, parseErrors);
            if (reward != null)
                parsed.Add(reward);
        }

        if (parseErrors.Count > 0)
        {
            foreach (var e in parseErrors)
                errors.Add(e);
            return errors;
        }

        if (!RewardsEqual(parsed, mission.Rewards))
            errors.Add("parsed rewards do not match rewards in file");

        return errors;
    }

    private MissionReward? ParseToken(string token, List<string> errors)
    {
        // Park
        if (token.StartsWith("Park:", StringComparison.OrdinalIgnoreCase))
        {
            var parkName = token["Park:".Length..].Trim();
            return new MissionReward { Type = "Park", Park = parkName };
        }

        // Pack — optional NUMx prefix
        int count = 1;
        var packCandidate = token;
        var countMatch = PackCountPrefix.Match(token);
        if (countMatch.Success)
        {
            count = int.Parse(countMatch.Groups[1].Value);
            packCandidate = countMatch.Groups[2].Value.Trim();
        }

        if (PackNameToType.TryGetValue(packCandidate, out var packType))
        {
            return new MissionReward
            {
                Type = "Pack",
                PackType = packType.ToString(),
                Count = count
            };
        }

        // Card — always use the full original token (not packCandidate)
        if (_cardMapping.TryLookup(token, out var cardEntry))
            return new MissionReward { Type = "Card", CardId = cardEntry.CardId };

        // Fallback: parse "{cardValue} {position} {playerName}" and match by name substring + value
        if (_cardMapping.TryFuzzyLookupByValueAndName(token, out var fuzzyEntry))
            return new MissionReward { Type = "Card", CardId = fuzzyEntry.CardId };

        errors.Add($"Reward Card Not Found: {token}");
        return null;
    }

    private static bool RewardsEqual(List<MissionReward> a, List<MissionReward> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Type != y.Type) return false;
            if (x.PackType != y.PackType) return false;
            if (x.CardId != y.CardId) return false;
            if (x.Count != y.Count) return false;
            if (x.Park != y.Park) return false;
        }

        return true;
    }
}
