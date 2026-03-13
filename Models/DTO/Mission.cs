using System.Text.Json.Serialization;

namespace MissionExtractor.dto;

/// <summary>
/// Represents a single mission in OOTP
/// </summary>
public class Mission
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public MissionType? Type { get; set; }

    [JsonPropertyName("requiredCount")]
    public int RequiredCount { get; set; }

    [JsonPropertyName("reward")]
    public string Reward { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("cards")]
    public List<MissionCard> Cards { get; set; } = new();

    [JsonPropertyName("missionIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? MissionIds { get; set; }

    [JsonPropertyName("totalPoints")]
    public int TotalPoints { get; set; }

    [JsonPropertyName("rewards")]
    public List<MissionReward> Rewards { get; set; } = new();

    /// <summary>
    /// Debug images from OCR capture, keyed by field name (e.g. "category", "status",
    /// "name", "reward", "missionDetails"). Each entry is a list of image paths — single
    /// for most fields, one per row for missionDetails. Used in validation reports.
    /// Not part of the final missions.json schema.
    /// </summary>
    [JsonPropertyName("debugImages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? DebugImages { get; set; }

    /// <summary>
    /// Raw OCR text from the status column. Used during transformation to derive Type,
    /// RequiredCount, and TotalPoints. Not part of the final missions.json schema.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Raw OCR text from the mission detail card grid. Used during transformation to
    /// map cards or sub-missions. Not part of the final missions.json schema.
    /// </summary>
    [JsonPropertyName("missionDetails")]
    public List<string> MissionDetails { get; set; } = new();

    [JsonPropertyName("verified")]
    public bool Verified { get; set; } = false;

    [JsonPropertyName("readOnly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadOnly { get; set; } = false;
}
