using MissionExtractor.dto;
using mission_extractor.Models;
using System.Text.RegularExpressions;

namespace mission_extractor.Services;

public class FullTransformationService
{
    private readonly MissionState _missionState;
    private readonly LightweightValidationService _lws;
    private readonly CardMappingService _cardMapping;
    private readonly RewardMappingService _rewardMapping;

    public FullTransformationService(
        MissionState missionState,
        LightweightValidationService lws,
        CardMappingService cardMapping,
        RewardMappingService rewardMapping)
    {
        _missionState = missionState;
        _lws = lws;
        _cardMapping = cardMapping;
        _rewardMapping = rewardMapping;
    }

    private static readonly HashSet<string> AvailableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Live Series",
        "Pack Rewards",
        "Launch Deck",
        "Bonus Rewards",
        "Immortal Seasons",
        "Negro Leagues",
        "Hall of Fame",
        "Baseball Reference",
        "Future Legends",
        "Launch Plus",
        "PT Elite",
        "Playoff Moments",
        "World Series Start",
        "Holiday Times",
        "Final Mission Set"
    };

    public List<ValidationError> Run()
    {
        var (afterEmpty, emptyRemoved) = _lws.RemoveEmptyMissions(_missionState.Missions);
        if (emptyRemoved > 0)
            Console.WriteLine($"Removed {emptyRemoved} empty mission(s).");

        var (afterDedup, dupRemoved) = _lws.Deduplicate(afterEmpty);
        if (dupRemoved > 0)
            Console.WriteLine($"Removed {dupRemoved} duplicate mission(s).");

        var result = _lws.RegenerateIds(afterDedup);

        _lws.CleanFields(result);
        _lws.ParseAllStatuses(result);
        _lws.StripDetailTrailingCommas(result);
        DeduplicateMissionDetails(result);

        BackfillFields(result);

        var errors = ValidateFields(result);
        var errorMissionIds = new HashSet<int>(errors.Select(e => e.Mission.Id));

        TransformPass1(result, errors, errorMissionIds);
        TransformPass2(result, errors, errorMissionIds);
        SetTotals(result);
        CheckRequiredCountVsTotalPoints(result, errors, errorMissionIds);

        foreach (var mission in result)
        {
            if (errorMissionIds.Contains(mission.Id)) continue;
            var rewardErrors = _rewardMapping.ParseAndSet(mission);
            foreach (var e in rewardErrors)
                errors.Add(new ValidationError(mission, e,
                    mission.DebugImages?.GetValueOrDefault("reward")));
        }

        result = ReorderMissions(result, errors);

        _lws.RegenerateIds(result);

        var finalErrorIds = new HashSet<int>(errors.Select(e => e.Mission.Id));
        foreach (var mission in result.Where(m => finalErrorIds.Contains(m.Id)))
            mission.Verified = false;

        if (errors.Count == 0)
            Console.WriteLine("Validation passed.");

        _missionState.Replace(result);
        return errors;
    }

    private void BackfillFields(List<Mission> missions)
    {
        foreach (var m in missions)
        {
            if (string.IsNullOrWhiteSpace(m.Reward) && m.Rewards?.Count > 0)
                m.Reward = _rewardMapping.GenerateRewardString(m.Rewards);

            if (string.IsNullOrWhiteSpace(m.Status) && m.Type.HasValue)
                m.Status = m.Type.Value switch
                {
                    MissionType.Count    => $"0 / {m.RequiredCount} out of {m.RequiredCount}",
                    MissionType.Points   => $"0 / {m.RequiredCount} Points",
                    MissionType.Missions => $"0 / {m.RequiredCount} Missions",
                    _                    => m.Status
                };

            if ((m.MissionDetails == null || m.MissionDetails.Count == 0) && m.Cards?.Count > 0)
                m.MissionDetails = m.Cards
                    .Select(c => _cardMapping.TryLookupById(c.CardId, out var title) ? title : c.CardId.ToString())
                    .ToList();
        }
    }

    private static void DeduplicateMissionDetails(List<Mission> missions)
    {
        foreach (var mission in missions)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keptIndices = new List<int>();
            var deduped = new List<string>();

            for (int i = 0; i < mission.MissionDetails.Count; i++)
            {
                if (seen.Add(mission.MissionDetails[i]))
                {
                    keptIndices.Add(i);
                    deduped.Add(mission.MissionDetails[i]);
                }
            }

            mission.MissionDetails = deduped;

            if (mission.DebugImages?.TryGetValue("missionDetails", out var images) == true && images != null)
            {
                mission.DebugImages["missionDetails"] = keptIndices
                    .Where(i => i < images.Count)
                    .Select(i => images[i])
                    .ToList();
            }
        }
    }

    private static List<ValidationError> ValidateFields(IReadOnlyList<Mission> missions)
    {
        var errors = new List<ValidationError>();

        foreach (var mission in missions)
        {
            if (string.IsNullOrWhiteSpace(mission.Name))
                errors.Add(new ValidationError(mission, "Name Blank",
                    mission.DebugImages?.GetValueOrDefault("name")));

            if (string.IsNullOrWhiteSpace(mission.Category))
                errors.Add(new ValidationError(mission, "Category Blank",
                    mission.DebugImages?.GetValueOrDefault("category")));
            else if (!AvailableCategories.Contains(mission.Category.Trim()))
                errors.Add(new ValidationError(mission, "Category Invalid",
                    mission.DebugImages?.GetValueOrDefault("category")));

            if (string.IsNullOrWhiteSpace(mission.Reward))
                errors.Add(new ValidationError(mission, "Reward Blank",
                    mission.DebugImages?.GetValueOrDefault("reward")));

            if (string.IsNullOrWhiteSpace(mission.Status))
                errors.Add(new ValidationError(mission, "Status Blank",
                    mission.DebugImages?.GetValueOrDefault("status")));
            else if (mission.Type == null)
                errors.Add(new ValidationError(mission, "Type Unparsed",
                    mission.DebugImages?.GetValueOrDefault("status")));

            if (mission.MissionDetails.Count == 0)
                errors.Add(new ValidationError(mission, "MissionDetails Empty",
                    mission.DebugImages?.GetValueOrDefault("missionDetails")));
        }

        return errors;
    }

    private void TransformPass1(
        List<Mission> missions,
        List<ValidationError> errors,
        HashSet<int> errorMissionIds)
    {
        foreach (var mission in missions)
        {
            if (errorMissionIds.Contains(mission.Id))
                continue;

            var detailImages = mission.DebugImages?.GetValueOrDefault("missionDetails");

            if (mission.Type == MissionType.Count)
            {
                var cards = new List<MissionCard>();
                for (int i = 0; i < mission.MissionDetails.Count; i++)
                {
                    var detail = mission.MissionDetails[i];
                    if (_cardMapping.TryLookup(detail, out var entry))
                    {
                        cards.Add(new MissionCard { CardId = entry.CardId });
                    }
                    else
                    {
                        cards.Add(new MissionCard { CardId = 0 });
                        errors.Add(new ValidationError(mission, $"Card Not Found: {detail}",
                            DetailImage(detailImages, i)));
                    }
                }
                mission.Cards = cards;
            }
            else if (mission.Type == MissionType.Points)
            {
                var cards = new List<MissionCard>();
                for (int i = 0; i < mission.MissionDetails.Count; i++)
                {
                    var detail = mission.MissionDetails[i];
                    var extractedPoints = TryExtractTrailingParenInt(detail);
                    var cleanTitle = detail;
                    int parenIndex = detail.LastIndexOf('(');
                    if (parenIndex >= 0)
                        cleanTitle = detail[..parenIndex].TrimEnd();

                    int cardId;
                    int mappedPoints;
                    if (_cardMapping.TryLookup(cleanTitle, out var entry))
                    {
                        cardId = entry.CardId;
                        mappedPoints = CardValueToPoints(entry.CardValue);
                    }
                    else
                    {
                        cardId = 0;
                        mappedPoints = 0;
                        errors.Add(new ValidationError(mission, $"Card Not Found: {cleanTitle}",
                            DetailImage(detailImages, i)));
                    }

                    if (extractedPoints != null && extractedPoints != mappedPoints)
                        errors.Add(new ValidationError(mission,
                            $"Points Mismatch: {cleanTitle} (expected {mappedPoints}, got {extractedPoints})",
                            DetailImage(detailImages, i)));

                    cards.Add(new MissionCard { CardId = cardId, Points = mappedPoints });
                }
                mission.Cards = cards;
            }
        }
    }

    private static void TransformPass2(
        List<Mission> missions,
        List<ValidationError> errors,
        HashSet<int> errorMissionIds)
    {
        var nameLookup = missions.ToDictionary(
            m => m.Name.Trim(),
            m => m.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var mission in missions)
        {
            if (errorMissionIds.Contains(mission.Id))
                continue;

            if (mission.Type != MissionType.Missions)
                continue;

            var detailImages = mission.DebugImages?.GetValueOrDefault("missionDetails");
            mission.MissionIds = new List<int>();
            for (int i = 0; i < mission.MissionDetails.Count; i++)
            {
                var detail = mission.MissionDetails[i];
                if (nameLookup.TryGetValue(detail.Trim(), out int refId))
                {
                    mission.MissionIds.Add(refId);
                }
                else
                {
                    mission.MissionIds.Add(0);
                    errors.Add(new ValidationError(mission, $"Mission Not Found: {detail.Trim()}",
                        DetailImage(detailImages, i)));
                }
            }
        }
    }

    private static List<Mission> ReorderMissions(List<Mission> missions, List<ValidationError> errors)
    {
        var inDegree = new Dictionary<int, int>(missions.Count);
        foreach (var m in missions)
            inDegree[m.Id] = 0;

        var dependents = new Dictionary<int, List<Mission>>();

        foreach (var m in missions.Where(m => m.Type == MissionType.Missions && m.MissionIds != null))
        {
            foreach (var refId in m.MissionIds!)
            {
                if (refId == 0) continue;
                if (!inDegree.ContainsKey(refId)) continue;

                if (!dependents.TryGetValue(refId, out var list))
                {
                    list = new List<Mission>();
                    dependents[refId] = list;
                }
                list.Add(m);
                inDegree[m.Id]++;
            }
        }

        var queue = new Queue<Mission>(missions.Where(m => inDegree[m.Id] == 0));
        var sorted = new List<Mission>(missions.Count);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            if (dependents.TryGetValue(current.Id, out var deps))
            {
                foreach (var dep in deps)
                {
                    inDegree[dep.Id]--;
                    if (inDegree[dep.Id] == 0)
                        queue.Enqueue(dep);
                }
            }
        }

        if (sorted.Count < missions.Count)
        {
            Console.WriteLine("Warning: dependency cycle detected in Mission-type missions. Order not changed.");
            var sortedIds = new HashSet<int>(sorted.Select(m => m.Id));
            foreach (var m in missions.Where(m => !sortedIds.Contains(m.Id)))
                errors.Add(new ValidationError(m, "Dependency Cycle", null));
            return missions;
        }

        return sorted;
    }

    private static void CheckRequiredCountVsTotalPoints(
        List<Mission> missions,
        List<ValidationError> errors,
        HashSet<int> errorMissionIds)
    {
        foreach (var mission in missions)
        {
            if (errorMissionIds.Contains(mission.Id)) continue;

            string? errorMsg = mission.Type switch
            {
                MissionType.Count when mission.RequiredCount > mission.TotalPoints =>
                    $"count mission: requiredCount ({mission.RequiredCount}) > cards.length ({mission.TotalPoints})",
                MissionType.Points when mission.RequiredCount > mission.TotalPoints =>
                    $"points mission: requiredCount ({mission.RequiredCount}) > sum of card points ({mission.TotalPoints})",
                MissionType.Missions when mission.RequiredCount > mission.TotalPoints =>
                    $"mission type: requiredCount ({mission.RequiredCount}) > missionIds.length ({mission.TotalPoints})",
                _ => null
            };

            if (errorMsg != null)
            {
                errors.Add(new ValidationError(mission, errorMsg,
                    mission.DebugImages?.GetValueOrDefault("status")));
                errorMissionIds.Add(mission.Id);
            }
        }
    }

    private static void SetTotals(List<Mission> missions)
    {
        foreach (var mission in missions)
        {
            mission.TotalPoints = mission.Type switch
            {
                MissionType.Count   => mission.Cards.Count,
                MissionType.Points  => mission.Cards.Sum(c => c.Points ?? 0),
                MissionType.Missions => mission.MissionIds?.Count ?? 0,
                _                   => 0
            };
        }
    }

    private static int CardValueToPoints(int cardValue) => cardValue switch
    {
        < 60  => 1,
        < 70  => 2,
        < 80  => 5,
        < 85  => 10,
        < 90  => 15,
        < 95  => 50,
        < 100 => 75,
        _     => 200
    };

    private static IReadOnlyList<string>? DetailImage(List<string>? images, int index) =>
        images != null && index < images.Count ? new[] { images[index] } : null;

    private static int? TryExtractTrailingParenInt(string text)
    {
        var match = Regex.Match(text, @"\((\d+)\)\s*$");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

}
