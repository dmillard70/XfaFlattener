using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace XfaFlatten.Analysis;

/// <summary>
/// Exports XFA XML data packets from a PDF document.
/// </summary>
public sealed class XfaDataExtractor
{
    /// <summary>
    /// Exports all XFA packets from the given PDF to individual XML files.
    /// </summary>
    /// <param name="pdfPath">Path to the source PDF file.</param>
    /// <param name="outputDirectory">Directory where the XML files will be written.</param>
    /// <exception cref="FileNotFoundException">The PDF file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The PDF does not contain XFA data.</exception>
    public void ExportAll(string pdfPath, string outputDirectory)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF file not found.", pdfPath);

        Directory.CreateDirectory(outputDirectory);

        string baseName = Path.GetFileNameWithoutExtension(pdfPath);

        using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);

        byte[] xfaBytes = ExtractXfaStreamBytes(document);
        string xfaXml = Encoding.UTF8.GetString(xfaBytes);

        // Write full XFA XML
        string fullPath = Path.Combine(outputDirectory, $"{baseName}.xfa.xml");
        File.WriteAllText(fullPath, xfaXml, Encoding.UTF8);

        // Extract and write individual packets
        string? templateXml = XfaDetector.ExtractPacket(xfaXml, "template");
        if (templateXml is not null)
        {
            string templatePath = Path.Combine(outputDirectory, $"{baseName}.template.xml");
            File.WriteAllText(templatePath, templateXml, Encoding.UTF8);
        }

        string? datasetsXml = XfaDetector.ExtractPacket(xfaXml, "datasets");
        if (datasetsXml is not null)
        {
            string dataPath = Path.Combine(outputDirectory, $"{baseName}.data.xml");
            File.WriteAllText(dataPath, datasetsXml, Encoding.UTF8);
        }
    }

    /// <summary>
    /// Extracts the raw XFA stream bytes from a PDF document.
    /// </summary>
    private static byte[] ExtractXfaStreamBytes(PdfDocument document)
    {
        var catalog = document.Internals.Catalog;
        var acroForm = catalog.Elements.GetDictionary("/AcroForm")
            ?? throw new InvalidOperationException("PDF does not contain an AcroForm dictionary.");

        var xfaItem = acroForm.Elements["/XFA"]
            ?? throw new InvalidOperationException("PDF does not contain XFA data.");

        byte[]? bytes = XfaDetector.ExtractXfaBytes(xfaItem);
        if (bytes is null || bytes.Length == 0)
            throw new InvalidOperationException("XFA stream is empty or could not be read.");

        return bytes;
    }
}
