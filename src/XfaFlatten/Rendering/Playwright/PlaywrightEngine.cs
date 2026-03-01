using Microsoft.Playwright;

namespace XfaFlatten.Rendering.Playwright;

/// <summary>
/// Rendering engine that uses Playwright/Chromium to print XFA PDFs via <c>page.PdfAsync()</c>.
/// Produces vector PDF output with selectable text when the browser can render the XFA content.
/// </summary>
public sealed class PlaywrightEngine : IRenderEngine
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ChromiumManager _chromiumManager;
    private readonly string? _chromiumPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaywrightEngine"/> class.
    /// </summary>
    /// <param name="chromiumPath">Optional path to a custom Chromium executable.</param>
    public PlaywrightEngine(string? chromiumPath = null)
    {
        _chromiumManager = new ChromiumManager();
        _chromiumPath = chromiumPath;
    }

    /// <inheritdoc />
    public string Name => "Playwright/Chromium";

    /// <inheritdoc />
    public async Task<RenderResult> RenderAsync(string inputPath, int dpi, bool verbose)
    {
        if (!File.Exists(inputPath))
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = $"Input file not found: {inputPath}"
            };
        }

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        IPage? page = null;

        try
        {
            // Resolve the Chromium executable path.
            var executablePath = await _chromiumManager.GetChromiumPathAsync(_chromiumPath);

            if (verbose)
                Console.WriteLine($"[Playwright] Creating Playwright instance...");

            playwright = await Microsoft.Playwright.Playwright.CreateAsync();

            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
            };

            if (executablePath != null)
            {
                launchOptions.ExecutablePath = executablePath;
                if (verbose)
                    Console.WriteLine($"[Playwright] Using Chromium at: {executablePath}");
            }

            if (verbose)
                Console.WriteLine("[Playwright] Launching Chromium...");

            browser = await playwright.Chromium.LaunchAsync(launchOptions);
            page = await browser.NewPageAsync();

            // Build file URI from the absolute path, converting backslashes for URI format.
            var absolutePath = Path.GetFullPath(inputPath);
            var fileUri = "file:///" + absolutePath.Replace('\\', '/');

            if (verbose)
                Console.WriteLine($"[Playwright] Navigating to: {fileUri}");

            // Navigate to the PDF file and wait for the page to load.
            // Chromium's built-in PDF viewer will attempt to render the PDF including XFA content.
            var response = await page.GotoAsync(fileUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = (float)DefaultTimeout.TotalMilliseconds,
            });

            if (response == null || !response.Ok)
            {
                return new RenderResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to load PDF in Chromium. " +
                        $"Status: {response?.Status.ToString() ?? "no response"}"
                };
            }

            // Give Chromium extra time to fully render any XFA content / execute JavaScript.
            await page.WaitForTimeoutAsync(2000);

            if (verbose)
                Console.WriteLine("[Playwright] Generating PDF output...");

            // Use page.PdfAsync() to get vector PDF output.
            // This captures the rendered page content as a new PDF.
            var pdfBytes = await page.PdfAsync(new PagePdfOptions
            {
                Format = "A4",
                PrintBackground = true,
            });

            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                return new RenderResult
                {
                    Success = false,
                    ErrorMessage = "Chromium produced empty PDF output."
                };
            }

            if (verbose)
                Console.WriteLine($"[Playwright] PDF generated: {pdfBytes.Length} bytes");

            return new RenderResult
            {
                Success = true,
                PdfBytes = pdfBytes,
            };
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = "Chromium browser is not installed. " +
                    "Run 'playwright install chromium' or provide --chromium-path."
            };
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Timeout"))
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = $"Chromium timed out while rendering the PDF ({DefaultTimeout.TotalSeconds}s). " +
                    "The PDF may be too complex for browser-based rendering."
            };
        }
        catch (PlaywrightException ex)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = $"Playwright error: {ex.Message}"
            };
        }
        catch (FileNotFoundException ex)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        catch (InvalidOperationException ex)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            // Ensure proper cleanup in reverse order of creation.
            if (page != null)
            {
                try { await page.CloseAsync(); }
                catch { /* Ignore cleanup errors */ }
            }

            if (browser != null)
            {
                try { await browser.CloseAsync(); }
                catch { /* Ignore cleanup errors */ }
            }

            playwright?.Dispose();
        }
    }
}
