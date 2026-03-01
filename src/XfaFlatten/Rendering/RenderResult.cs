namespace XfaFlatten.Rendering;

/// <summary>
/// Contains the output of a rendering engine: either a list of page bitmaps or raw PDF bytes.
/// </summary>
public class RenderResult
{
    /// <summary>
    /// Whether the rendering completed successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// A human-readable error message when <see cref="Success"/> is <c>false</c>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// For bitmap-based rendering (PDFium): list of page bitmaps as BGRA byte arrays.
    /// </summary>
    public List<PageBitmap>? Pages { get; init; }

    /// <summary>
    /// For PDF-based rendering (Playwright): the resulting PDF as a byte array.
    /// </summary>
    public byte[]? PdfBytes { get; init; }

    /// <summary>
    /// Whether the result is already a complete PDF (vs. bitmaps that need assembly).
    /// </summary>
    public bool IsPdfOutput => PdfBytes != null;
}

/// <summary>
/// Represents a single rendered page as a BGRA bitmap.
/// </summary>
/// <param name="Data">Raw pixel data in BGRA format.</param>
/// <param name="Width">Bitmap width in pixels.</param>
/// <param name="Height">Bitmap height in pixels.</param>
/// <param name="Stride">Number of bytes per scanline row.</param>
public record PageBitmap(byte[] Data, int Width, int Height, int Stride);
