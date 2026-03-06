using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Renders laid-out items to a PDF document using PDFSharp.
/// Produces vector text output (not bitmaps).
/// </summary>
public static class XfaPdfWriter
{
    // Conversion: 1mm = 2.8346pt (PDF points)
    private const double MmToPt = 72.0 / 25.4;

    // Reference-matching default colors (dark gray instead of pure black)
    private static readonly XColor DefaultStrokeColor = XColor.FromArgb(34, 31, 31);
    private static readonly XColor DefaultTextColor = XColor.FromArgb(34, 31, 31);

    /// <summary>
    /// Renders all layout items into a PDF byte array.
    /// </summary>
    public static byte[] Render(List<LayoutItem> items, List<XfaPageArea> pageAreas,
        int totalPages, List<int>? pageAreaMap = null)
    {
        using var doc = new PdfDocument();

        for (int p = 0; p < totalPages; p++)
        {
            var pageArea = SelectPageArea(pageAreas, p, totalPages, pageAreaMap);
            var page = doc.AddPage();
            page.Width = XUnit.FromMillimeter(pageArea.WidthMm);
            page.Height = XUnit.FromMillimeter(pageArea.HeightMm);

            using var gfx = XGraphics.FromPdfPage(page);

            // Draw static elements from the page area template
            DrawStaticElements(gfx, pageArea);

            // Draw content items for this page in z-order:
            // 1. Filled rectangles (backgrounds) first
            // 2. Text content second
            // 3. Stroked rectangles and lines last (borders on top)
            var pageItems = items.Where(i => i.PageIndex == p).ToList();

            foreach (var item in pageItems.Where(i => i.ItemType == LayoutItemType.FilledRectangle))
                DrawItem(gfx, item);

            foreach (var item in pageItems.Where(i => i.ItemType == LayoutItemType.Text))
                DrawItem(gfx, item);

            foreach (var item in pageItems.Where(i => i.ItemType is LayoutItemType.Rectangle or LayoutItemType.Line))
                DrawItem(gfx, item);
        }

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void DrawItem(XGraphics gfx, LayoutItem item)
    {
        switch (item.ItemType)
        {
            case LayoutItemType.Line:
                DrawLine(gfx, item);
                break;

            case LayoutItemType.Rectangle:
                DrawRectangle(gfx, item);
                break;

            case LayoutItemType.FilledRectangle:
                DrawFilledRectangle(gfx, item);
                break;

            case LayoutItemType.Text:
            default:
                DrawText(gfx, item);
                break;
        }
    }

    private static void DrawText(XGraphics gfx, LayoutItem item)
    {
        // Use inline-run rendering when the item has multiple TextRuns with individual fonts
        if (item.Runs is { Count: > 0 })
        {
            DrawInlineRuns(gfx, item, new XSolidBrush(DefaultTextColor));
            return;
        }

        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var font = CreateFont(item.Font);
        var brush = new XSolidBrush(DefaultTextColor);

        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        // Handle 90° rotation: text flows bottom-to-top in the original coordinate system.
        // We rotate -90° around (x, y) which is the bottom-left of the text strip.
        // In the rotated coordinate system, draw text at (x, y) flowing right for W pts
        // with line height H pts. After rotation, "right" becomes "up" and "down" becomes "right".
        if (item.Rotate == 90)
        {
            var state = gfx.Save();
            gfx.RotateAtTransform(-90, new XPoint(x, y));
            var rect = new XRect(x, y, w, h);
            DrawSingleLineText(gfx, item.Text, font, brush, rect, item.Para);
            gfx.Restore(state);
            return;
        }

        var textRect = new XRect(x, y, w, h);

        // Clip text rendering to the LayoutItem boundary so text never extends
        // beyond the field, preventing visual overlaps with adjacent items.
        var clipState = gfx.Save();
        gfx.IntersectClip(textRect);
        DrawTextInRect(gfx, item.Text, font, brush, textRect, item.Para);

        // Draw underline after text (on top), still inside clip
        if (item.Font.Underline)
            DrawUnderline(gfx, item.Text, font, textRect, item.Para);
        gfx.Restore(clipState);
    }

    private static void DrawTextInRect(XGraphics gfx, string text, XFont font,
        XBrush brush, XRect rect, XfaPara para)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // For multi-line text, use XTextFormatter
        double measuredWidth = gfx.MeasureString(text, font).Width;
        if (text.Contains('\n') || measuredWidth > rect.Width)
        {
            try
            {
                // Use the specified rect height. XTextFormatter will clip text that
                // doesn't fit, which is correct for overflow/clamped fields.
                var drawRect = rect;

                var tf = new XTextFormatter(gfx);
                tf.Alignment = para.HAlign switch
                {
                    "center" => XParagraphAlignment.Center,
                    "right" => XParagraphAlignment.Right,
                    "justify" => XParagraphAlignment.Justify,
                    _ => XParagraphAlignment.Left
                };

                tf.DrawString(text, font, brush, drawRect, XStringFormats.TopLeft);
            }
            catch
            {
                // Fallback to single-line if XTextFormatter fails
                DrawSingleLineText(gfx, text, font, brush, rect, para);
            }
        }
        else
        {
            DrawSingleLineText(gfx, text, font, brush, rect, para);
        }
    }

