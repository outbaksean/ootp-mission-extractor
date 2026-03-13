using System.Text.Json.Serialization;

namespace MissionExtractor.dto;

/// <summary>
/// Represents a reward for completing a mission
/// </summary>
public class MissionReward
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("packType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackType { get; set; }

    [JsonPropertyName("cardId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CardId { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; set; }

    [JsonPropertyName("park")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Park { get; set; }
}
