using System.Runtime.InteropServices;

namespace XfaFlatten.Rendering.Pdfium;

/// <summary>
/// Manages the PDFium Form Fill Environment lifecycle.
/// Pins the <see cref="PdfiumNative.FPDF_FORMFILLINFO"/> struct in memory so
/// PDFium can safely reference it for the entire lifetime of the environment.
/// </summary>
internal sealed class PdfiumFormFill : IDisposable
{
    private IntPtr _formHandle;
    private GCHandle _formInfoPin;
    private bool _disposed;

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

    private PdfiumFormFill(IntPtr formHandle, GCHandle formInfoPin)
    {
        _formHandle = formHandle;
        _formInfoPin = formInfoPin;
    }

    /// <summary>
    /// Initializes the PDFium Form Fill Environment for the given document.
    /// </summary>
    /// <param name="document">A valid PDFium document handle.</param>
    /// <param name="xfaEnabled">If true, use version 2 (XFA). If false, use version 1 (AcroForm only).</param>
    /// <returns>A new <see cref="PdfiumFormFill"/> managing the form fill handle.</returns>
    public static PdfiumFormFill Init(IntPtr document, bool xfaEnabled = false)
    {
        // Always use version 2 for the struct layout (so PDFium parses all fields).
        // Use xfa_disabled to control XFA features without triggering V8.
        var formInfo = new PdfiumNative.FPDF_FORMFILLINFO
        {
            version = 2,
            xfa_disabled = xfaEnabled ? 0 : 1
        };

        // Pin the struct so PDFium's pointer to it remains valid.
        var pin = GCHandle.Alloc(formInfo, GCHandleType.Pinned);

        try
        {
            var handle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document, ref formInfo);
            if (handle == IntPtr.Zero)
            {
                pin.Free();
                throw new InvalidOperationException(
                    "PDFium failed to initialize the Form Fill Environment.");
            }

            return new PdfiumFormFill(handle, pin);
        }
        catch
        {
            pin.Free();
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

        // NOTE: We intentionally skip FPDFDOC_ExitFormFillEnvironment here.
        // The V8+XFA PDFium build (NuGet 4522) crashes with an AccessViolationException
        // during V8 cleanup in ExitFormFillEnvironment. Since this is a CLI tool,
        // process exit will reclaim all native memory. The form handle is simply
        // abandoned and the struct pin is released.
        _formHandle = IntPtr.Zero;

        if (_formInfoPin.IsAllocated)
            _formInfoPin.Free();
    }
}
