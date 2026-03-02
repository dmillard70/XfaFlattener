using PdfSharp.Drawing;
using PdfSharp.Fonts;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Font resolver that loads fonts from the Windows system fonts directory.
/// Required by PDFSharp 6+ which doesn't have built-in system font access in .NET 6+.
/// </summary>
public sealed class SystemFontResolver : IFontResolver
{
    private static readonly string FontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    // Map font family + style to file name
    private static readonly Dictionary<string, string> FontMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Arial
        { "Arial|Regular", "arial.ttf" },
        { "Arial|Bold", "arialbd.ttf" },
        { "Arial|Italic", "ariali.ttf" },
        { "Arial|BoldItalic", "arialbi.ttf" },
        // Arial Narrow
        { "Arial Narrow|Regular", "arialn.ttf" },
        { "Arial Narrow|Bold", "arialnb.ttf" },
        { "Arial Narrow|Italic", "arialni.ttf" },
        { "Arial Narrow|BoldItalic", "arialnbi.ttf" },
        // Times New Roman
        { "Times New Roman|Regular", "times.ttf" },
        { "Times New Roman|Bold", "timesbd.ttf" },
        { "Times New Roman|Italic", "timesi.ttf" },
        { "Times New Roman|BoldItalic", "timesbi.ttf" },
        // Courier New
        { "Courier New|Regular", "cour.ttf" },
        { "Courier New|Bold", "courbd.ttf" },
        { "Courier New|Italic", "couri.ttf" },
        { "Courier New|BoldItalic", "courbi.ttf" },
        // Verdana
        { "Verdana|Regular", "verdana.ttf" },
        { "Verdana|Bold", "verdanab.ttf" },
        { "Verdana|Italic", "verdanai.ttf" },
        { "Verdana|BoldItalic", "verdanaz.ttf" },
        // Calibri
        { "Calibri|Regular", "calibri.ttf" },
        { "Calibri|Bold", "calibrib.ttf" },
        { "Calibri|Italic", "calibrii.ttf" },
        { "Calibri|BoldItalic", "calibriz.ttf" },
    };

    /// <summary>
    /// Ensures the font resolver is registered with PDFSharp's global settings.
    /// Safe to call multiple times.
    /// </summary>
    public static void Register()
    {
        if (GlobalFontSettings.FontResolver is not SystemFontResolver)
            GlobalFontSettings.FontResolver = new SystemFontResolver();
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string style = (isBold, isItalic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular"
        };

        string key = $"{familyName}|{style}";

        // Direct match
        if (FontMap.ContainsKey(key))
            return new FontResolverInfo(key);

        // Try without style
        string basicKey = $"{familyName}|Regular";
        if (FontMap.ContainsKey(basicKey))
            return new FontResolverInfo(basicKey, isBold, isItalic);

        // Fallback to Arial
        string arialKey = $"Arial|{style}";
        if (FontMap.ContainsKey(arialKey))
            return new FontResolverInfo(arialKey);

        return new FontResolverInfo("Arial|Regular", isBold, isItalic);
    }

    public byte[]? GetFont(string faceName)
    {
        if (FontMap.TryGetValue(faceName, out string? fileName))
        {
            string path = Path.Combine(FontDir, fileName);
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        // Try direct file name
        string directPath = Path.Combine(FontDir, faceName);
        if (File.Exists(directPath))
            return File.ReadAllBytes(directPath);

        // Ultimate fallback - return Arial Regular
        string arialPath = Path.Combine(FontDir, "arial.ttf");
        if (File.Exists(arialPath))
            return File.ReadAllBytes(arialPath);

        return null;
    }
}
