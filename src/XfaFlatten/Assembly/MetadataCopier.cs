using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace XfaFlatten.Assembly;

/// <summary>
/// Copies metadata (title, author, subject, etc.) from the original PDF to the output PDF.
/// </summary>
public static class MetadataCopier
{
    /// <summary>
    /// Copies standard metadata fields from the source PDF to the destination PDF file.
    /// The destination file must already exist.
    /// </summary>
    /// <param name="sourcePdfPath">Path to the original PDF.</param>
    /// <param name="destinationPdfPath">Path to the output PDF to update.</param>
    public static void CopyMetadata(string sourcePdfPath, string destinationPdfPath)
    {
        // Read metadata from the source.
        PdfDocument sourceDoc;
        try
        {
            sourceDoc = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        }
        catch
        {
            // If we can't read the source, skip metadata copying silently.
            return;
        }

        using (sourceDoc)
        {
            var srcInfo = sourceDoc.Info;

            // Only proceed if there's any metadata to copy (excluding Creator).
            if (string.IsNullOrEmpty(srcInfo.Title) &&
                string.IsNullOrEmpty(srcInfo.Author) &&
                string.IsNullOrEmpty(srcInfo.Subject) &&
                string.IsNullOrEmpty(srcInfo.Keywords))
            {
                return;
            }

            // Open the destination for modification.
            using var destDoc = PdfReader.Open(destinationPdfPath, PdfDocumentOpenMode.Modify);
            var destInfo = destDoc.Info;

            if (!string.IsNullOrEmpty(srcInfo.Title))
                destInfo.Title = srcInfo.Title;

            if (!string.IsNullOrEmpty(srcInfo.Author))
                destInfo.Author = srcInfo.Author;

            if (!string.IsNullOrEmpty(srcInfo.Subject))
                destInfo.Subject = srcInfo.Subject;

            if (!string.IsNullOrEmpty(srcInfo.Keywords))
                destInfo.Keywords = srcInfo.Keywords;

            // Do NOT copy Creator — the original XFA Creator ("Adobe Experience
            // Manager forms PDF forms") can confuse Acrobat Reader into activating
            // XFA processing on the flattened PDF.  Keep PDFsharp's default Creator.

            destDoc.Save(destinationPdfPath);
        }
    }
}
