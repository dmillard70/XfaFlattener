namespace XfaFlatten.Rendering.Playwright;

/// <summary>
/// Manages the Chromium browser lifecycle and path resolution for the Playwright rendering engine.
/// </summary>
public sealed class ChromiumManager
{
    /// <summary>
    /// Resolves the Chromium executable path based on custom path, environment variable, or Playwright defaults.
    /// </summary>
    /// <param name="customPath">An optional user-specified path to a Chromium executable.</param>
    /// <returns>
    /// The resolved Chromium executable path, or <c>null</c> to let Playwright use its default browser.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when a custom path is specified but does not exist.</exception>
    public Task<string?> GetChromiumPathAsync(string? customPath)
    {
        // If user specified a custom path, validate and return it.
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            if (!File.Exists(customPath))
            {
                throw new FileNotFoundException(
                    $"The specified Chromium executable was not found: {customPath}",
                    customPath);
            }

            return Task.FromResult<string?>(customPath);
        }

        // Check PLAYWRIGHT_BROWSERS_PATH environment variable for a custom browsers directory.
        var browsersPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrWhiteSpace(browsersPath))
        {
            // Playwright stores Chromium under <browsersPath>/chromium-<revision>/chrome-win/chrome.exe on Windows.
            // Search for the first matching chrome.exe in the expected structure.
            var chromiumExe = FindChromiumInDirectory(browsersPath);
            if (chromiumExe != null)
            {
                return Task.FromResult<string?>(chromiumExe);
            }
        }

        // Let Playwright handle it with its default installation.
        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Ensures that a Chromium browser is installed via Playwright.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when Chromium installation fails.</exception>
    public async Task EnsureInstalledAsync()
    {
        // Playwright.Program.Main runs the Playwright CLI to install browsers.
        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Playwright Chromium installation failed with exit code {exitCode}. " +
                "Try running 'playwright install chromium' manually.");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Searches a directory for a Chromium executable following the Playwright browser structure.
    /// </summary>
    private static string? FindChromiumInDirectory(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
            return null;

        // Playwright stores browsers in subdirectories like chromium-<revision>/chrome-win/chrome.exe
        try
        {
            var chromiumDirs = Directory.GetDirectories(baseDirectory, "chromium-*");
            foreach (var dir in chromiumDirs.OrderByDescending(d => d))
            {
                var chromeExe = Path.Combine(dir, "chrome-win", "chrome.exe");
                if (File.Exists(chromeExe))
                    return chromeExe;
            }
        }
        catch (IOException)
        {
            // Directory access error; fall through to return null.
        }

        return null;
    }
}
