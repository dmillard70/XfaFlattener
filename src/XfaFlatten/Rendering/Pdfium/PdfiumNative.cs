using System.Runtime.InteropServices;

namespace XfaFlatten.Rendering.Pdfium;

/// <summary>
/// P/Invoke declarations for the PDFium native library (V8+XFA build).
/// </summary>
internal static partial class PdfiumNative
{
    private const string PdfiumLib = "pdfium";

    // ---------------------------------------------------------------
    // Render flags
    // ---------------------------------------------------------------

    /// <summary>Render annotations.</summary>
    public const int FPDF_ANNOT = 0x01;

    /// <summary>Suppress native text rendering — use PDFium's own renderer.</summary>
    public const int FPDF_NO_NATIVETEXT = 0x04;

    /// <summary>Render in print mode (essential for XFA).</summary>
    public const int FPDF_PRINTING = 0x800;

    // ---------------------------------------------------------------
    // Bitmap formats
    // ---------------------------------------------------------------

    /// <summary>BGRA pixel format, 4 bytes per pixel.</summary>
    public const int FPDFBitmap_BGRA = 4;

    // ---------------------------------------------------------------
    // XFA load results
    // ---------------------------------------------------------------

    public const int FPDF_ERR_SUCCESS = 0;
    public const int FPDF_ERR_UNKNOWN = 1;
    public const int FPDF_ERR_FILE = 2;
    public const int FPDF_ERR_FORMAT = 3;
    public const int FPDF_ERR_PASSWORD = 4;
    public const int FPDF_ERR_SECURITY = 5;
    public const int FPDF_ERR_PAGE = 6;
    public const int FPDF_ERR_XFALOAD = 7;
    public const int FPDF_ERR_XFALAYOUT = 8;

    // ---------------------------------------------------------------
    // Structs
    // ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_LIBRARY_CONFIG
    {
        /// <summary>Version of the struct. Must be 2.</summary>
        public int version;

        /// <summary>Null-terminated list of user font paths. Usually null.</summary>
        public IntPtr userFontPaths;

        /// <summary>Pointer to a V8 isolate. IntPtr.Zero to let PDFium create one.</summary>
        public IntPtr isolate;

        /// <summary>The V8 embedder slot to use. Usually 0.</summary>
        public uint v8EmbedderSlot;

        /// <summary>Pointer to a v8::Platform. IntPtr.Zero to let PDFium create one.</summary>
        public IntPtr pPlatform;
    }

    /// <summary>
    /// The FPDF_FORMFILLINFO struct (version 2) used for XFA form fill callbacks.
    /// Version 2 adds XFA-specific fields at the end.
    /// All callback function pointers are set to IntPtr.Zero (unused).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FORMFILLINFO
    {
        /// <summary>Must be 2 for XFA support.</summary>
        public int version;

        // --- Version 1 callbacks (all set to IntPtr.Zero) ---

        /// <summary>Release callback.</summary>
        public IntPtr Release;
        /// <summary>FFI_Invalidate callback.</summary>
        public IntPtr FFI_Invalidate;
        /// <summary>FFI_OutputSelectedRect callback.</summary>
        public IntPtr FFI_OutputSelectedRect;
        /// <summary>FFI_SetCursor callback.</summary>
        public IntPtr FFI_SetCursor;
        /// <summary>FFI_SetTimer callback.</summary>
        public IntPtr FFI_SetTimer;
        /// <summary>FFI_KillTimer callback.</summary>
        public IntPtr FFI_KillTimer;
        /// <summary>FFI_GetLocalTime callback.</summary>
        public IntPtr FFI_GetLocalTime;
        /// <summary>FFI_OnChange callback.</summary>
        public IntPtr FFI_OnChange;
        /// <summary>FFI_GetPage callback.</summary>
        public IntPtr FFI_GetPage;
        /// <summary>FFI_GetCurrentPage callback.</summary>
        public IntPtr FFI_GetCurrentPage;
        /// <summary>FFI_GetRotation callback.</summary>
        public IntPtr FFI_GetRotation;
        /// <summary>FFI_ExecuteNamedAction callback.</summary>
        public IntPtr FFI_ExecuteNamedAction;
        /// <summary>FFI_SetTextFieldFocus callback.</summary>
        public IntPtr FFI_SetTextFieldFocus;
        /// <summary>FFI_DoURIAction callback.</summary>
        public IntPtr FFI_DoURIAction;
        /// <summary>FFI_DoGoToAction callback.</summary>
        public IntPtr FFI_DoGoToAction;

        // --- Version 2 fields (m_pJsPlatform, then XFA callbacks) ---

        /// <summary>Pointer to IPDF_JSPLATFORM. Can be IntPtr.Zero.</summary>
        public IntPtr m_pJsPlatform;

        // --- XFA-specific callbacks (version 2) ---

        /// <summary>xfa_disabled flag. Must be 0 (false) for XFA support.</summary>
        public int xfa_disabled;

        /// <summary>FFI_DisplayCaret callback.</summary>
        public IntPtr FFI_DisplayCaret;
        /// <summary>FFI_GetCurrentPageIndex callback.</summary>
        public IntPtr FFI_GetCurrentPageIndex;
        /// <summary>FFI_SetCurrentPage callback.</summary>
        public IntPtr FFI_SetCurrentPage;
        /// <summary>FFI_GotoURL callback.</summary>
        public IntPtr FFI_GotoURL;
        /// <summary>FFI_GetPageViewRect callback.</summary>
        public IntPtr FFI_GetPageViewRect;
        /// <summary>FFI_PageEvent callback.</summary>
        public IntPtr FFI_PageEvent;
        /// <summary>FFI_PopupMenu callback.</summary>
        public IntPtr FFI_PopupMenu;
        /// <summary>FFI_OpenFile callback.</summary>
        public IntPtr FFI_OpenFile;
        /// <summary>FFI_EmailTo callback.</summary>
        public IntPtr FFI_EmailTo;
        /// <summary>FFI_UploadTo callback.</summary>
        public IntPtr FFI_UploadTo;
        /// <summary>FFI_GetPlatform callback.</summary>
        public IntPtr FFI_GetPlatform;
        /// <summary>FFI_GetLanguage callback.</summary>
        public IntPtr FFI_GetLanguage;
        /// <summary>FFI_DownloadFromURL callback.</summary>
        public IntPtr FFI_DownloadFromURL;
        /// <summary>FFI_PostRequestURL callback.</summary>
        public IntPtr FFI_PostRequestURL;
        /// <summary>FFI_PutRequestURL callback.</summary>
        public IntPtr FFI_PutRequestURL;
    }

