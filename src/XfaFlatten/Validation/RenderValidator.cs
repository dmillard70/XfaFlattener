using XfaFlatten.Rendering;

namespace XfaFlatten.Validation;

/// <summary>
/// Result of rendering validation.
/// </summary>
/// <param name="IsValid">Whether the rendering is considered valid.</param>
/// <param name="BlankPageIndices">Zero-based indices of pages detected as blank.</param>
/// <param name="Message">Descriptive message about the validation result.</param>
public record ValidationResult(bool IsValid, int[] BlankPageIndices, string Message);

/// <summary>
/// Validates the output of a rendering engine, checking for completeness and blank pages.
/// </summary>
public static class RenderValidator
{
    /// <summary>
    /// Validates a <see cref="RenderResult"/> for completeness and quality.
    /// </summary>
    /// <param name="result">The render result to validate.</param>
    /// <param name="expectedPageCount">Expected number of pages (from the source PDF), or 0 to skip the check.</param>
    public static ValidationResult Validate(RenderResult result, int expectedPageCount = 0)
    {
        if (!result.Success)
        {
            return new ValidationResult(false, [], $"Rendering failed: {result.ErrorMessage}");
        }

        // If the result is already a PDF (Playwright output), basic validation only.
        if (result.IsPdfOutput)
        {
            if (result.PdfBytes!.Length < 100)
                return new ValidationResult(false, [], "Output PDF is suspiciously small.");

            return new ValidationResult(true, [], "PDF output accepted.");
        }

        // Bitmap-based validation (PDFium output).
        if (result.Pages is null || result.Pages.Count == 0)
        {
            return new ValidationResult(false, [], "No pages were rendered.");
        }

        // Check page count plausibility.
        // XFA documents may produce more pages than the AcroForm replacement page count.
        // For example, 1 replacement page may expand to 11 XFA pages. This is expected.
        string? pageCountNote = null;
        if (expectedPageCount > 0 && result.Pages.Count != expectedPageCount)
        {
            pageCountNote = $" (XFA expanded from {expectedPageCount} to {result.Pages.Count} pages)";
        }

        // Check for blank pages.
        var blankIndices = new List<int>();
        for (int i = 0; i < result.Pages.Count; i++)
        {
            if (BlankPageDetector.IsBlank(result.Pages[i]))
                blankIndices.Add(i);
        }

        if (blankIndices.Count == result.Pages.Count)
        {
            return new ValidationResult(
                false,
                [.. blankIndices],
                "All pages are blank. The rendering engine likely failed to render XFA content.");
        }

        if (blankIndices.Count > 0)
        {
            var pageNumbers = string.Join(", ", blankIndices.Select(i => i + 1));
            return new ValidationResult(
                true,
                [.. blankIndices],
                $"Warning: {blankIndices.Count} blank page(s) detected (pages {pageNumbers}).");
        }

        return new ValidationResult(true, [], $"All {result.Pages.Count} page(s) rendered successfully.{pageCountNote}");
    }
}
