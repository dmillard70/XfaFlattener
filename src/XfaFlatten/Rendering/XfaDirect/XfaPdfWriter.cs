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

            // Draw content items for this page
            var pageItems = items.Where(i => i.PageIndex == p).ToList();
            foreach (var item in pageItems)
            {
                DrawItem(gfx, item);
            }
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
                DrawRectangle(gfx, item, filled: false);
                break;

            case LayoutItemType.FilledRectangle:
                DrawRectangle(gfx, item, filled: true);
                break;

            case LayoutItemType.Text:
            default:
                DrawText(gfx, item);
                break;
        }
    }

    private static void DrawText(XGraphics gfx, LayoutItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return;

        var font = CreateFont(item.Font);
        var brush = XBrushes.Black;

        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        // Handle rotation
        if (item.Rotate == 90)
        {
            var state = gfx.Save();
            // Rotate around the top-left corner of the field
            gfx.RotateAtTransform(-90, new XPoint(x, y));
            // After rotation, draw at the adjusted position
            // The rotated text goes downward from the original position
            var rect = new XRect(x, y - w, h, w);
            DrawTextInRect(gfx, item.Text, font, brush, rect, item.Para);
            gfx.Restore(state);
            return;
        }

        var textRect = new XRect(x, y, w, h);
        DrawTextInRect(gfx, item.Text, font, brush, textRect, item.Para);
    }

    private static void DrawTextInRect(XGraphics gfx, string text, XFont font,
        XBrush brush, XRect rect, XfaPara para)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // For multi-line text, use XTextFormatter
        if (text.Contains('\n') || EstimateTextWidth(text, font) > rect.Width * 1.1)
        {
            try
            {
                var tf = new XTextFormatter(gfx);
                tf.Alignment = para.HAlign switch
                {
                    "center" => XParagraphAlignment.Center,
                    "right" => XParagraphAlignment.Right,
                    "justify" => XParagraphAlignment.Justify,
                    _ => XParagraphAlignment.Left
                };

                tf.DrawString(text, font, brush, rect, XStringFormats.TopLeft);
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

    private static void DrawLine(XGraphics gfx, LayoutItem item)
    {
        double x1 = item.X * MmToPt;
        double y1 = item.Y * MmToPt;
        double x2 = (item.X + item.W) * MmToPt;
        double y2 = item.Y * MmToPt; // Horizontal line

        var pen = new XPen(XColors.Black, Math.Max(item.H * MmToPt, 0.5));
        gfx.DrawLine(pen, x1, y1, x2, y2);
    }

    private static void DrawRectangle(XGraphics gfx, LayoutItem item, bool filled)
    {
        double x = item.X * MmToPt;
        double y = item.Y * MmToPt;
        double w = item.W * MmToPt;
        double h = item.H * MmToPt;

        if (w <= 0 || h <= 0) return;

        var rect = new XRect(x, y, w, h);

        if (filled)
        {
            var brush = ParseColorBrush(item.Text) ?? XBrushes.LightGray;
            gfx.DrawRectangle(brush, rect);
        }
        else
        {
            var pen = new XPen(XColors.Black, 0.5);
            gfx.DrawRectangle(pen, rect);
        }
    }

    /// <summary>
    /// Parses an "R,G,B" color string into an XBrush.
    /// </summary>
    private static XSolidBrush? ParseColorBrush(string? colorValue)
    {
        if (string.IsNullOrEmpty(colorValue)) return null;

        var parts = colorValue.Split(',');
        if (parts.Length >= 3 &&
            int.TryParse(parts[0].Trim(), out int r) &&
            int.TryParse(parts[1].Trim(), out int g) &&
            int.TryParse(parts[2].Trim(), out int b))
        {
            return new XSolidBrush(XColor.FromArgb(r, g, b));
        }
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
            var pen = new XPen(XColors.Black, Math.Max(draw.Border.Thickness * MmToPt, 0.5));
            gfx.DrawLine(pen, x, y, x + w, y);
        }
        else if (draw.IsRectangle)
        {
            if (w > 0 && h > 0)
            {
                var pen = new XPen(XColors.Black, Math.Max(draw.Border.Thickness * MmToPt, 0.5));
                var rect = new XRect(x, y, w, h);

                if (draw.Corner is not null && draw.Corner.Radius > 0)
                {
                    double r = draw.Corner.Radius * MmToPt;
                    gfx.DrawRoundedRectangle(pen, rect, new XSize(r, r));
                }
                else
                {
                    gfx.DrawRectangle(pen, rect);
                }
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
        var style = XFontStyleEx.Regular;
        if (xfaFont.Bold && xfaFont.Italic)
            style = XFontStyleEx.BoldItalic;
        else if (xfaFont.Bold)
            style = XFontStyleEx.Bold;
        else if (xfaFont.Italic)
            style = XFontStyleEx.Italic;

        string typeface = MapFontName(xfaFont.Typeface);
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
        // Map common XFA font names to system fonts
        return typeface switch
        {
            "Myriad Pro" => "Arial",
            "Minion Pro" => "Times New Roman",
            "Myriad Pro Black" => "Arial",
            "DTLDocumentaSansT" => "Arial Narrow",
            "DTLDocumentaSansST" => "Arial Narrow",
            "Free 3 of 9 Extended" => "Arial", // Barcode font - fallback
            "Sparkasse Symbol" => "Arial",      // Custom symbol font - fallback
            "Eformso1" => "Arial",              // Custom form font - fallback
            "LBSLogos" => "Arial",              // Custom logo font - fallback
            _ => typeface
        };
    }

    private static double EstimateTextWidth(string text, XFont font)
    {
        // Rough estimate: average char width is ~0.5 * font size
        return text.Length * font.Size * 0.45;
    }
}
