using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using XfaFlatten.Analysis;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Direct XFA rendering engine that parses XFA template and datasets XML,
/// merges them, and renders to PDF using PDFSharp. Produces vector text
/// output without depending on PDFium for content rendering.
/// </summary>
public sealed class XfaDirectEngine : IRenderEngine
{
    /// <inheritdoc />
    public string Name => "XFA Direct (Vector Text)";

    /// <inheritdoc />
    public Task<RenderResult> RenderAsync(string inputPath, int dpi, bool verbose)
    {
        try
        {
            var result = Render(inputPath, verbose);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new RenderResult
            {
                Success = false,
                ErrorMessage = $"XFA Direct rendering failed: {ex.Message}"
            });
        }
    }

    private static RenderResult Render(string inputPath, bool verbose)
    {
        // Ensure font resolver is registered for PDFSharp
        SystemFontResolver.Register();

        // Step 1: Extract XFA XML from the PDF
        if (verbose) Console.WriteLine("[XfaDirect] Extracting XFA streams...");

        byte[] xfaBytes = ExtractXfaBytes(inputPath);
        string xfaXml = Encoding.UTF8.GetString(xfaBytes);

        // Step 2: Extract template and datasets packets
        // Note: ExtractPacket finds the first <template>, which may be the small config
        // template inside <config>. The real XFA template contains the xmlns attribute
        // with "xfa-template". We use a dedicated extractor that finds the correct one.
        string? templateXml = ExtractXfaTemplatePacket(xfaXml);
        string? datasetsXml = XfaDetector.ExtractPacket(xfaXml, "datasets");

        if (templateXml is null)
        {
            return new RenderResult
            {
                Success = false,
                ErrorMessage = "No template packet found in XFA data."
            };
        }

        if (verbose) Console.WriteLine($"[XfaDirect] Template: {templateXml.Length} chars");

        // Step 3: Parse the template
        if (verbose) Console.WriteLine("[XfaDirect] Parsing template...");
        var parser = new XfaTemplateParser();

        // If the template is embedded in a larger XDP document, we may need to wrap it
        XfaTemplateParser.ParseResult parseResult;
        try
        {
            parseResult = parser.Parse(templateXml);
        }
        catch (Exception ex)
        {
            // Try wrapping in an XML declaration if it fails
            try
            {
                parseResult = parser.Parse($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>{templateXml}");
            }
            catch
            {
                return new RenderResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to parse XFA template: {ex.Message}"
                };
            }
        }

        if (verbose)
        {
            Console.WriteLine($"[XfaDirect] Page areas: {parseResult.PageAreas.Count}");
            foreach (var pa in parseResult.PageAreas)
            {
                Console.WriteLine($"  - {pa.Name}: {pa.WidthMm}x{pa.HeightMm}mm, content area: ({pa.ContentArea.X},{pa.ContentArea.Y}) {pa.ContentArea.W}x{pa.ContentArea.H}mm, static elements: {pa.StaticElements.Count}");
            }
            Console.WriteLine($"[XfaDirect] Root subform: {parseResult.RootSubform?.Name ?? "(none)"}, children: {parseResult.RootSubform?.Children.Count ?? 0}");
        }

        // Step 4: Parse the datasets
        if (verbose) Console.WriteLine("[XfaDirect] Parsing datasets...");
        var data = new XfaData();
        if (datasetsXml is not null)
        {
            var dataParser = new XfaDataParser();
            try
            {
                data = dataParser.Parse(datasetsXml);
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"[XfaDirect] Warning: Dataset parsing failed: {ex.Message}");
                // Continue with empty data - static content will still render
            }
        }

        if (verbose)
        {
            Console.WriteLine($"[XfaDirect] Data nodes indexed: {data.NodesByName.Count}");
        }

        // Step 5: Layout with script engine
        if (verbose) Console.WriteLine("[XfaDirect] Computing layout...");

        using var scriptEngine = new XfaScriptEngine(verbose);

        var layoutEngine = new XfaLayoutEngine(parseResult.PageAreas, data, verbose, scriptEngine);

        XfaLayoutEngine.LayoutResult layoutResult;
        if (parseResult.RootSubform is not null)
        {
            layoutResult = layoutEngine.Layout(parseResult.RootSubform);
        }
        else
        {
            layoutResult = new XfaLayoutEngine.LayoutResult(new List<LayoutItem>(), 1);
        }

        if (verbose)
        {
            Console.WriteLine($"[XfaDirect] Layout items: {layoutResult.Items.Count}, pages: {layoutResult.TotalPages}");
            // Count items by type
            int textCount = layoutResult.Items.Count(i => i.ItemType == LayoutItemType.Text && !string.IsNullOrEmpty(i.Text));
            int lineCount = layoutResult.Items.Count(i => i.ItemType == LayoutItemType.Line);
            Console.WriteLine($"[XfaDirect] Text items: {textCount}, lines: {lineCount}");
            // Per-page stats
            for (int pg = 0; pg < layoutResult.TotalPages; pg++)
            {
                var pageItems = layoutResult.Items.Where(i => i.PageIndex == pg).ToList();
                double maxY = pageItems.Count > 0 ? pageItems.Max(i => i.Y + i.H) : 0;
                int pgText = pageItems.Count(i => i.ItemType == LayoutItemType.Text && !string.IsNullOrEmpty(i.Text));
                int pgImg = pageItems.Count(i => i.ItemType == LayoutItemType.Image);
                Console.WriteLine($"  Page {pg}: {pageItems.Count} items ({pgText} text, {pgImg} img), maxY={maxY:F1}mm");
            }
        }

        // Step 6: Render to PDF
        if (verbose) Console.WriteLine("[XfaDirect] Rendering to PDF...");
        byte[] pdfBytes = XfaPdfWriter.Render(
            layoutResult.Items,
            parseResult.PageAreas,
            layoutResult.TotalPages,
            layoutResult.PageAreaMap);

        if (verbose) Console.WriteLine($"[XfaDirect] Output PDF: {pdfBytes.Length} bytes, {layoutResult.TotalPages} pages");

        return new RenderResult
        {
            Success = true,
            PdfBytes = pdfBytes
        };
    }

    /// <summary>
    /// Extracts the real XFA template packet, not the small config template.
    /// The real template has xmlns containing "xfa-template".
    /// </summary>
    private static string? ExtractXfaTemplatePacket(string xfaXml)
    {
        // Find all <template occurrences and pick the one with the XFA template namespace
        const string marker = "<template";
        int searchPos = 0;

        while (searchPos < xfaXml.Length)
        {
            int tagStart = xfaXml.IndexOf(marker, searchPos, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0) break;

            // Check if this is inside a closing tag
            if (tagStart > 0 && xfaXml[tagStart - 1] == '/')
            {
                searchPos = tagStart + marker.Length;
                continue;
            }

            // Find the end of the opening tag
            int tagEnd = xfaXml.IndexOf('>', tagStart);
            if (tagEnd < 0) break;

            string openTag = xfaXml[tagStart..(tagEnd + 1)];

            // Check if this template has an XFA template namespace
            bool isXfaTemplate = openTag.Contains("xfa-template", StringComparison.OrdinalIgnoreCase)
                                 || openTag.Contains("subform", StringComparison.OrdinalIgnoreCase);

            // Also check if the content is substantial (real template > 100 chars)
            if (!isXfaTemplate)
            {
                // Look ahead to see content size
                int closingIdx = FindClosingTag(xfaXml, tagStart, "template");
                if (closingIdx > 0)
                {
                    int contentLen = closingIdx - tagStart;
                    if (contentLen > 200) isXfaTemplate = true;
                }
            }

            if (isXfaTemplate)
            {
                int closingIdx = FindClosingTag(xfaXml, tagStart, "template");
                if (closingIdx > 0)
                {
                    // Find the actual end of the closing tag
                    int closeEnd = xfaXml.IndexOf('>', closingIdx);
                    if (closeEnd > 0)
                        return xfaXml[tagStart..(closeEnd + 1)];
                }
            }

            searchPos = tagEnd + 1;
        }

        // Fallback to standard extraction
        return XfaDetector.ExtractPacket(xfaXml, "template");
    }

    private static int FindClosingTag(string xml, int startFrom, string tagName)
    {
        // Simple nesting-aware closing tag finder
        int depth = 0;
        int pos = startFrom;
        string openPattern = $"<{tagName}";
        string closePattern = $"</{tagName}";

        while (pos < xml.Length)
        {
            int nextOpen = xml.IndexOf(openPattern, pos, StringComparison.OrdinalIgnoreCase);
            int nextClose = xml.IndexOf(closePattern, pos, StringComparison.OrdinalIgnoreCase);

            if (nextClose < 0) return -1; // No closing tag found

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                // Check if it's a self-closing tag
                int openEnd = xml.IndexOf('>', nextOpen);
                if (openEnd > 0 && xml[openEnd - 1] == '/')
                {
                    // Self-closing, don't increment depth
                }
                else
                {
                    depth++;
                }
                pos = (openEnd >= 0 ? openEnd : nextOpen) + 1;
            }
            else
            {
                depth--;
                if (depth <= 0)
                    return nextClose;
                int closeEnd = xml.IndexOf('>', nextClose);
                pos = (closeEnd >= 0 ? closeEnd : nextClose) + 1;
            }
        }

        return -1;
    }

    private static byte[] ExtractXfaBytes(string pdfPath)
    {
        using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
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
