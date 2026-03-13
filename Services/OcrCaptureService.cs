namespace mission_extractor.Services;

using mission_extractor.Models;
using System.Drawing;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

/// <summary>
/// Service for capturing screen regions and performing OCR
/// </summary>
public class OcrCaptureService
{
    private readonly bool _debugImagesEnabled;

    public OcrCaptureService(bool debugImagesEnabled = false)
    {
        _debugImagesEnabled = debugImagesEnabled;
    }

    /// <summary>
    /// Capture a screen region and extract text using OCR
    /// </summary>
    public async Task<CaptureResult> CaptureScreenRegion(CaptureRegionConfig region, string debugImageOverrideName = "")
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException("Capture region width and height must be greater than zero.");
        }

        var uniqueOverrideName = string.IsNullOrWhiteSpace(debugImageOverrideName)
            ? debugImageOverrideName
            : $"{debugImageOverrideName}-{Guid.NewGuid():N}";

        var screenShotService = new ScreenshotService(_debugImagesEnabled);
        SoftwareBitmap softwareBitmap = await screenShotService.GetPreprocessedImage(region, uniqueOverrideName);

        string? debugImagePath = null;
        if (_debugImagesEnabled && !string.IsNullOrWhiteSpace(uniqueOverrideName))
            debugImagePath = Path.Combine(AppContext.BaseDirectory, "debugImages", $"{uniqueOverrideName}.png");

        var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Unable to initialize Windows OCR engine.");

        var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
        var captureResult = new CaptureResult
        {
            CaptureType = "ScreenRegion",
            CaptureTime = DateTime.UtcNow,
            ExtractedText = ocrResult.Lines.Select(line => line.Text).ToList(),
            DebugImagePath = debugImagePath,
            MetaData = new Dictionary<string, object>
            {
                { "RegionLeft", region.Left },
                { "RegionTop", region.Top },
                { "RegionWidth", region.Width },
                { "RegionHeight", region.Height }
            }
        };
        return captureResult;
    }

}
