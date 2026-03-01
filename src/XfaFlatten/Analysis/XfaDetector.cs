using System.Text;
using System.Xml;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace XfaFlatten.Analysis;

/// <summary>
/// Result of XFA detection on a PDF document.
/// </summary>
/// <param name="Type">The detected XFA type.</param>
/// <param name="PageCount">Number of pages in the document, or 0 if detection failed.</param>
/// <param name="ErrorMessage">Error message if detection failed, null otherwise.</param>
public record XfaDetectionResult(XfaType Type, int PageCount, string? ErrorMessage = null);

/// <summary>
/// Detects whether a PDF contains XFA form data and determines its type.
/// </summary>
public sealed class XfaDetector
{
    /// <summary>
    /// Analyzes the PDF at the given path and returns the XFA detection result.
    /// </summary>
    /// <param name="pdfPath">Absolute path to the PDF file.</param>
    /// <returns>Detection result with XFA type, page count, and any error message.</returns>
    /// <exception cref="FileNotFoundException">The specified file does not exist.</exception>
    public XfaDetectionResult Detect(string pdfPath)
    {
        if (!File.Exists(pdfPath))
            throw new FileNotFoundException("PDF file not found.", pdfPath);

        PdfDocument document;
        try
        {
            document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException ex)
        {
            return new XfaDetectionResult(XfaType.None, 0, $"Invalid or corrupt PDF: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return new XfaDetectionResult(XfaType.None, 0, $"Password-protected PDF: {ex.Message}");
        }

        using (document)
        {
            int pageCount = document.PageCount;

            // Navigate to /Root (Catalog) -> /AcroForm -> /XFA
            var catalog = document.Internals.Catalog;
            var acroForm = catalog.Elements.GetDictionary("/AcroForm");
            if (acroForm is null)
                return new XfaDetectionResult(XfaType.None, pageCount);

            var xfaItem = acroForm.Elements["/XFA"];
            if (xfaItem is null)
                return new XfaDetectionResult(XfaType.None, pageCount);

            // Extract XFA stream bytes
            byte[]? xfaBytes = ExtractXfaBytes(xfaItem);
            if (xfaBytes is null || xfaBytes.Length == 0)
                return new XfaDetectionResult(XfaType.None, pageCount);

            // Determine if dynamic by inspecting the template packet
            bool isDynamic = HasDynamicLayout(xfaBytes);

            // Check if AcroForm also has /Fields with entries (hybrid detection)
            bool hasAcroFormFields = HasAcroFormFields(acroForm);

            XfaType type;
            if (hasAcroFormFields)
                type = XfaType.Hybrid;
            else if (isDynamic)
                type = XfaType.Dynamic;
            else
                type = XfaType.Static;

            return new XfaDetectionResult(type, pageCount);
        }
    }

    /// <summary>
    /// Extracts the raw XFA stream bytes from the /XFA entry.
    /// The /XFA value can be a single stream or an array of alternating name/stream pairs.
    /// </summary>
    internal static byte[]? ExtractXfaBytes(PdfItem xfaItem)
    {
        // Resolve indirect references
        if (xfaItem is PdfReference reference)
            xfaItem = reference.Value;

        // Single stream case
        if (xfaItem is PdfDictionary dict && dict.Stream?.Value is { Length: > 0 } streamBytes)
            return streamBytes;

        // Array of alternating [name, stream, name, stream, ...]
        if (xfaItem is PdfArray array)
            return ExtractXfaBytesFromArray(array);

        return null;
    }

    /// <summary>
    /// Extracts and concatenates XFA bytes from an array of name/stream pairs.
    /// </summary>
    internal static byte[] ExtractXfaBytesFromArray(PdfArray array)
    {
        using var ms = new MemoryStream();

        for (int i = 0; i < array.Elements.Count; i++)
        {
            var element = array.Elements[i];

            // Resolve indirect references
            if (element is PdfReference refItem)
                element = refItem.Value;

            // Skip name entries (odd-index entries in alternating name/stream pairs are the streams)
            if (element is PdfDictionary streamDict && streamDict.Stream?.Value is { Length: > 0 } data)
            {
                ms.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Checks whether the XFA template contains dynamic layout attributes
    /// (layout="tb", "rl-tb", or "lr-tb" on subform elements).
    /// </summary>
    internal static bool HasDynamicLayout(byte[] xfaBytes)
    {
        string xfaXml = Encoding.UTF8.GetString(xfaBytes);

        // Try to find and parse the template packet
        string? templateXml = ExtractPacket(xfaXml, "template");
        if (templateXml is null)
        {
            // If we cannot isolate the template packet, search the entire XFA content
            templateXml = xfaXml;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            };

            using var reader = XmlReader.Create(new StringReader(templateXml), settings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element &&
                    reader.LocalName == "subform")
                {
                    string? layout = reader.GetAttribute("layout");
                    if (layout is "tb" or "rl-tb" or "lr-tb")
                        return true;
                }
            }
        }
        catch (XmlException)
        {
            // If XML parsing fails, try simple string matching as a fallback
            return xfaXml.Contains("layout=\"tb\"", StringComparison.OrdinalIgnoreCase) ||
                   xfaXml.Contains("layout=\"rl-tb\"", StringComparison.OrdinalIgnoreCase) ||
                   xfaXml.Contains("layout=\"lr-tb\"", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Checks whether the AcroForm dictionary has a /Fields array with at least one entry.
    /// </summary>
    internal static bool HasAcroFormFields(PdfDictionary acroForm)
    {
        var fields = acroForm.Elements.GetArray("/Fields");
        return fields is not null && fields.Elements.Count > 0;
    }

    /// <summary>
    /// Extracts a named packet from the combined XFA XML string.
    /// Packets are typically wrapped as elements in an xdp:xdp root.
    /// </summary>
    internal static string? ExtractPacket(string xfaXml, string packetName)
    {
        // Look for the template element in the XFA XML
        // The template packet is typically: <template xmlns="http://www.xfa.org/schema/xfa-template/...">
        // We search for the opening and closing tags

        // Try namespace-qualified search first
        int startIdx = FindPacketStart(xfaXml, packetName);
        if (startIdx < 0)
            return null;

        // Find the closing tag - it could be </template> or </xfa:template> etc.
        string closingTag = $"</{packetName}>";
        int endIdx = xfaXml.IndexOf(closingTag, startIdx, StringComparison.OrdinalIgnoreCase);

        if (endIdx < 0)
        {
            // Try with namespace prefix
            // Search for any closing tag that ends with :packetName>
            int searchPos = startIdx;
            while (searchPos < xfaXml.Length)
            {
                int closePos = xfaXml.IndexOf("</", searchPos, StringComparison.Ordinal);
                if (closePos < 0) break;

                int gtPos = xfaXml.IndexOf('>', closePos);
                if (gtPos < 0) break;

                string tagContent = xfaXml.Substring(closePos + 2, gtPos - closePos - 2).Trim();
                if (tagContent.Equals(packetName, StringComparison.OrdinalIgnoreCase) ||
                    tagContent.EndsWith($":{packetName}", StringComparison.OrdinalIgnoreCase))
                {
                    endIdx = closePos;
                    closingTag = xfaXml.Substring(closePos, gtPos - closePos + 1);
                    break;
                }

                searchPos = gtPos + 1;
            }
        }

        if (endIdx < 0)
            return null;

        return xfaXml.Substring(startIdx, endIdx - startIdx + closingTag.Length);
    }

    /// <summary>
    /// Finds the start position of a packet element in the XFA XML.
    /// Handles both unqualified and namespace-prefixed element names.
    /// </summary>
    private static int FindPacketStart(string xfaXml, string packetName)
    {
        // Search for <packetName or <prefix:packetName
        int pos = 0;
        while (pos < xfaXml.Length)
        {
            int tagStart = xfaXml.IndexOf('<', pos);
            if (tagStart < 0) break;

            // Skip comments and processing instructions
            if (tagStart + 1 < xfaXml.Length && (xfaXml[tagStart + 1] == '!' || xfaXml[tagStart + 1] == '?'))
            {
                pos = tagStart + 2;
                continue;
            }

            // Skip closing tags
            if (tagStart + 1 < xfaXml.Length && xfaXml[tagStart + 1] == '/')
            {
                pos = tagStart + 2;
                continue;
            }

            // Extract the tag name (up to whitespace, >, or /)
            int nameStart = tagStart + 1;
            int nameEnd = nameStart;
            while (nameEnd < xfaXml.Length && xfaXml[nameEnd] != ' ' && xfaXml[nameEnd] != '>'
                   && xfaXml[nameEnd] != '/' && xfaXml[nameEnd] != '\t' && xfaXml[nameEnd] != '\n'
                   && xfaXml[nameEnd] != '\r')
            {
                nameEnd++;
            }

            string tagName = xfaXml.Substring(nameStart, nameEnd - nameStart);

            // Match either "packetName" or "prefix:packetName"
            if (tagName.Equals(packetName, StringComparison.OrdinalIgnoreCase) ||
                tagName.EndsWith($":{packetName}", StringComparison.OrdinalIgnoreCase))
            {
                return tagStart;
            }

            pos = nameEnd;
        }

        return -1;
    }
}
