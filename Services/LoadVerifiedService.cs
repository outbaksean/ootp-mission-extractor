using MissionExtractor.dto;
using mission_extractor.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace mission_extractor.Services;

public record LoadVerifiedResult(List<string> Errors, int LoadedCount, int MarkedVerifiedCount);

public class LoadVerifiedService
{
    private readonly MissionState _missionState;
    private readonly LightweightValidationService _lws;
    private readonly RewardMappingService _rewardMapping;
    private readonly CardMappingService _cardMapping;

    private static readonly HashSet<string> AvailableCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Live Series", "Pack Rewards", "Launch Deck", "Bonus Rewards",
        "Immortal Seasons", "Negro Leagues", "Hall of Fame", "Baseball Reference",
        "Future Legends", "Launch Plus", "PT Elite", "Playoff Moments",
        "World Series Start", "Holiday Times", "Final Mission Set"
    };

    private static readonly JsonSerializerOptions CompareOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LoadVerifiedService(MissionState missionState, LightweightValidationService lws,
        RewardMappingService rewardMapping, CardMappingService cardMapping)
    {
        _missionState = missionState;
        _lws = lws;
        _rewardMapping = rewardMapping;
        _cardMapping = cardMapping;
    }

    public LoadVerifiedResult Load(List<Mission> incoming, bool clean = false)
    {
        var errors = new List<string>();
        int markedVerifiedCount = 0;

        // Phase 1: Deduplicate by name within the incoming file
        var candidates = DeduplicateIncoming(incoming, errors);

        // Phase 2: Per-mission validation
        var incomingIds = incoming.Select(m => m.Id).ToHashSet();
        var validCandidates = new List<Mission>();
        foreach (var mission in candidates)
        {
            var (missionErrors, missionWarnings) = ValidateMission(mission, incomingIds, clean, _rewardMapping);
            foreach (var w in missionWarnings)
                errors.Add($"'{mission.Name}': {w}");
            if (missionErrors.Count > 0)
                foreach (var e in missionErrors)
                    errors.Add($"'{mission.Name}': {e}");
            else
                validCandidates.Add(mission);
        }

        // Phase 2.5: Propagate failures — Missions-type missions referencing a failed candidate
        // are themselves invalid. Loop until stable to handle cascading dependencies.
        var candidateById = candidates.ToDictionary(m => m.Id);
        var failedIds = candidates.Where(m => !validCandidates.Contains(m)).Select(m => m.Id).ToHashSet();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = validCandidates.Count - 1; i >= 0; i--)
            {
                var mission = validCandidates[i];
                if (mission.Type != MissionType.Missions || mission.MissionIds == null)
                    continue;

                var badIds = mission.MissionIds.Where(id => failedIds.Contains(id)).ToList();
                if (badIds.Count == 0)
                    continue;

                var badNames = badIds
                    .Where(id => candidateById.TryGetValue(id, out _))
                    .Select(id => $"'{candidateById[id].Name}'");
                errors.Add($"'{mission.Name}': references mission(s) with errors: {string.Join(", ", badNames)}");
                validCandidates.RemoveAt(i);
                failedIds.Add(mission.Id);
                changed = true;
            }
        }

        // Phase 3: Deduplicate against existing state
        var toLoad = new List<Mission>();
        foreach (var mission in validCandidates)
        {
            var existing = _missionState.Missions.FirstOrDefault(m =>
                string.Equals(m.Name.Trim(), mission.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                mission.Verified = true;
                mission.ReadOnly = true;
                toLoad.Add(mission);
            }
            else if (FinalFieldsEqual(existing, mission))
            {
                if (!existing.Verified)
                {
                    existing.Verified = true;
                    markedVerifiedCount++;
                }
            }
            else
            {
                errors.Add($"'{mission.Name}': conflicts with existing mission in state (different final fields), not loaded");
            }
        }

        // Phase 4: Prepend to state and regenerate IDs
        if (toLoad.Count > 0)
        {
            // Remap incoming IDs to values above the current max to avoid conflicts
            // with existing state IDs before RegenerateIds renumbers everything
            int nextTempId = (_missionState.Count > 0 ? _missionState.Missions.Max(m => m.Id) : 0) + 1;
            var idRemap = new Dictionary<int, int>();
            foreach (var mission in toLoad)
            {
                idRemap[mission.Id] = nextTempId;
                mission.Id = nextTempId++;
            }
            foreach (var mission in toLoad)
            {
                if (mission.MissionIds != null)
                    mission.MissionIds = mission.MissionIds
                        .Select(id => idRemap.TryGetValue(id, out int newId) ? newId : id)
                        .ToList();
            }

            var allMissions = toLoad.Concat(_missionState.Missions).ToList();
            _lws.RegenerateIds(allMissions);
            _missionState.Replace(allMissions);
        }

        Console.WriteLine($"Loaded {toLoad.Count} verified mission(s). Marked {markedVerifiedCount} existing mission(s) as verified.");
        return new LoadVerifiedResult(errors, toLoad.Count, markedVerifiedCount);
    }

    public LoadVerifiedResult LoadFinalFormat(List<Mission> incoming)
    {
        var errors = new List<string>();
        int markedVerifiedCount = 0;

        // Phase 1: Deduplicate by name within the incoming file
        var candidates = DeduplicateIncoming(incoming, errors);

        // Phase 1.5: Reverse-parse status and missionDetails from structured fields
        SetReverseParsedFields(candidates, _cardMapping, errors);

        // Phase 2: Per-mission validation — all issues are warnings, missions still load
        var incomingIds = incoming.Select(m => m.Id).ToHashSet();
        var missionsWithIssues = new HashSet<int>();
        foreach (var mission in candidates)
        {
            var (missionErrors, missionWarnings) = ValidateMission(mission, incomingIds, false, _rewardMapping);
            var allIssues = missionErrors.Concat(missionWarnings).ToList();
            foreach (var w in allIssues)
                errors.Add($"'{mission.Name}': {w}");
            if (allIssues.Count > 0)
                missionsWithIssues.Add(mission.Id);
        }

        // Phase 3: Deduplicate against existing state
        var existingIdToName = _missionState.Missions.ToDictionary(m => m.Id, m => m.Name.Trim());
        var incomingIdToName = candidates.ToDictionary(m => m.Id, m => m.Name.Trim());
        var toLoad = new List<Mission>();
        foreach (var mission in candidates)
        {
            var existing = _missionState.Missions.FirstOrDefault(m =>
                string.Equals(m.Name.Trim(), mission.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                mission.Verified = !missionsWithIssues.Contains(mission.Id);
                mission.ReadOnly = false;
                toLoad.Add(mission);
            }
            else if (FinalFieldsEqualSemantic(existing, mission, existingIdToName, incomingIdToName))
            {
                // identical — no action
            }
            else
            {
                if (existing.Verified)
                    existing.Verified = false;
                var diffs = GetFinalFieldDiffs(existing, mission, existingIdToName, incomingIdToName);
                var diffStr = diffs.Count > 0 ? $" [{string.Join("; ", diffs)}]" : "";
                errors.Add($"'{mission.Name}': conflicts with existing mission in state (different final fields), not loaded{diffStr}");
            }
        }

        // Phase 4: Prepend to state and regenerate IDs
        if (toLoad.Count > 0)
        {
            int nextTempId = (_missionState.Count > 0 ? _missionState.Missions.Max(m => m.Id) : 0) + 1;
            var idRemap = new Dictionary<int, int>();
            foreach (var mission in toLoad)
            {
                idRemap[mission.Id] = nextTempId;
                mission.Id = nextTempId++;
            }
            foreach (var mission in toLoad)
            {
                if (mission.MissionIds != null)
                    mission.MissionIds = mission.MissionIds
                        .Select(id => idRemap.TryGetValue(id, out int newId) ? newId : id)
                        .ToList();
            }

            var allMissions = toLoad.Concat(_missionState.Missions).ToList();
            _lws.RegenerateIds(allMissions);
            _missionState.Replace(allMissions);
        }

        Console.WriteLine($"Loaded {toLoad.Count} mission(s).");
        return new LoadVerifiedResult(errors, toLoad.Count, markedVerifiedCount);
    }

    public LoadVerifiedResult LoadAndCleanFinalFormat(List<Mission> incoming)
    {
        var errors = new List<string>();
        int markedVerifiedCount = 0;

        // Phase 1: Deduplicate by name within the incoming file
        var candidates = DeduplicateIncoming(incoming, errors);

        // Phase 1.5: Pre-cleanup (silent — no warnings generated)
        CleanupForFinalFormat(candidates);

        // Phase 1.6: Reverse-parse status and missionDetails from structured fields
        SetReverseParsedFields(candidates, _cardMapping, errors);

        // Phase 2: Per-mission validation — all issues are warnings, missions still load
        // Uses clean=true: ParseAndSet for reward, fixes totalPoints mismatches with warnings
        var incomingIds = incoming.Select(m => m.Id).ToHashSet();
        var missionsWithIssues = new HashSet<int>();
        foreach (var mission in candidates)
        {
            var (missionErrors, missionWarnings) = ValidateMission(mission, incomingIds, true, _rewardMapping);
            var allIssues = missionErrors.Concat(missionWarnings).ToList();
            foreach (var w in allIssues)
                errors.Add($"'{mission.Name}': {w}");
            if (allIssues.Count > 0)
                missionsWithIssues.Add(mission.Id);
        }

        // Phase 3: Deduplicate against existing state
        var existingIdToName = _missionState.Missions.ToDictionary(m => m.Id, m => m.Name.Trim());
        var incomingIdToName = candidates.ToDictionary(m => m.Id, m => m.Name.Trim());
        var toLoad = new List<Mission>();
        foreach (var mission in candidates)
        {
            var existing = _missionState.Missions.FirstOrDefault(m =>
                string.Equals(m.Name.Trim(), mission.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                mission.Verified = !missionsWithIssues.Contains(mission.Id);
                mission.ReadOnly = false;
                toLoad.Add(mission);
            }
            else if (FinalFieldsEqualSemantic(existing, mission, existingIdToName, incomingIdToName))
            {
                // identical — no action
            }
            else
            {
                if (existing.Verified)
                    existing.Verified = false;
                var diffs = GetFinalFieldDiffs(existing, mission, existingIdToName, incomingIdToName);
                var diffStr = diffs.Count > 0 ? $" [{string.Join("; ", diffs)}]" : "";
                errors.Add($"'{mission.Name}': conflicts with existing mission in state (different final fields), not loaded{diffStr}");
            }
        }

        // Phase 4: Prepend to state and regenerate IDs
        if (toLoad.Count > 0)
        {
            int nextTempId = (_missionState.Count > 0 ? _missionState.Missions.Max(m => m.Id) : 0) + 1;
            var idRemap = new Dictionary<int, int>();
            foreach (var mission in toLoad)
            {
                idRemap[mission.Id] = nextTempId;
                mission.Id = nextTempId++;
            }
            foreach (var mission in toLoad)
            {
                if (mission.MissionIds != null)
                    mission.MissionIds = mission.MissionIds
                        .Select(id => idRemap.TryGetValue(id, out int newId) ? newId : id)
                        .ToList();
            }

            var allMissions = toLoad.Concat(_missionState.Missions).ToList();
            _lws.RegenerateIds(allMissions);
            _missionState.Replace(allMissions);
        }

        Console.WriteLine($"Loaded {toLoad.Count} mission(s).");
        return new LoadVerifiedResult(errors, toLoad.Count, markedVerifiedCount);
    }

    // Matches "3 ... Pack" or "3x ... Packs" — only applies to pack rewards
    private static readonly Regex RewardCountPrefix = new(@"^(\d+)x?\s+(.+Packs?)$", RegexOptions.Compiled);

    private static void CleanupForFinalFormat(List<Mission> missions)
    {
        foreach (var mission in missions)
        {
            // Normalize "3 Standard Packs" → "3x Standard Pack" silently (add x, drop trailing s)
            if (!string.IsNullOrWhiteSpace(mission.Reward))
            {
                mission.Reward = string.Join(", ", mission.Reward
                    .Split(',')
                    .Select(t =>
                    {
                        var trimmed = t.Trim();
                        var m = RewardCountPrefix.Match(trimmed);
                        if (!m.Success) return trimmed;
                        var packName = m.Groups[2].Value;
                        if (packName.EndsWith('s'))
                            packName = packName[..^1];
                        return $"{m.Groups[1].Value}x {packName}";
                    }));
            }

            // Fix totalPoints for Missions-type silently
            if (mission.Type == MissionType.Missions && mission.MissionIds != null)
                mission.TotalPoints = mission.MissionIds.Count;
        }
    }

    private static void SetReverseParsedFields(
        List<Mission> missions,
        CardMappingService cardMapping,
        List<string> warnings)
    {
        var cardIdToTitle = new Dictionary<int, string>();
        foreach (var (title, entry) in cardMapping.Cards)
            cardIdToTitle.TryAdd(entry.CardId, title);

        var missionIdToName = missions.ToDictionary(m => m.Id, m => m.Name);

        foreach (var mission in missions)
        {
            mission.Status = mission.Type switch
            {
                MissionType.Count    => $"0 / {mission.RequiredCount} out of {mission.TotalPoints}",
                MissionType.Points   => $"0 / {mission.RequiredCount} Points",
                MissionType.Missions => $"0 / {mission.RequiredCount} Missions",
                _                    => mission.Status
            };

            if (mission.Type == MissionType.Count || mission.Type == MissionType.Points)
            {
                var details = new List<string>();
                foreach (var card in mission.Cards)
                {
                    if (cardIdToTitle.TryGetValue(card.CardId, out var title))
                        details.Add(title);
                    else
                        warnings.Add($"'{mission.Name}': card ID {card.CardId} not found in shop_cards.csv, missionDetails entry left blank");
                }
                mission.MissionDetails = details;
            }
            else if (mission.Type == MissionType.Missions)
            {
                var details = new List<string>();
                foreach (var id in mission.MissionIds ?? [])
                {
                    if (missionIdToName.TryGetValue(id, out var name))
                        details.Add(name);
                    else
                        warnings.Add($"'{mission.Name}': mission ID {id} not found in file, missionDetails entry left blank");
                }
                mission.MissionDetails = details;
            }
        }
    }

    private static List<Mission> DeduplicateIncoming(List<Mission> incoming, List<string> errors)
    {
        var result = new List<Mission>();
        var idRemap = new Dictionary<int, int>(); // discarded duplicate ID → kept mission ID
        var groups = incoming.GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var missions = group.ToList();
            if (missions.Count == 1)
            {
                result.Add(missions[0]);
                continue;
            }

            var firstJson = FinalFieldsJson(missions[0]);
            bool allSame = missions.Skip(1).All(m => FinalFieldsJson(m) == firstJson);

            if (allSame)
            {
                errors.Add($"'{group.Key}': {missions.Count} identical copies in file, loaded one");
                result.Add(missions[0]);
                foreach (var discarded in missions.Skip(1))
                    idRemap[discarded.Id] = missions[0].Id;
            }
            else
            {
                errors.Add($"'{group.Key}': duplicate name with different data in file, none loaded");
            }
        }

        // Remap MissionIds that reference a discarded duplicate to the kept mission's ID
        if (idRemap.Count > 0)
        {
            foreach (var mission in result)
            {
                if (mission.MissionIds != null)
                    mission.MissionIds = mission.MissionIds
                        .Select(id => idRemap.TryGetValue(id, out int newId) ? newId : id)
                        .ToList();
            }
        }

        return result;
    }

    private static (List<string> Errors, List<string> Warnings) ValidateMission(
        Mission m, HashSet<int> incomingIds, bool clean, RewardMappingService rewardMapping)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (m.RequiredCount <= 0)
            errors.Add($"requiredCount must be > 0 (got {m.RequiredCount})");

        if (m.Id == 0)
            errors.Add("id is 0");

        if (string.IsNullOrWhiteSpace(m.Reward))
            errors.Add("reward is blank");

        if (m.Type == null)
            errors.Add("type is invalid or missing");

        if (!AvailableCategories.Contains(m.Category?.Trim() ?? ""))
            errors.Add($"category '{m.Category}' is not in available categories");

        if (m.Cards.Any(c => c.CardId == 0))
            errors.Add("one or more cards has cardId 0");

        if (m.MissionIds != null && m.MissionIds.Any(id => id == 0))
            errors.Add("one or more missionIds is 0");

        if (m.Type == MissionType.Count)
        {
            int cardCount = m.Cards.Count;
            if (m.RequiredCount > cardCount)
                errors.Add($"count mission: requiredCount ({m.RequiredCount}) > cards.length ({cardCount})");
            if (cardCount != m.TotalPoints)
            {
                if (clean) { warnings.Add($"count mission: totalPoints fixed from {m.TotalPoints} to {cardCount}"); m.TotalPoints = cardCount; }
                else errors.Add($"count mission: cards.length ({cardCount}) != totalPoints ({m.TotalPoints})");
            }
            var dupCardIds = m.Cards.GroupBy(c => c.CardId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupCardIds.Count > 0)
                errors.Add($"count mission: duplicate cardIds [{string.Join(", ", dupCardIds)}]");
        }
        else if (m.Type == MissionType.Points)
        {
            int sumPoints = m.Cards.Sum(c => c.Points ?? 0);
            if (m.RequiredCount > sumPoints)
                errors.Add($"points mission: requiredCount ({m.RequiredCount}) > sum of card points ({sumPoints})");
            if (sumPoints != m.TotalPoints)
            {
                if (clean) { warnings.Add($"points mission: totalPoints fixed from {m.TotalPoints} to {sumPoints}"); m.TotalPoints = sumPoints; }
                else errors.Add($"points mission: sum of card points ({sumPoints}) != totalPoints ({m.TotalPoints})");
            }
            var dupCardIds = m.Cards.GroupBy(c => c.CardId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupCardIds.Count > 0)
                errors.Add($"points mission: duplicate cardIds [{string.Join(", ", dupCardIds)}]");
        }
        else if (m.Type == MissionType.Missions)
        {
            int missionIdsCount = m.MissionIds?.Count ?? 0;
            if (m.RequiredCount > missionIdsCount)
                errors.Add($"mission type: requiredCount ({m.RequiredCount}) > missionIds.length ({missionIdsCount})");
            if (missionIdsCount != m.TotalPoints)
            {
                if (clean) { warnings.Add($"mission type: totalPoints fixed from {m.TotalPoints} to {missionIdsCount}"); m.TotalPoints = missionIdsCount; }
                else errors.Add($"mission type: missionIds.length ({missionIdsCount}) != totalPoints ({m.TotalPoints})");
            }

            if (m.MissionIds != null)
            {
                var missing = m.MissionIds.Where(id => id != 0 && !incomingIds.Contains(id)).ToList();
                if (missing.Count > 0)
                    errors.Add($"mission type: missionIds [{string.Join(", ", missing)}] not found in file");

                var dupMissionIds = m.MissionIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (dupMissionIds.Count > 0)
                    errors.Add($"mission type: duplicate missionIds [{string.Join(", ", dupMissionIds)}]");
            }
        }

        // Reward validation
        if (clean)
        {
            var rewardErrors = rewardMapping.ParseAndSet(m);
            foreach (var e in rewardErrors)
                errors.Add($"reward: {e}");
        }
        else
        {
            var rewardErrors = rewardMapping.ValidateMatchesExisting(m);
            foreach (var e in rewardErrors)
                errors.Add($"reward: {e}");
        }

        return (errors, warnings);
    }

    private static List<string> ResolveMissionIdNames(List<int>? ids, IReadOnlyDictionary<int, string> idToName) =>
        (ids ?? []).Select(id => idToName.TryGetValue(id, out var name) ? name : id.ToString()).ToList();

    private static List<string> GetFinalFieldDiffs(
        Mission existing, Mission incoming,
        IReadOnlyDictionary<int, string> existingIdToName,
        IReadOnlyDictionary<int, string> incomingIdToName)
    {
        var diffs = new List<string>();

        if (existing.Name?.Trim() != incoming.Name?.Trim())
            diffs.Add($"name: '{existing.Name?.Trim()}' → '{incoming.Name?.Trim()}'");
        if (existing.Type != incoming.Type)
            diffs.Add($"type: {existing.Type} → {incoming.Type}");
        if (existing.RequiredCount != incoming.RequiredCount)
            diffs.Add($"requiredCount: {existing.RequiredCount} → {incoming.RequiredCount}");
        if (existing.Reward != incoming.Reward)
            diffs.Add($"reward: '{existing.Reward}' → '{incoming.Reward}'");
        if (existing.Category != incoming.Category)
            diffs.Add($"category: '{existing.Category}' → '{incoming.Category}'");
        if (existing.TotalPoints != incoming.TotalPoints)
            diffs.Add($"totalPoints: {existing.TotalPoints} → {incoming.TotalPoints}");
        if (JsonSerializer.Serialize(existing.Cards, CompareOptions) != JsonSerializer.Serialize(incoming.Cards, CompareOptions))
        {
            var removedCards = existing.Cards.Select(c => c.CardId).Except(incoming.Cards.Select(c => c.CardId)).ToList();
            var addedCards = incoming.Cards.Select(c => c.CardId).Except(existing.Cards.Select(c => c.CardId)).ToList();
            var cardDesc = new List<string>();
            if (removedCards.Count > 0) cardDesc.Add($"removed [{string.Join(", ", removedCards)}]");
            if (addedCards.Count > 0) cardDesc.Add($"added [{string.Join(", ", addedCards)}]");
            if (cardDesc.Count == 0) cardDesc.Add($"{existing.Cards.Count} items → {incoming.Cards.Count} items (order or points changed)");
            diffs.Add($"cards: {string.Join("; ", cardDesc)}");
        }
        var existingNames = ResolveMissionIdNames(existing.MissionIds, existingIdToName);
        var incomingNames = ResolveMissionIdNames(incoming.MissionIds, incomingIdToName);
        if (!existingNames.SequenceEqual(incomingNames, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"missionIds: [{string.Join(", ", existingNames)}] → [{string.Join(", ", incomingNames)}]");
        if (JsonSerializer.Serialize(existing.Rewards, CompareOptions) != JsonSerializer.Serialize(incoming.Rewards, CompareOptions))
            diffs.Add("rewards changed");

        return diffs;
    }

    private static bool FinalFieldsEqual(Mission a, Mission b) =>
        FinalFieldsJson(a) == FinalFieldsJson(b);

    private static bool FinalFieldsEqualSemantic(
        Mission existing, Mission incoming,
        IReadOnlyDictionary<int, string> existingIdToName,
        IReadOnlyDictionary<int, string> incomingIdToName)
    {
        if (existing.Name?.Trim() != incoming.Name?.Trim()) return false;
        if (existing.Type != incoming.Type) return false;
        if (existing.RequiredCount != incoming.RequiredCount) return false;
        if (existing.Reward != incoming.Reward) return false;
        if (existing.Category != incoming.Category) return false;
        if (existing.TotalPoints != incoming.TotalPoints) return false;
        if (JsonSerializer.Serialize(existing.Cards, CompareOptions) != JsonSerializer.Serialize(incoming.Cards, CompareOptions)) return false;
        if (JsonSerializer.Serialize(existing.Rewards, CompareOptions) != JsonSerializer.Serialize(incoming.Rewards, CompareOptions)) return false;

        var existingNames = ResolveMissionIdNames(existing.MissionIds, existingIdToName);
        var incomingNames = ResolveMissionIdNames(incoming.MissionIds, incomingIdToName);
        return existingNames.SequenceEqual(incomingNames, StringComparer.OrdinalIgnoreCase);
    }

    private static string FinalFieldsJson(Mission m)
    {
        var obj = new
        {
            name = m.Name?.Trim(),
            type = m.Type,
            requiredCount = m.RequiredCount,
            reward = m.Reward,
            category = m.Category,
            cards = m.Cards,
            missionIds = m.MissionIds,
            totalPoints = m.TotalPoints,
            rewards = m.Rewards
        };
        return JsonSerializer.Serialize(obj, CompareOptions);
    }
}