    private static void DrawSingleLineText(XGraphics gfx, string text, XFont font,
        XBrush brush, XRect rect, XfaPara para)
    {
        var format = new XStringFormat();

        format.Alignment = para.HAlign switch
        {
            "center" => XStringAlignment.Center,
            "right" => XStringAlignment.Far,
            _ => XStringAlignment.Near
        };

        format.LineAlignment = para.VAlign switch
        {
            "middle" => XLineAlignment.Center,
            "bottom" => XLineAlignment.Far,
            _ => XLineAlignment.Near
        };

        gfx.DrawString(text, font, brush, rect, format);
    }

    private static void DrawUnderline(XGraphics gfx, string text, XFont font,
        XRect rect, XfaPara para)
    {
        double textWidth = gfx.MeasureString(text, font).Width;
        double lineThickness = Math.Max(0.5, font.Size * 0.05);

        // Position underline just below the text baseline.
        // Baseline is approximately at ascent from top; underline sits slightly below it.
        double underlineY = rect.Y + font.Size * 1.1;

        // Adjust start X based on horizontal alignment
        double startX = para.HAlign switch
        {
            "center" => rect.X + (rect.Width - textWidth) / 2,
            "right" => rect.X + rect.Width - textWidth,
            _ => rect.X
        };

        var pen = new XPen(DefaultTextColor, lineThickness);
        gfx.DrawLine(pen, startX, underlineY, startX + textWidth, underlineY);
    }

    /// <summary>
    /// Renders inline text runs with mixed fonts/styles on the same line.
    /// Each run is drawn sequentially, advancing X. Words wrap to the next line when
    /// they exceed the available width. Line height is the max font height on that line.
    /// </summary>
    private static void DrawInlineRuns(XGraphics gfx, LayoutItem item, XSolidBrush brush)
    {
        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        // CSS paragraph indentation:
        // margin-left: offset for ALL lines from the left edge
        // text-indent: additional offset for the FIRST line only (can be negative for hanging indent)
        double marginLeftPt = item.MarginLeftMm * MmToPt;
        double textIndentPt = item.TextIndentMm * MmToPt;
        double leftEdge = x + marginLeftPt;         // left edge for subsequent lines
        double firstLineLeft = leftEdge + textIndentPt; // left edge for first line
        double rightEdge = x + w;
        double availWidth = rightEdge - leftEdge;

        // Clip inline-run rendering to the LayoutItem boundary so text never
        // extends beyond the field, preventing visual overlaps with adjacent items.
        var clipState = gfx.Save();
        gfx.IntersectClip(new XRect(x, y, w, h));

        var topLeft = new XStringFormat
        {
            Alignment = XStringAlignment.Near,
            LineAlignment = XLineAlignment.Near
        };

        // Pass 1: collect words into logical lines for alignment measurement
        var wordEntries = new List<(string word, XFont font, XfaFont xfaFont, double wordW, double spaceW, double lineH)>();
        bool needsSpace = false;

        foreach (var run in item.Runs!)
        {
            string text = run.Text?.Replace('\t', ' ') ?? "";
            if (text.Length == 0) continue;

            var font = CreateFont(run.Font);
            double lineH = font.Height * 1.15;
            double spaceW = gfx.MeasureString(" ", font).Width;

            if (text[0] == ' ')
                needsSpace = true;

            string trimmed = text.Trim();
            if (trimmed.Length == 0) { needsSpace = true; continue; }

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                double wordW = gfx.MeasureString(word, font).Width;
                wordEntries.Add((word, font, run.Font, wordW, needsSpace ? spaceW : 0, lineH));
                needsSpace = true;
            }

            if (text[^1] == ' ')
                needsSpace = true;
        }

