using XfaFlatten.Infrastructure;

namespace XfaFlatten.Rendering.Pdfium;

/// <summary>
/// Renders XFA-based PDFs using the PDFium native library with V8+XFA support.
/// Each page is rendered to a BGRA bitmap at the requested DPI.
/// </summary>
public sealed class PdfiumEngine : IRenderEngine
{
    private static bool _libraryInitialized;
    private static readonly object _initLock = new();

    /// <inheritdoc />
    public string Name => "PDFium";

    /// <inheritdoc />
    public Task<RenderResult> RenderAsync(string inputPath, int dpi, bool verbose)
    {
        // PDFium is synchronous; wrap in Task.Run to avoid blocking the caller.
        return Task.Run(() => Render(inputPath, dpi, verbose));
    }

    private static RenderResult Render(string inputPath, int dpi, bool verbose)
    {
        var logger = new ConsoleLogger { Verbose = verbose };

        try
        {
            // Step 1: Initialize the PDFium library (once per process).
            EnsureLibraryInitialized(logger);

            // Step 2: Load the PDF document.
            logger.VerboseLog("[PDFium] Loading document...");
            var document = PdfiumNative.FPDF_LoadDocument(inputPath, null);
            if (document == IntPtr.Zero)
            {
                var error = PdfiumNative.FPDF_GetLastError();
                return new RenderResult
                {
                    Success = false,
                    ErrorMessage = $"PDFium failed to load document: {PdfiumNative.GetErrorDescription(error)}"
                };
            }

            try
            {
                return RenderDocument(document, dpi, logger);
            }
            finally
            {
                // Step 9: Close the document.
                PdfiumNative.FPDF_CloseDocument(document);
                logger.VerboseLog("[PDFium] Document closed.");
            }
        }
        catch (DllNotFoundException ex)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = $"PDFium native library not found: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = $"PDFium rendering failed: {ex.Message}"
            };
        }
    }

    private static RenderResult RenderDocument(IntPtr document, int dpi, ConsoleLogger logger)
    {
        // Step 3: Skip FPDF_LoadXFA.
        // The V8 engine in the NuGet PDFium build (4522) has pointer compression
        // cage issues that cause fatal crashes. Without V8 properly initialized,
        // calling FPDF_LoadXFA creates a half-initialized state that crashes during
        // cleanup. For dynamic XFA with scripts, the Playwright fallback is used.
        logger.VerboseLog("[PDFium] Skipping XFA load (V8 not available in this build).");

        // Step 4: Initialize Form Fill Environment (version 1, no XFA).
        logger.VerboseLog("[PDFium] Initializing Form Fill Environment...");
        using var formFill = PdfiumFormFill.Init(document, xfaEnabled: false);

        // Steps 5+6: Skipped (no V8/JS execution).
        logger.VerboseLog("[PDFium] Skipping JS/Open actions.");

        // Step 7: Render each page.
        int pageCount = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Document has {pageCount} page(s).");

        if (pageCount <= 0)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = "PDFium reports zero pages in the document."
            };
        }

        var pages = new List<PageBitmap>(pageCount);
        int renderFlags = PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_PRINTING;

        for (int i = 0; i < pageCount; i++)
        {
            logger.VerboseLog($"[PDFium] Rendering page {i + 1}/{pageCount}...");
            var pageBitmap = RenderPage(document, formFill, i, dpi, renderFlags, logger);
            pages.Add(pageBitmap);
        }

        logger.VerboseLog("[PDFium] All pages rendered.");

        return new RenderResult
        {
            Success = true,
            Pages = pages
        };
    }

    private static PageBitmap RenderPage(
        IntPtr document,
        PdfiumFormFill formFill,
        int pageIndex,
        int dpi,
        int flags,
        ConsoleLogger logger)
    {
        // 7a: Load the page.
        var page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
        if (page == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"PDFium failed to load page {pageIndex + 1}.");
        }

        try
        {
            // 7b: Notify form fill that the page is loaded (triggers page-level scripts).
            formFill.OnAfterLoadPage(page);

            // Calculate pixel dimensions from page size (points) and DPI.
            float widthPt = PdfiumNative.FPDF_GetPageWidthF(page);
            float heightPt = PdfiumNative.FPDF_GetPageHeightF(page);

            int pixelWidth = (int)(widthPt * dpi / 72.0);
            int pixelHeight = (int)(heightPt * dpi / 72.0);

            logger.VerboseLog(
                $"[PDFium]   Page {pageIndex + 1}: {widthPt:F1}x{heightPt:F1} pt -> {pixelWidth}x{pixelHeight} px @ {dpi} DPI");

            // 7c: Create a bitmap.
            using var bitmap = PdfiumBitmap.Create(pixelWidth, pixelHeight);

            // 7d: Fill with white background.
            bitmap.FillWhite();

            // 7e: Render the page content onto the bitmap.
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap.Handle, page,
                0, 0, pixelWidth, pixelHeight,
                0, flags);

            // 7f: CRITICAL — Draw form fields AFTER RenderPageBitmap!
            formFill.DrawFormOnBitmap(
                bitmap.Handle, page,
                0, 0, pixelWidth, pixelHeight,
                0, flags);

            // 7g: Copy the pixel buffer into managed memory.
            var pageBitmap = bitmap.ToPageBitmap();

            // 7h: Notify form fill that the page is about to close.
            formFill.OnBeforeClosePage(page);

            return pageBitmap;
        }
        finally
        {
            // 7i: Close the page.
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    /// <summary>
    /// Ensures the PDFium library is initialized exactly once per process.
    /// Registers the DLL import resolver and calls FPDF_InitLibraryWithConfig.
    /// </summary>
    private static void EnsureLibraryInitialized(ConsoleLogger logger)
    {
        if (_libraryInitialized) return;

        lock (_initLock)
        {
            if (_libraryInitialized) return;

            // Register the resolver so .NET can find pdfium.dll in the x64/ subfolder.
            PdfiumNative.EnsureResolver();

            // Use version=2 but do NOT provide V8 isolate/platform.
            // The old NuGet build (4522) has V8 pointer compression issues,
            // so we avoid triggering V8 JS execution later in the pipeline.
            var config = new PdfiumNative.FPDF_LIBRARY_CONFIG
            {
                version = 2,
                userFontPaths = IntPtr.Zero,
                isolate = IntPtr.Zero,
                v8EmbedderSlot = 0,
                pPlatform = IntPtr.Zero
            };

            PdfiumNative.FPDF_InitLibraryWithConfig(ref config);
            _libraryInitialized = true;
            logger.VerboseLog("[PDFium] Library initialized.");
        }
    }
}
