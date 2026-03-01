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
        int formType = PdfiumNative.FPDF_GetFormType(document);
        string formTypeStr = formType switch
        {
            PdfiumNative.FORMTYPE_NONE => "NONE",
            PdfiumNative.FORMTYPE_ACRO_FORM => "ACRO_FORM",
            PdfiumNative.FORMTYPE_XFA_FULL => "XFA_FULL",
            PdfiumNative.FORMTYPE_XFA_FOREGROUND => "XFA_FOREGROUND",
            _ => $"UNKNOWN({formType})"
        };
        logger.VerboseLog($"[PDFium] Form type: {formTypeStr}");

        int pc0 = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Page count before XFA init: {pc0}");

        // Step 3: Initialize Form Fill Environment with XFA enabled.
        // In modern PDFium, InitFormFillEnvironment triggers the XFA engine
        // when xfa_disabled=0 (including layout computation).
        logger.VerboseLog("[PDFium] Initializing Form Fill Environment (XFA enabled)...");
        using var formFill = PdfiumFormFill.Init(document, xfaEnabled: true);

        int pc1 = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Page count after form fill init: {pc1}");

        // Step 4: Load XFA content (compatibility call; may be a no-op in newer builds).
        logger.VerboseLog("[PDFium] Loading XFA...");
        int xfaResult = PdfiumNative.FPDF_LoadXFA(document);
        logger.VerboseLog($"[PDFium] FPDF_LoadXFA returned {xfaResult}");

        int pc2 = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Page count after FPDF_LoadXFA: {pc2}");

        // Step 5: Execute document-level JavaScript actions.
        logger.VerboseLog("[PDFium] Executing document JS actions...");
        PdfiumNative.FORM_DoDocumentJSAction(formFill.Handle);

        // Step 6: Execute document open actions.
        logger.VerboseLog("[PDFium] Executing document open actions...");
        PdfiumNative.FORM_DoDocumentOpenAction(formFill.Handle);

        int pc3 = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Page count after JS/Open actions: {pc3}");

        // Probe: Try loading the first page to trigger XFA layout, then re-check page count.
        var probePage = PdfiumNative.FPDF_LoadPage(document, 0);
        if (probePage != IntPtr.Zero)
        {
            formFill.OnAfterLoadPage(probePage);
            int pc4 = PdfiumNative.FPDF_GetPageCount(document);
            logger.VerboseLog($"[PDFium] Page count after loading page 0: {pc4}");
            formFill.OnBeforeClosePage(probePage);
            PdfiumNative.FPDF_ClosePage(probePage);
        }

        // Step 7: Render each page — use the final page count.
        int pageCount = PdfiumNative.FPDF_GetPageCount(document);
        logger.VerboseLog($"[PDFium] Final page count: {pageCount}");

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

            // Use version=2 with V8+XFA support.
            // Setting isolate and pPlatform to IntPtr.Zero lets PDFium
            // create its own V8 isolate and platform internally.
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