        // Pass 2: break words into lines
        string hAlign = item.Para.HAlign ?? "left";
        bool needsAlign = hAlign is "right" or "center";

        var lines = new List<(int start, int count, double width, double height)>();
        double curLineW = 0;
        double curLineH = 0;
        int lineStart = 0;
        bool isFirstLine = true;

        for (int i = 0; i < wordEntries.Count; i++)
        {
            var (_, _, _, wordW, spW, lineH) = wordEntries[i];
            double totalW = wordW + spW;
            double lineEdge = isFirstLine ? (rightEdge - firstLineLeft) : availWidth;

            if (curLineW + totalW > lineEdge + 0.5 && curLineW > 0.5)
            {
                lines.Add((lineStart, i - lineStart, curLineW, curLineH));
                lineStart = i;
                curLineW = wordW; // no leading space on new line
                curLineH = lineH;
                isFirstLine = false;
                // Remove leading space for this word since it starts a new line
                wordEntries[i] = (wordEntries[i].word, wordEntries[i].font, wordEntries[i].xfaFont,
                    wordW, 0, lineH);
            }
            else
            {
                curLineW += totalW;
                curLineH = Math.Max(curLineH, lineH);
            }
        }
        if (lineStart < wordEntries.Count)
            lines.Add((lineStart, wordEntries.Count - lineStart, curLineW, curLineH));

        // Pass 3: draw lines with alignment
        double curY = y;
        isFirstLine = true;
        foreach (var (start, count, lineWidth, lineHeight) in lines)
        {
            double lineLeft = isFirstLine ? firstLineLeft : leftEdge;
            double curX = hAlign switch
            {
                "right" => rightEdge - lineWidth,
                "center" => lineLeft + ((isFirstLine ? rightEdge - firstLineLeft : availWidth) - lineWidth) / 2,
                _ => lineLeft
            };

            for (int i = start; i < start + count; i++)
            {
                var (word, font, xfaFont, wordW, spW, _) = wordEntries[i];
                curX += spW;

                gfx.DrawString(word, font, brush, new XPoint(curX, curY), topLeft);

                if (xfaFont.Underline)
                {
                    double ulY = curY + font.Size * 1.1;
                    double ulThick = Math.Max(0.5, font.Size * 0.05);
                    gfx.DrawLine(new XPen(DefaultTextColor, ulThick),
                        curX, ulY, curX + wordW, ulY);
                }

                curX += wordW;
            }

            curY += lineHeight > 0 ? lineHeight : 10;
            isFirstLine = false;
        }

