using System.Text.Json.Serialization;

namespace MissionExtractor.dto;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PackType
{
    Standard,
    Silver,
    Gold,
    Diamond,
    Perfect,
    Rainbow,
    HistSilver,
    HistGold,
    HistDiamond,
    HistPerfect,
    HistRainbow,
    AllPerfect,
    HistAllPerfect,
    AllDiamond,
    HistAllDiamond,
    MoneyballDiamond,
    AnalyticsDiamond,
    DefensiveDiamond,
    PowerDiamond,
    SpotlightImmortals,
}
