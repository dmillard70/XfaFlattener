using XfaFlatten.Infrastructure;
using XfaFlatten.Rendering.Pdfium;
using XfaFlatten.Rendering.Playwright;
using XfaFlatten.Validation;

namespace XfaFlatten.Rendering;

/// <summary>
/// Selects and executes the appropriate rendering engine based on the --engine flag.
/// In "auto" mode, tries PDFium first and falls back to Playwright on failure.
/// </summary>
public sealed class EngineSelector
{
    private readonly string _engineChoice;
    private readonly string? _chromiumPath;
    private readonly ConsoleLogger _logger;

    public EngineSelector(string engineChoice, string? chromiumPath, ConsoleLogger logger)
    {
        _engineChoice = engineChoice;
        _chromiumPath = chromiumPath;
        _logger = logger;
    }

    /// <summary>
    /// Renders the input PDF using the configured engine strategy.
    /// </summary>
    /// <param name="inputPath">Path to the XFA PDF.</param>
    /// <param name="dpi">Rendering DPI for bitmap engines.</param>
    /// <param name="expectedPageCount">Expected page count for validation.</param>
    /// <returns>The render result from the successful engine, or the last failure.</returns>
    public async Task<RenderResult> RenderAsync(string inputPath, int dpi, int expectedPageCount)
    {
        return _engineChoice switch
        {
            "pdfium" => await RenderWithPdfium(inputPath, dpi),
            "playwright" => await RenderWithPlaywright(inputPath, dpi),
            _ => await RenderAuto(inputPath, dpi, expectedPageCount), // "auto"
        };
    }

    private async Task<RenderResult> RenderAuto(string inputPath, int dpi, int expectedPageCount)
    {
        // Try PDFium first.
        _logger.Info("Trying PDFium engine...");
        var result = await RenderWithPdfium(inputPath, dpi);

        if (result.Success)
        {
            // Validate the result.
            var validation = RenderValidator.Validate(result, expectedPageCount);

            if (validation.IsValid)
            {
                if (validation.BlankPageIndices.Length > 0)
                    _logger.Warning(validation.Message);
                else
                    _logger.VerboseLog($"PDFium validation: {validation.Message}");

                // If all pages are blank, fall back.
                if (validation.BlankPageIndices.Length > 0 &&
                    result.Pages != null &&
                    validation.BlankPageIndices.Length == result.Pages.Count)
                {
                    _logger.Warning("All pages blank. Falling back to Playwright...");
                }
                else
                {
                    return result;
                }
            }
            else
            {
                _logger.Warning($"PDFium validation failed: {validation.Message}");
                _logger.Info("Falling back to Playwright engine...");
            }
        }
        else
        {
            _logger.Warning($"PDFium failed: {result.ErrorMessage}");
            _logger.Info("Falling back to Playwright engine...");
        }

        // Fallback to Playwright.
        return await RenderWithPlaywright(inputPath, dpi);
    }

    private async Task<RenderResult> RenderWithPdfium(string inputPath, int dpi)
    {
        var engine = new PdfiumEngine();
        _logger.VerboseLog($"Using engine: {engine.Name}");
        return await engine.RenderAsync(inputPath, dpi, _logger.Verbose);
    }

    private async Task<RenderResult> RenderWithPlaywright(string inputPath, int dpi)
    {
        var engine = new PlaywrightEngine(_chromiumPath);
        _logger.VerboseLog($"Using engine: {engine.Name}");
        return await engine.RenderAsync(inputPath, dpi, _logger.Verbose);
    }
}
