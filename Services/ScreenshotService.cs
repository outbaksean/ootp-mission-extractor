using mission_extractor.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;

namespace mission_extractor.Services
{
    public class ScreenshotService
    {
        private readonly bool _debugImagesEnabled;
        private readonly string _debugImagesPath;

        public ScreenshotService(bool debugImagesEnabled = false)
        {
            _debugImagesEnabled = debugImagesEnabled;
            _debugImagesPath = Path.Combine(AppContext.BaseDirectory, "debugImages");
        }

        public async Task<SoftwareBitmap> GetPreprocessedImage(CaptureRegionConfig region, string debugImageOverrideName = "")
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                throw new ArgumentException("Capture region width and height must be greater than zero.");
            }

            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(region.Left, region.Top, 0, 0, new Size(region.Width, region.Height));
            }

            const int ocrScale = 4;
            using var scaledBitmap = new Bitmap(region.Width * ocrScale, region.Height * ocrScale, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(scaledBitmap))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; // Could be set to HighQuality for better quality, but may cause blurring
                // Could be converted to grayscale, not recommended due to light theme

                graphics.DrawImage(
                    bitmap,
                    new Rectangle(0, 0, scaledBitmap.Width, scaledBitmap.Height),
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel);
            }

            if (_debugImagesEnabled)
            {
                Directory.CreateDirectory(_debugImagesPath);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var rawFileName = $"capture_{stamp}_{region.Left}_{region.Top}_{region.Width}x{region.Height}_raw.png";
                var scaledFileName = $"capture_{stamp}_{region.Left}_{region.Top}_{scaledBitmap.Width}x{scaledBitmap.Height}_scaled.png";
                if (!string.IsNullOrWhiteSpace(debugImageOverrideName))
                {
                    rawFileName = $"{debugImageOverrideName}.png";
                    scaledFileName = $"{debugImageOverrideName}_scaled.png";
                }
                bitmap.Save(Path.Combine(_debugImagesPath, rawFileName), ImageFormat.Png);
                scaledBitmap.Save(Path.Combine(_debugImagesPath, scaledFileName), ImageFormat.Png);
            }

            
            // Could try to add more left padding to center horizontally
            int paddingPixels = 100;
            int finalWidth = scaledBitmap.Width + paddingPixels;
            int finalHeight = scaledBitmap.Height + paddingPixels;

            using var paddedBitmap = new Bitmap(finalWidth, finalHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(paddedBitmap))
            {
                g.Clear(Color.White); // Use white (or your background color)
                                      // Center the scaled text in the padded area
                g.DrawImage(scaledBitmap, (finalWidth - scaledBitmap.Width) / 2, (finalHeight - scaledBitmap.Height) / 2);
            }

            using var memoryStream = new MemoryStream();
            paddedBitmap.Save(memoryStream, ImageFormat.Bmp);
            memoryStream.Position = 0;

            using var randomAccessStream = memoryStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            return softwareBitmap;
        }
    }
}
