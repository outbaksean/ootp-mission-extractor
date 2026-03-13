namespace mission_extractor.Models;

/// <summary>
/// Represents the result of a screen capture and OCR operation
/// </summary>
public class CaptureResult
{
    public string CaptureType { get; set; } = string.Empty;
    public DateTime CaptureTime { get; set; }
    public List<string> ExtractedText { get; set; } = new();
    public Dictionary<string, object> MetaData { get; set; } = new();

    /// <summary>
    /// Full path to the saved raw debug image, or null if debug images are disabled.
    /// </summary>
    public string? DebugImagePath { get; set; }
}
