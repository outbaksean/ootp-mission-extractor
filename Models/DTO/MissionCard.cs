using System.Text.Json.Serialization;

namespace MissionExtractor.dto;

/// <summary>
/// Represents a card requirement for a mission
/// </summary>
public class MissionCard
{
    [JsonPropertyName("cardId")]
    public int CardId { get; set; }

    [JsonPropertyName("points")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Points { get; set; }
}
