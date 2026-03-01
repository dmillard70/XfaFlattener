using System.Runtime.InteropServices;

namespace XfaFlatten.Rendering.Pdfium;

/// <summary>
/// Wraps a PDFium bitmap handle with safe creation, fill, buffer read, and disposal.
/// </summary>
internal sealed class PdfiumBitmap : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>Bitmap width in pixels.</summary>
    public int Width { get; }

    /// <summary>Bitmap height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per scanline row.</summary>
    public int Stride { get; }

    /// <summary>The native bitmap handle.</summary>
    public IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handle;
        }
    }

    private PdfiumBitmap(IntPtr handle, int width, int height, int stride)
    {
        _handle = handle;
        Width = width;
        Height = height;
        Stride = stride;
    }

    /// <summary>
    /// Creates a new BGRA bitmap of the given dimensions.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>A new <see cref="PdfiumBitmap"/> wrapping the native handle.</returns>
    /// <exception cref="InvalidOperationException">Thrown when PDFium fails to allocate the bitmap.</exception>
    public static PdfiumBitmap Create(int width, int height)
    {
        // alpha=1 for BGRA (4 bytes per pixel with alpha channel)
        var handle = PdfiumNative.FPDFBitmap_Create(width, height, 1);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"PDFium failed to create a {width}x{height} bitmap. " +
                "The system may be out of memory.");
        }

        var stride = PdfiumNative.FPDFBitmap_GetStride(handle);
        return new PdfiumBitmap(handle, width, height, stride);
    }

    /// <summary>
    /// Fills the entire bitmap with the specified color (ARGB as 0xAARRGGBB).
    /// </summary>
    public void FillWhite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 0xFFFFFFFF = fully opaque white
        PdfiumNative.FPDFBitmap_FillRect(_handle, 0, 0, Width, Height, 0xFFFFFFFF);
    }

    /// <summary>
    /// Copies the raw pixel buffer out of the native bitmap into a managed byte array.
    /// </summary>
    /// <returns>A byte array containing BGRA pixel data.</returns>
    public byte[] CopyBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bufferPtr = PdfiumNative.FPDFBitmap_GetBuffer(_handle);
        if (bufferPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDFium bitmap buffer is null.");
        }

        int totalBytes = Stride * Height;
        var data = new byte[totalBytes];
        Marshal.Copy(bufferPtr, data, 0, totalBytes);
        return data;
    }

    /// <summary>
    /// Converts this bitmap into a <see cref="PageBitmap"/> record and returns it.
    /// The underlying native bitmap is still valid until <see cref="Dispose"/> is called.
    /// </summary>
    public PageBitmap ToPageBitmap()
    {
        return new PageBitmap(CopyBuffer(), Width, Height, Stride);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            PdfiumNative.FPDFBitmap_Destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
