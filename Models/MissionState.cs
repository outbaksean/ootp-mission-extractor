using MissionExtractor.dto;

namespace mission_extractor.Models;

public class MissionState
{
    private readonly List<Mission> _missions = new();

    public IReadOnlyList<Mission> Missions => _missions.AsReadOnly();

    public int Count => _missions.Count;

    public int NextId() => _missions.Count > 0 ? _missions.Max(m => m.Id) + 1 : 1;

    public void Add(Mission mission) => _missions.Add(mission);

    public void Clear() => _missions.Clear();

    public bool Remove(int id)
    {
        var mission = _missions.FirstOrDefault(m => m.Id == id);
        if (mission is null) return false;
        _missions.Remove(mission);
        return true;
    }

    public void Replace(IEnumerable<Mission> missions)
    {
        _missions.Clear();
        _missions.AddRange(missions);
    }

    public Mission? TryUpdate(int id, string? name, string? category, string? reward,
                               string? status, List<string>? missionDetails, List<MissionReward>? rewards = null)
    {
        var mission = _missions.FirstOrDefault(m => m.Id == id);
        if (mission is null) return null;
        if (name is not null) mission.Name = name;
        if (category is not null) mission.Category = category;
        if (reward is not null) mission.Reward = reward;
        if (status is not null) mission.Status = status;
        if (missionDetails is not null) mission.MissionDetails = missionDetails;
        if (rewards is not null) mission.Rewards = rewards;
        return mission;
    }

    public Mission? SetVerified(int id, bool verified)
    {
        var mission = _missions.FirstOrDefault(m => m.Id == id);
        if (mission is null) return null;
        mission.Verified = verified;
        return mission;
    }
}
