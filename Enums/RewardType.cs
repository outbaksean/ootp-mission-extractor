using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace mission_extractor.Enums
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RewardType
    {
        Card,
        Pack,
        Park,
        Other
    }
}