    // ---------------------------------------------------------------
    // Library init / destroy
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibraryWithConfig(ref FPDF_LIBRARY_CONFIG config);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_DestroyLibrary();

    // ---------------------------------------------------------------
    // Document
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPStr)] string filePath,
        [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadMemDocument(
        byte[] dataBuffer,
        int size,
        [MarshalAs(UnmanagedType.LPStr)] string? password);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetLastError();

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    // ---------------------------------------------------------------
    // XFA
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_LoadXFA(IntPtr document);

    // ---------------------------------------------------------------
    // Page
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern float FPDF_GetPageHeightF(IntPtr page);

    // ---------------------------------------------------------------
    // Form fill environment
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFDOC_InitFormFillEnvironment(
        IntPtr document,
        ref FPDF_FORMFILLINFO formInfo);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_DoDocumentJSAction(IntPtr formHandle);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FORM_DoDocumentOpenAction(IntPtr formHandle);

    // ---------------------------------------------------------------
    // Bitmap
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(
        int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_FillRect(
        IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);

    // ---------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [DllImport(PdfiumLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_FFLDraw(
        IntPtr formHandle,
        IntPtr bitmap,
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    // ---------------------------------------------------------------
    // DLL import resolver
    // ---------------------------------------------------------------

    private static IntPtr _resolvedHandle;
    private static bool _resolverRegistered;
    private static readonly object _resolverLock = new();

    /// <summary>
    /// Registers a <see cref="NativeLibrary"/> DLL import resolver so that
    /// the runtime can find <c>pdfium.dll</c> in the <c>x64/</c> subfolder
    /// placed there by the NuGet package.
    /// </summary>
    public static void EnsureResolver()
    {
        if (_resolverRegistered) return;

        lock (_resolverLock)
        {
            if (_resolverRegistered) return;

            NativeLibrary.SetDllImportResolver(
                typeof(PdfiumNative).Assembly,
                DllImportResolver);

            _resolverRegistered = true;
        }
    }

    private static IntPtr DllImportResolver(
        string libraryName,
        System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (libraryName != PdfiumLib)
            return IntPtr.Zero;

        if (_resolvedHandle != IntPtr.Zero)
            return _resolvedHandle;

        // Try loading from the default search path first.
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out _resolvedHandle))
            return _resolvedHandle;

        // Try the x64/ subfolder next to the assembly (NuGet package layout).
        var assemblyDir = Path.GetDirectoryName(assembly.Location) ?? ".";
        var candidate = Path.Combine(assemblyDir, "x64", "pdfium.dll");
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _resolvedHandle))
            return _resolvedHandle;

        // Try the runtimes/win-x64/native/ subfolder (alternate NuGet layout).
        candidate = Path.Combine(assemblyDir, "runtimes", "win-x64", "native", "pdfium.dll");
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _resolvedHandle))
            return _resolvedHandle;

        return IntPtr.Zero;
    }

    /// <summary>
    /// Translates a PDFium error code from <see cref="FPDF_GetLastError"/> to a human-readable string.
    /// </summary>
    public static string GetErrorDescription(uint errorCode)
    {
        return errorCode switch
        {
            FPDF_ERR_SUCCESS  => "Success",
            FPDF_ERR_UNKNOWN  => "Unknown error",
            FPDF_ERR_FILE     => "File not found or could not be opened",
            FPDF_ERR_FORMAT   => "File is not a valid PDF",
            FPDF_ERR_PASSWORD => "Password required or incorrect",
            FPDF_ERR_SECURITY => "Security error (unsupported security scheme)",
            FPDF_ERR_PAGE     => "Page not found or content error",
            FPDF_ERR_XFALOAD  => "XFA load error",
            FPDF_ERR_XFALAYOUT => "XFA layout error",
            _ => $"Error code {errorCode}"
        };
    }
}
