using System.Runtime.InteropServices;

namespace XfaFlatten.Rendering.Pdfium;

/// <summary>
/// Manages the PDFium Form Fill Environment lifecycle.
/// Allocates the <see cref="PdfiumNative.FPDF_FORMFILLINFO"/> and
/// <see cref="PdfiumNative.IPDF_JSPLATFORM"/> structs in unmanaged
/// memory with native callback function pointers required for XFA.
/// </summary>
internal sealed class PdfiumFormFill : IDisposable
{
    private IntPtr _formHandle;
    private IntPtr _formInfoPtr;
    private IntPtr _jsPlatformPtr;
    private IntPtr _document;
    private bool _disposed;

    // Keep delegates alive to prevent GC collection while PDFium holds function pointers.
    private static Delegate[]? _callbacks;

    /// <summary>
    /// The native form fill handle, used by FORM_* and FPDF_FFLDraw calls.
    /// </summary>
    public IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _formHandle;
        }
    }

    private PdfiumFormFill(IntPtr formHandle, IntPtr formInfoPtr, IntPtr jsPlatformPtr, IntPtr document)
    {
        _formHandle = formHandle;
        _formInfoPtr = formInfoPtr;
        _jsPlatformPtr = jsPlatformPtr;
        _document = document;
    }

    // ---------------------------------------------------------------
    // FPDF_FORMFILLINFO callback delegate types
    // ---------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FFI_GetPage_Delegate(IntPtr pThis, IntPtr document, int nPageIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FFI_SetTimer_Delegate(IntPtr pThis, int uElapse, IntPtr lpTimerFunc);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FFI_KillTimer_Delegate(IntPtr pThis, int nTimerID);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FFI_GetCurrentPageIndex_Delegate(IntPtr pThis, IntPtr document);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FFI_SetCurrentPage_Delegate(IntPtr pThis, IntPtr document, int iCurPage);

    // ---------------------------------------------------------------
    // IPDF_JSPLATFORM callback delegate types
    // ---------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JsAppAlert_Delegate(IntPtr pThis, IntPtr msg, IntPtr title, int type, int icon);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JsAppBeep_Delegate(IntPtr pThis, int nType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JsAppResponse_Delegate(
        IntPtr pThis, IntPtr question, IntPtr title, IntPtr defaultValue,
        IntPtr cLabel, int bPassword, IntPtr response, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JsDocGetFilePath_Delegate(IntPtr pThis, IntPtr filePath, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JsDocMail_Delegate(
        IntPtr pThis, IntPtr mailData, int length, int bUI,
        IntPtr to, IntPtr subject, IntPtr cc, IntPtr bcc, IntPtr msg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JsDocPrint_Delegate(
        IntPtr pThis, int bUI, int nStart, int nEnd, int bSilent,
        int bShrinkToFit, int bPrintAsImage, int bReverse, int bAnnotations);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JsDocSubmitForm_Delegate(IntPtr pThis, IntPtr formData, int length, IntPtr url);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void JsDocGotoPage_Delegate(IntPtr pThis, int nPageNum);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int JsFieldBrowse_Delegate(IntPtr pThis, IntPtr filePath, int length);

    // ---------------------------------------------------------------
    // FPDF_FORMFILLINFO callback implementations
    // ---------------------------------------------------------------

    private static IntPtr OnGetPage(IntPtr pThis, IntPtr document, int nPageIndex)
    {
        // The XFA engine calls this to retrieve page handles during layout computation.
        return PdfiumNative.FPDF_LoadPage(document, nPageIndex);
    }

    private static int _nextTimerId = 1;
    private static int OnSetTimer(IntPtr pThis, int uElapse, IntPtr lpTimerFunc)
    {
        // For a CLI tool, timers are not needed. Return a dummy ID.
        return _nextTimerId++;
    }

    private static void OnKillTimer(IntPtr pThis, int nTimerID)
    {
        // No-op for CLI tool.
    }

    private static int OnGetCurrentPageIndex(IntPtr pThis, IntPtr document)
    {
        return 0;
    }

    private static void OnSetCurrentPage(IntPtr pThis, IntPtr document, int iCurPage)
    {
        // No-op for CLI tool.
    }

    // ---------------------------------------------------------------
    // IPDF_JSPLATFORM callback implementations
    // ---------------------------------------------------------------

    private static int OnJsAppAlert(IntPtr pThis, IntPtr msg, IntPtr title, int type, int icon)
    {
        // Return OK for any alert.
        return 1; // JSPLATFORM_ALERT_RETURN_OK
    }

    private static void OnJsAppBeep(IntPtr pThis, int nType)
    {
        // No-op for CLI tool.
    }

    private static int OnJsAppResponse(
        IntPtr pThis, IntPtr question, IntPtr title, IntPtr defaultValue,
        IntPtr cLabel, int bPassword, IntPtr response, int length)
    {
        // No response — return 0 bytes.
        return 0;
    }

    private static int OnJsDocGetFilePath(IntPtr pThis, IntPtr filePath, int length)
    {
        // No file path — return 0 bytes.
        return 0;
    }

    private static void OnJsDocMail(
        IntPtr pThis, IntPtr mailData, int length, int bUI,
        IntPtr to, IntPtr subject, IntPtr cc, IntPtr bcc, IntPtr msg)
    {
        // No-op for CLI tool.
    }

    private static void OnJsDocPrint(
        IntPtr pThis, int bUI, int nStart, int nEnd, int bSilent,
        int bShrinkToFit, int bPrintAsImage, int bReverse, int bAnnotations)
    {
        // No-op for CLI tool.
    }

    private static void OnJsDocSubmitForm(IntPtr pThis, IntPtr formData, int length, IntPtr url)
    {
        // No-op for CLI tool.
    }

    private static void OnJsDocGotoPage(IntPtr pThis, int nPageNum)
    {
        // No-op for CLI tool.
    }

    private static int OnJsFieldBrowse(IntPtr pThis, IntPtr filePath, int length)
    {
        // No file selection — return 0 bytes.
        return 0;
    }

    /// <summary>
    /// Initializes the PDFium Form Fill Environment for the given document.
    /// </summary>
    public static PdfiumFormFill Init(IntPtr document, bool xfaEnabled = false)
    {
        // ---------------------------------------------------------------
        // 1. Create FPDF_FORMFILLINFO callback delegates.
        // ---------------------------------------------------------------
        var getPageDel = new FFI_GetPage_Delegate(OnGetPage);
        var setTimerDel = new FFI_SetTimer_Delegate(OnSetTimer);
        var killTimerDel = new FFI_KillTimer_Delegate(OnKillTimer);
        var getCurrentPageIndexDel = new FFI_GetCurrentPageIndex_Delegate(OnGetCurrentPageIndex);
        var setCurrentPageDel = new FFI_SetCurrentPage_Delegate(OnSetCurrentPage);

        // ---------------------------------------------------------------
        // 2. Create IPDF_JSPLATFORM callback delegates.
        // ---------------------------------------------------------------
        var jsAlertDel = new JsAppAlert_Delegate(OnJsAppAlert);
        var jsBeepDel = new JsAppBeep_Delegate(OnJsAppBeep);
        var jsResponseDel = new JsAppResponse_Delegate(OnJsAppResponse);
        var jsGetFilePathDel = new JsDocGetFilePath_Delegate(OnJsDocGetFilePath);
        var jsMailDel = new JsDocMail_Delegate(OnJsDocMail);
        var jsPrintDel = new JsDocPrint_Delegate(OnJsDocPrint);
        var jsSubmitFormDel = new JsDocSubmitForm_Delegate(OnJsDocSubmitForm);
        var jsGotoPageDel = new JsDocGotoPage_Delegate(OnJsDocGotoPage);
        var jsFieldBrowseDel = new JsFieldBrowse_Delegate(OnJsFieldBrowse);

        // Keep ALL delegates alive for the process lifetime (prevents GC collection).
        _callbacks =
        [
            getPageDel, setTimerDel, killTimerDel, getCurrentPageIndexDel, setCurrentPageDel,
            jsAlertDel, jsBeepDel, jsResponseDel, jsGetFilePathDel, jsMailDel,
            jsPrintDel, jsSubmitFormDel, jsGotoPageDel, jsFieldBrowseDel
        ];

        // ---------------------------------------------------------------
        // 3. Allocate IPDF_JSPLATFORM in unmanaged memory.
        // ---------------------------------------------------------------
        var jsPlatform = new PdfiumNative.IPDF_JSPLATFORM
        {
            version = 3,
            app_alert = Marshal.GetFunctionPointerForDelegate(jsAlertDel),
            app_beep = Marshal.GetFunctionPointerForDelegate(jsBeepDel),
            app_response = Marshal.GetFunctionPointerForDelegate(jsResponseDel),
            Doc_getFilePath = Marshal.GetFunctionPointerForDelegate(jsGetFilePathDel),
            Doc_mail = Marshal.GetFunctionPointerForDelegate(jsMailDel),
            Doc_print = Marshal.GetFunctionPointerForDelegate(jsPrintDel),
            Doc_submitForm = Marshal.GetFunctionPointerForDelegate(jsSubmitFormDel),
            Doc_gotoPage = Marshal.GetFunctionPointerForDelegate(jsGotoPageDel),
            Field_browse = Marshal.GetFunctionPointerForDelegate(jsFieldBrowseDel),
            m_pFormfillinfo = IntPtr.Zero, // Will be set after formInfoPtr is allocated
            m_isolate = IntPtr.Zero,
            m_v8EmbedderSlot = 0,
        };

        int jsPlatformSize = Marshal.SizeOf<PdfiumNative.IPDF_JSPLATFORM>();
        IntPtr jsPlatformPtr = Marshal.AllocHGlobal(jsPlatformSize);
        Marshal.StructureToPtr(jsPlatform, jsPlatformPtr, false);

        // ---------------------------------------------------------------
        // 4. Build FPDF_FORMFILLINFO with m_pJsPlatform pointing to our JS platform.
        // ---------------------------------------------------------------
        var formInfo = new PdfiumNative.FPDF_FORMFILLINFO
        {
            version = 2,
            xfa_disabled = xfaEnabled ? 0 : 1,
            m_pJsPlatform = jsPlatformPtr,
            FFI_GetPage = Marshal.GetFunctionPointerForDelegate(getPageDel),
            FFI_SetTimer = Marshal.GetFunctionPointerForDelegate(setTimerDel),
            FFI_KillTimer = Marshal.GetFunctionPointerForDelegate(killTimerDel),
            FFI_GetCurrentPageIndex = Marshal.GetFunctionPointerForDelegate(getCurrentPageIndexDel),
            FFI_SetCurrentPage = Marshal.GetFunctionPointerForDelegate(setCurrentPageDel),
        };

        int formInfoSize = Marshal.SizeOf<PdfiumNative.FPDF_FORMFILLINFO>();
        IntPtr formInfoPtr = Marshal.AllocHGlobal(formInfoSize);
        Marshal.StructureToPtr(formInfo, formInfoPtr, false);

        try
        {
            var handle = PdfiumNative.FPDFDOC_InitFormFillEnvironmentPtr(document, formInfoPtr);
            if (handle == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(formInfoPtr);
                Marshal.FreeHGlobal(jsPlatformPtr);
                throw new InvalidOperationException(
                    "PDFium failed to initialize the Form Fill Environment.");
            }

            return new PdfiumFormFill(handle, formInfoPtr, jsPlatformPtr, document);
        }
        catch
        {
            Marshal.FreeHGlobal(formInfoPtr);
            Marshal.FreeHGlobal(jsPlatformPtr);
            throw;
        }
    }

    /// <summary>
    /// Notifies PDFium that a page has been loaded (triggers page-level scripts).
    /// </summary>
    public void OnAfterLoadPage(IntPtr page)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PdfiumNative.FORM_OnAfterLoadPage(page, _formHandle);
    }

    /// <summary>
    /// Notifies PDFium that a page is about to be closed.
    /// </summary>
    public void OnBeforeClosePage(IntPtr page)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PdfiumNative.FORM_OnBeforeClosePage(page, _formHandle);
    }

    /// <summary>
    /// Draws form fields onto a bitmap. Must be called AFTER FPDF_RenderPageBitmap.
    /// </summary>
    public void DrawFormOnBitmap(IntPtr bitmap, IntPtr page,
        int startX, int startY, int sizeX, int sizeY, int rotate, int flags)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PdfiumNative.FPDF_FFLDraw(
            _formHandle, bitmap, page,
            startX, startY, sizeX, sizeY,
            rotate, flags);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_formHandle != IntPtr.Zero)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_formHandle);
            _formHandle = IntPtr.Zero;
        }

        if (_formInfoPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formInfoPtr);
            _formInfoPtr = IntPtr.Zero;
        }

        if (_jsPlatformPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_jsPlatformPtr);
            _jsPlatformPtr = IntPtr.Zero;
        }
    }
}
