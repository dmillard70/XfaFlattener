using XfaFlatten.Rendering;

namespace XfaFlatten.Validation;

/// <summary>
/// Detects blank (all-white or near-white) pages in rendered bitmaps.
/// </summary>
public static class BlankPageDetector
{
    /// <summary>
    /// Default threshold: if more than this fraction of pixels are white, the page is blank.
    /// </summary>
    private const double DefaultWhiteThreshold = 0.995;

    /// <summary>
    /// Pixel luminance above which a pixel is considered "white".
    /// BGRA format: B=0, G=1, R=2, A=3.
    /// </summary>
    private const int WhiteLuminanceThreshold = 250;

    /// <summary>
    /// Returns true if the given page bitmap appears blank (all white or near-white).
    /// </summary>
    public static bool IsBlank(PageBitmap page, double whiteThreshold = DefaultWhiteThreshold)
    {
        if (page.Data.Length == 0 || page.Width == 0 || page.Height == 0)
            return true;

        long whitePixels = 0;
        long totalPixels = (long)page.Width * page.Height;

        // Walk through the BGRA bitmap data row by row.
        for (int y = 0; y < page.Height; y++)
        {
            int rowOffset = y * page.Stride;

            for (int x = 0; x < page.Width; x++)
            {
                int pixelOffset = rowOffset + x * 4; // 4 bytes per pixel (BGRA)

                if (pixelOffset + 2 >= page.Data.Length)
                    break;

                byte b = page.Data[pixelOffset];
                byte g = page.Data[pixelOffset + 1];
                byte r = page.Data[pixelOffset + 2];

                if (r >= WhiteLuminanceThreshold &&
                    g >= WhiteLuminanceThreshold &&
                    b >= WhiteLuminanceThreshold)
                {
                    whitePixels++;
                }
            }
        }

        return (double)whitePixels / totalPixels >= whiteThreshold;
    }
}
