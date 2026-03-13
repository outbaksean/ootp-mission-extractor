using mission_extractor.Models;
using MissionExtractor.dto;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace mission_extractor.Services;

public class LightweightValidationService
{
    private readonly MissionState _missionState;

    public LightweightValidationService(MissionState missionState)
    {
        _missionState = missionState;
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

    public static readonly IReadOnlyList<string> CategoryOrder = new[]
    {
        "Live Series", "Pack Rewards", "Launch Deck", "Bonus Rewards",
        "Immortal Seasons", "Negro Leagues", "Hall of Fame", "Baseball Reference",
        "Future Legends", "Launch Plus", "PT Elite", "Playoff Moments",
        "World Series Start", "Holiday Times", "Final Mission Set"
    };

    private static readonly IReadOnlyDictionary<string, int> CategoryPriority =
        CategoryOrder.Select((c, i) => (c, i))
                     .ToDictionary(x => x.c, x => x.i, StringComparer.OrdinalIgnoreCase);

    private static int GetCategoryPriority(string? cat) =>
        cat != null && CategoryPriority.TryGetValue(cat, out var i) ? i : int.MaxValue;

    private static int GetTypePriority(MissionType? type) => type switch
    {
        MissionType.Count    => 0,
        MissionType.Points   => 1,
        MissionType.Missions => 2,
        _                    => 3
    };

    /// <summary>
    /// Sorts missions by (category order, Count → Points → Missions, name) using Kahn's
    /// algorithm so that Missions-type children always precede their parent missions.
    /// Missions caught in dependency cycles are appended at the end without error reporting.
    /// </summary>
    public List<Mission> ReorderMissions(List<Mission> missions)
    {
        var missionById = missions.ToDictionary(m => m.Id);

        var inDegree = missions.ToDictionary(m => m.Id, _ => 0);
        var dependents = new Dictionary<int, List<int>>();

        foreach (var m in missions)
        {
            if (m.Type != MissionType.Missions || m.MissionIds == null) continue;
            foreach (var refId in m.MissionIds)
            {
                if (refId == 0 || !missionById.ContainsKey(refId)) continue;
                inDegree[m.Id]++;
                if (!dependents.TryGetValue(refId, out var deps))
                    dependents[refId] = deps = new();
                deps.Add(m.Id);
            }
        }

        // Priority queue ordered by (category, type, name, id as tiebreaker)
        var ready = new SortedSet<Mission>(Comparer<Mission>.Create((a, b) =>
        {
            int c = GetCategoryPriority(a.Category).CompareTo(GetCategoryPriority(b.Category));
            if (c != 0) return c;
            c = GetTypePriority(a.Type).CompareTo(GetTypePriority(b.Type));
            if (c != 0) return c;
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return a.Id.CompareTo(b.Id);
        }));

        foreach (var m in missions.Where(m => inDegree[m.Id] == 0))
            ready.Add(m);

        var result = new List<Mission>(missions.Count);
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            result.Add(current);

            if (!dependents.TryGetValue(current.Id, out var deps)) continue;
            foreach (var depId in deps)
                if (--inDegree[depId] == 0)
                    ready.Add(missionById[depId]);
        }

        // Append any missions in cycles (shouldn't normally happen after transform validation)
        if (result.Count < missions.Count)
        {
            var resultIds = result.Select(m => m.Id).ToHashSet();
            result.AddRange(missions.Where(m => !resultIds.Contains(m.Id)));
        }

        return result;
    }

    // Confirmed OCR status text patterns:
    //   Count:   "X / Y out of Z"  — Y -> RequiredCount
    //   Points:  "X / Y Points"    — Y -> TotalPoints
    //   Mission: "X / Y Missions"  — Y -> RequiredCount
    //   No match -> Type left null (reported as "Type Unparsed" by ValidateFields)
    // Strips a trailing card-value number that OCR appends after the 4-digit year,
    // e.g. "Dan Haren OAK 2005 60" → "Dan Haren OAK 2005"
    private static readonly Regex TrailingCardValueAfterYearPattern =
        new(@"((?:18|19|20)\d{2})\s+\d.*$", RegexOptions.Compiled);

