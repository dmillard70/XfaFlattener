using PdfSharp.Drawing;
using PdfSharp.Pdf;
using XfaFlatten.Rendering;

namespace XfaFlatten.Assembly;

/// <summary>
/// Assembles a new PDF document from rendered page bitmaps or raw PDF bytes.
/// </summary>
public static class PdfAssembler
{
    /// <summary>
    /// Creates a flattened PDF from a render result.
    /// For bitmap-based results, each bitmap becomes a page in the output PDF.
    /// For PDF-based results, the PDF bytes are written directly.
    /// </summary>
    /// <param name="result">The render result containing page bitmaps or PDF bytes.</param>
    /// <param name="outputPath">The path to write the output PDF to.</param>
    public static void Assemble(RenderResult result, string outputPath)
    {
        if (result.IsPdfOutput)
        {
            // Playwright already produced a PDF — write it directly.
            File.WriteAllBytes(outputPath, result.PdfBytes!);
            return;
        }

        if (result.Pages is null || result.Pages.Count == 0)
            throw new InvalidOperationException("No pages to assemble.");

        using var document = new PdfDocument();

        foreach (var pageBitmap in result.Pages)
        {
            AddBitmapPage(document, pageBitmap);
        }

        document.Save(outputPath);
    }

    /// <summary>
    /// Adds a single BGRA bitmap as a new page in the PDF document.
    /// The page is sized to match the bitmap at 72 DPI (matching PDF points).
    /// </summary>
    private static void AddBitmapPage(PdfDocument document, PageBitmap pageBitmap)
    {
        // Convert BGRA raw bytes to a BMP in memory so XImage can load it.
        byte[] bmpBytes = ConvertBgraToBmp(pageBitmap);

        using var stream = new MemoryStream(bmpBytes);
        var image = XImage.FromStream(stream);

        // Create a page matching the image dimensions (in points).
        var page = document.AddPage();
        page.Width = XUnitPt.FromPoint(image.PointWidth);
        page.Height = XUnitPt.FromPoint(image.PointHeight);

        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
    }

    /// <summary>
    /// Converts raw BGRA pixel data to a BMP file byte array for use with XImage.
    /// </summary>
    private static byte[] ConvertBgraToBmp(PageBitmap pageBitmap)
    {
        int width = pageBitmap.Width;
        int height = pageBitmap.Height;

        // BMP row stride must be aligned to 4 bytes.
        int bmpStride = (width * 3 + 3) & ~3;
        int imageSize = bmpStride * height;
        int fileSize = 54 + imageSize; // 14 (file header) + 40 (info header) + data

        var bmp = new byte[fileSize];

        // BMP File Header (14 bytes)
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt32(bmp, 2, fileSize);
        WriteInt32(bmp, 10, 54); // data offset

        // BMP Info Header (40 bytes)
        WriteInt32(bmp, 14, 40); // header size
        WriteInt32(bmp, 18, width);
        WriteInt32(bmp, 22, height); // positive = bottom-up
        WriteInt16(bmp, 26, 1);  // planes
        WriteInt16(bmp, 28, 24); // bits per pixel (RGB)
        WriteInt32(bmp, 34, imageSize);

        // Pixel data: BMP is bottom-up, 24-bit BGR.
        for (int y = 0; y < height; y++)
        {
            int srcRow = (height - 1 - y) * pageBitmap.Stride; // flip vertically
            int dstRow = 54 + y * bmpStride;

            for (int x = 0; x < width; x++)
            {
                int srcIdx = srcRow + x * 4; // BGRA
                int dstIdx = dstRow + x * 3; // BGR

                if (srcIdx + 2 < pageBitmap.Data.Length && dstIdx + 2 < bmp.Length)
                {
                    bmp[dstIdx] = pageBitmap.Data[srcIdx];       // B
                    bmp[dstIdx + 1] = pageBitmap.Data[srcIdx + 1]; // G
                    bmp[dstIdx + 2] = pageBitmap.Data[srcIdx + 2]; // R
                }
            }
        }

        return bmp;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }
}