        gfx.Restore(clipState);
    }

    private static void DrawLine(XGraphics gfx, LayoutItem item)
    {
        double x1 = item.X * MmToPt;
        double y1 = item.Y * MmToPt;
        // Determine direction: if W >= H, horizontal line; otherwise vertical line
        double x2, y2;
        if (item.W >= item.H)
        {
            x2 = (item.X + item.W) * MmToPt;
            y2 = y1; // Horizontal
        }
        else
        {
            x2 = x1; // Vertical
            y2 = (item.Y + item.H) * MmToPt;
        }

        var color = ParseColor(item.StrokeColor) ?? DefaultStrokeColor;
        double thickness = item.StrokeThicknessPt > 0 ? item.StrokeThicknessPt : 0.5;
        var pen = new XPen(color, thickness);
        gfx.DrawLine(pen, x1, y1, x2, y2);
    }

    private static void DrawRectangle(XGraphics gfx, LayoutItem item)
    {
        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        var rect = new XRect(x, y, w, h);
        var color = ParseColor(item.StrokeColor) ?? DefaultStrokeColor;
        double thickness = item.StrokeThicknessPt > 0 ? item.StrokeThicknessPt : 0.5;
        var pen = new XPen(color, thickness);
        gfx.DrawRectangle(pen, rect);
    }

    private static void DrawFilledRectangle(XGraphics gfx, LayoutItem item)
    {
        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        var rect = new XRect(x, y, w, h);

        // Use FillColor from the new field, fall back to Text for backward compatibility
        var brush = ParseColorBrush(item.FillColor)
                    ?? ParseColorBrush(item.Text)
                    ?? new XSolidBrush(XColor.FromArgb(215, 218, 219));
        gfx.DrawRectangle(brush, rect);
    }

    /// <summary>
    /// Parses an "R,G,B" color string into an XColor.
    /// </summary>
    private static XColor? ParseColor(string? colorValue)
    {
        if (string.IsNullOrEmpty(colorValue)) return null;

        var parts = colorValue.Split(',');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0].Trim(), out int r) &&
            int.TryParse(parts[1].Trim(), out int g) &&
            int.TryParse(parts[2].Trim(), out int b))
        {
            return XColor.FromArgb(r, g, b);
        }
        return null;
    }

    /// <summary>
    /// Parses an "R,G,B" color string into an XBrush.
    /// </summary>
    private static XSolidBrush? ParseColorBrush(string? colorValue)
    {
        var color = ParseColor(colorValue);
        if (color.HasValue)
            return new XSolidBrush(color.Value);
        return null;
    }

    private static void DrawStaticElements(XGraphics gfx, XfaPageArea pageArea)
    {
        foreach (var element in pageArea.StaticElements)
        {
            if (element is XfaDrawDef draw)
            {
                DrawStaticDraw(gfx, draw);
            }
            // Static fields and subforms on page areas are handled by the layout engine's InjectPageNumbers
        }
    }

    private static void DrawStaticDraw(XGraphics gfx, XfaDrawDef draw)
    {
        if (draw.Presence is "hidden" or "inactive")
            return;

        double x = (draw.X ?? 0) * MmToPt;
        double y = (draw.Y ?? 0) * MmToPt;
        double w = (draw.W ?? 0) * MmToPt;
        double h = (draw.H ?? 0) * MmToPt;

        if (draw.IsLine)
        {
            double thickness = draw.Border.Thickness * MmToPt;
            if (thickness < 0.3) thickness = 0.5;
            var color = ParseColor(draw.Border.StrokeColor) ?? DefaultStrokeColor;
            var pen = new XPen(color, thickness);
            gfx.DrawLine(pen, x, y, x + w, y);
        }
        else if (draw.IsRectangle)
        {
            if (w > 0 && h > 0)
            {
                double thickness = draw.Border.Thickness * MmToPt;
                if (thickness < 0.3) thickness = 0.5;
                var color = ParseColor(draw.Border.StrokeColor) ?? DefaultStrokeColor;
                var pen = new XPen(color, thickness);
                var rect = new XRect(x, y, w, h);

                // Always use sharp corners — rounded corners from the template are
                // not rendered in the reference Adobe output for this form.
                gfx.DrawRectangle(pen, rect);
            }
        }
    }

    // ===================== Helpers =====================

    private static XfaPageArea SelectPageArea(List<XfaPageArea> pageAreas, int pageIndex,
        int totalPages, List<int>? pageAreaMap)
    {
        if (pageAreas.Count == 0)
            return new XfaPageArea("Default", 210, 297,
                new XfaContentArea(20, 20, 170, 257), new List<XfaElement>());

        // Use the page area map if available (layout engine computed this)
        if (pageAreaMap is not null && pageIndex < pageAreaMap.Count)
        {
            int idx = pageAreaMap[pageIndex];
            if (idx >= 0 && idx < pageAreas.Count)
                return pageAreas[idx];
        }

        // Fallback: first page uses first area, subsequent pages use second area
        if (pageIndex == 0)
            return pageAreas[0];

        return pageAreas.Count > 1 ? pageAreas[1] : pageAreas[0];
    }

    private static XFont CreateFont(XfaFont xfaFont)
    {
        bool bold = xfaFont.Bold;
        bool italic = xfaFont.Italic;

        // Detect bold/italic from font name (e.g., "SparkasseRg-Bold", "Arial-BoldMT")
        string rawTypeface = xfaFont.Typeface;
        if (!bold && (rawTypeface.Contains("-Bold", StringComparison.OrdinalIgnoreCase)
                      || rawTypeface.EndsWith("Bold", StringComparison.OrdinalIgnoreCase)
                      || rawTypeface.Contains("BoldMT", StringComparison.OrdinalIgnoreCase)))
            bold = true;
        if (!italic && (rawTypeface.Contains("-Italic", StringComparison.OrdinalIgnoreCase)
                        || rawTypeface.Contains("Oblique", StringComparison.OrdinalIgnoreCase)))
            italic = true;

        var style = XFontStyleEx.Regular;
        if (bold && italic)
            style = XFontStyleEx.BoldItalic;
        else if (bold)
            style = XFontStyleEx.Bold;
        else if (italic)
            style = XFontStyleEx.Italic;

        string typeface = MapFontName(rawTypeface);
        double size = Math.Max(xfaFont.SizePt, 1);

        try
        {
            return new XFont(typeface, size, style);
        }
        catch
        {
            // Fallback to Arial if the requested font is unavailable
            return new XFont("Arial", size, style);
        }
    }

    private static string MapFontName(string typeface)
    {
        // Map common XFA font names to available fonts
        return typeface switch
        {
            "Myriad Pro" => "Arial",
            "Minion Pro" => "Times New Roman",
            "Myriad Pro Black" => "Arial",
            "DTLDocumentaSansT" => "Arial Narrow",
            "DTLDocumentaSansST" => "Arial Narrow",
            "Free 3 of 9 Extended" => "Arial",       // Barcode font - fallback
            "Sparkasse Symbol" => "Arial",            // Custom symbol font - fallback
            "SparkasseRg-Regular" => "SparkasseRg",   // Use extracted SparkasseRg font
            "SparkasseRg-Bold" => "SparkasseRg",      // Use extracted SparkasseRg font (bold via style)
            "SparkasseRg" => "SparkasseRg",           // Pass through
            "Sparkasse Rg" => "SparkasseRg",          // Space variant → SparkasseRg
            "Eformso1" => "Arial",                    // Custom form font - fallback
            "LBSLogos" => "Arial",                    // Custom logo font - fallback
            "ArialMT" => "Arial",                     // PostScript name → system name
            "Arial-BoldMT" => "Arial",                // PostScript Bold → Arial (bold via style)
            "Arial-ItalicMT" => "Arial",              // PostScript Italic → Arial (italic via style)
            "Arial-BoldItalicMT" => "Arial",          // PostScript BoldItalic → Arial
            _ => typeface
        };
    }

    private static double EstimateTextWidth(string text, XFont font)
    {
        // Rough estimate: average char width is ~0.5 * font size
        return text.Length * font.Size * 0.45;
    }
}