    // Expands "Historical <POS>" → "Historical All-Star <POS>" for any fielding position.
    // Keep the alternation in sync with the positionAbbreviations HashSet in CleanFields.
    private static readonly Regex HistoricalPositionPattern =
        new(@"\bHistorical\s+(SP|RP|CL|1B|2B|3B|SS|LF|CF|RF|DH|C)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CountPattern =
        new(@"\S+\s*/\s*(?:[a-z]+\s+)*(\d+)\s+out\s+[o0]f\s*\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PointsPattern =
        new(@"\S+\s*/\s*(\d+)\s+Points", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MissionPattern =
        new(@"\S+\s*/\s*(\d+)\s+Missions", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Runs all lightweight cleanup and validation steps in order and updates MissionState with the result.
    /// </summary>
    public List<ValidationError> Run()
    {
        var (afterEmpty, emptyRemoved) = RemoveEmptyMissions(_missionState.Missions);
        if (emptyRemoved > 0)
            Console.WriteLine($"Removed {emptyRemoved} empty mission(s).");

        var (afterDedup, dupRemoved) = Deduplicate(afterEmpty);
        if (dupRemoved > 0)
            Console.WriteLine($"Removed {dupRemoved} duplicate mission(s).");

        var result = RegenerateIds(afterDedup);

        CleanFields(result);

        ParseAllStatuses(result);

        StripDetailTrailingCommas(result);

        var errors = ValidateFields(result);

        if (errors.Count == 0)
            Console.WriteLine("Validation passed. No errors found.");

        _missionState.Replace(result);
        return errors;
    }

    /// <summary>
    /// Removes missions where all key fields are blank.
    /// </summary>
    public (List<Mission> result, int removedCount) RemoveEmptyMissions(IReadOnlyList<Mission> missions)
    {
        var result = missions
            .Where(m =>
                !string.IsNullOrWhiteSpace(m.Name) ||
                !string.IsNullOrWhiteSpace(m.Category) ||
                !string.IsNullOrWhiteSpace(m.Reward) ||
                !string.IsNullOrWhiteSpace(m.Status) ||
                m.MissionDetails.Count > 0)
            .ToList();

        return (result, missions.Count - result.Count);
    }

    /// <summary>
    /// Deduplicates by Name + Category, keeping the first occurrence of each.
    /// </summary>
    public (List<Mission> result, int removedCount) Deduplicate(IReadOnlyList<Mission> missions)
    {
        var seen = new HashSet<string>();
        var result = new List<Mission>();

        foreach (var mission in missions)
        {
            var key = $"{mission.Name.Trim()}|{mission.Category.Trim()}";
            if (seen.Add(key))
                result.Add(mission);
        }

        return (result, missions.Count - result.Count);
    }

    /// <summary>
    /// Reassigns sequential IDs starting at 1 and updates any missionIds cross-references.
    /// </summary>
    public List<Mission> RegenerateIds(List<Mission> missions)
    {
        var idMap = new Dictionary<int, int>();

        for (int i = 0; i < missions.Count; i++)
        {
            int oldId = missions[i].Id;
            int newId = i + 1;
            idMap[oldId] = newId;
            missions[i].Id = newId;
        }

        foreach (var mission in missions)
        {
            if (mission.MissionIds != null)
                mission.MissionIds = mission.MissionIds
                    .Select(id => idMap.TryGetValue(id, out int newId) ? newId : id)
                    .ToList();
        }

        return missions;
    }

    /// <summary>
    /// Trims OCR noise from key text fields: strips everything from the last "[" in Name,
    /// and strips everything from the last "(" in each MissionDetails entry.
    /// </summary>
    public void CleanFields(List<Mission> missions)
    {
        var positionAbbreviations = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "SP", "RP", "CL", "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH"
        };
        foreach (var mission in missions)
        {
            int bracketIndex = mission.Name.LastIndexOf('[');
            if (bracketIndex >= 0)
                mission.Name = mission.Name[..bracketIndex].TrimEnd();

            if (mission.Type == MissionType.Missions)
                continue;

            for (int i = 0; i < mission.MissionDetails.Count; i++)
            {
                int parenIndex = mission.MissionDetails[i].IndexOf('(');
                if (parenIndex >= 0)
                    mission.MissionDetails[i] = mission.MissionDetails[i][..parenIndex].TrimEnd();
                int buyIndex = mission.MissionDetails[i].IndexOf("Buy", StringComparison.InvariantCultureIgnoreCase);
                if (buyIndex >= 0)
                    mission.MissionDetails[i] = mission.MissionDetails[i][..buyIndex].TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Sell Orders", "", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Locked", "", StringComparison.InvariantCultureIgnoreCase).TrimStart();
                mission.MissionDetails[i] = mission.MissionDetails[i].TrimStart('-');

                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Historical AS", "Historical All-Star", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = HistoricalPositionPattern.Replace(mission.MissionDetails[i], "Historical All-Star $1");
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("UnH Heroes", "Unsung Heroes", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("RSSensation", "Rookie Sensation", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("RSSensatlon", "Rookie Sensation", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("HaHes", "Hardware Heroes", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Future Leg ", "Future Legend ", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Future Le ", "Future Legend ", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Last Call Legs ", "Last Call Legends ", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("All-Time Leg ", "All-Time Legend ", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("All-Time Le ", "All-Time Legend ", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("NeL Star", "Negro League Star", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace("Atlanta ASS", "Atlanta All-Stars", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(" KCI", " KC1", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(" NYI", " NY1", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(" WSI", " WS1", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(" MSI", " MS1", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(" MLI", " ML1", StringComparison.InvariantCultureIgnoreCase).TrimEnd();
                mission.MissionDetails[i] = TrailingCardValueAfterYearPattern.Replace(mission.MissionDetails[i], "$1");
                mission.MissionDetails[i] = RemoveAccents(mission.MissionDetails[i]);
            }
        }
    }

    private static string RemoveAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Strips trailing commas from MissionDetails entries for Count and Points missions.
    /// Must be called after ParseAllStatuses so Type is known.
    /// </summary>
    public void StripDetailTrailingCommas(List<Mission> missions)
    {
        foreach (var mission in missions)
        {
            if (mission.Type != MissionType.Count && mission.Type != MissionType.Points)
                continue;

            for (int i = 0; i < mission.MissionDetails.Count; i++)
                mission.MissionDetails[i] = mission.MissionDetails[i].Replace(",", "");
        }
    }

    /// <summary>
    /// Infers mission Type and sets RequiredCount or TotalPoints by parsing the Status field.
    /// Mutates each mission in place. Missions with no matching pattern remain Undefined.
    /// </summary>
    public void ParseAllStatuses(List<Mission> missions)
    {
        foreach (var mission in missions)
        {
            if (string.IsNullOrWhiteSpace(mission.Status))
                continue;

            Match m;
            if ((m = CountPattern.Match(mission.Status)).Success)
            {
                mission.Type = MissionType.Count;
                mission.RequiredCount = int.Parse(m.Groups[1].Value);
            }
            else if ((m = PointsPattern.Match(mission.Status)).Success)
            {
                mission.Type = MissionType.Points;
                mission.RequiredCount = int.Parse(m.Groups[1].Value);
            }
            else if ((m = MissionPattern.Match(mission.Status)).Success)
            {
                mission.Type = MissionType.Missions;
                mission.RequiredCount = int.Parse(m.Groups[1].Value);
            }
            // else: leave Type = null
        }
    }

    /// <summary>
    /// Validates all key fields of each mission. Images for each error are pulled from
    /// the mission's DebugImages dict by field name.
    /// </summary>
    public List<ValidationError> ValidateFields(IReadOnlyList<Mission> missions)
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

}
