namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Page definition parsed from the XFA template's pageArea elements.
/// </summary>
/// <param name="Name">Page area name (e.g., "DS_V1_SET0").</param>
/// <param name="WidthMm">Page width in millimeters.</param>
/// <param name="HeightMm">Page height in millimeters.</param>
/// <param name="ContentArea">The content area where flowing content is placed.</param>
/// <param name="StaticElements">Positioned elements on the page (headers, footers, lines, etc.).</param>
public record XfaPageArea(
    string Name,
    double WidthMm,
    double HeightMm,
    XfaContentArea ContentArea,
    List<XfaElement> StaticElements);

/// <summary>
/// Defines the rectangular area within a page where flowing content is placed.
/// </summary>
public record XfaContentArea(double X, double Y, double W, double H);

/// <summary>
/// Base class for all template elements (fields, draws, subforms).
/// </summary>
public abstract record XfaElement(
    string? Name,
    double? X,
    double? Y,
    double? W,
    double? H,
    double? MinH,
    string? Presence);

/// <summary>
/// A data-bound field or static text field from the template.
/// </summary>
public record XfaFieldDef(
    string? Name,
    double? X,
    double? Y,
    double? W,
    double? H,
    double? MinH,
    string? Presence,
    XfaFont Font,
    XfaPara Para,
    XfaMargin Margin,
    string? BindRef,
    string? BindMatch,
    string? StaticValue,
    bool IsRichText,
    bool IsMultiLine,
    double? CaptionReserve,
    string? CaptionText,
    XfaFont? CaptionFont,
    XfaPara? CaptionPara,
    double Rotate,
    XfaBorder Border = default!,
    string? CaptionBindRef = null,
    bool HideIfEmpty = false,
    List<XfaScript>? Scripts = null) : XfaElement(Name, X, Y, W, H, MinH, Presence);

/// <summary>
/// A static draw element (text, line, rectangle) from the template.
/// </summary>
public record XfaDrawDef(
    string? Name,
    double? X,
    double? Y,
    double? W,
    double? H,
    double? MinH,
    string? Presence,
    XfaFont Font,
    XfaPara Para,
    XfaMargin Margin,
    string? TextValue,
    bool IsRichText,
    bool IsLine,
    bool IsRectangle,
    XfaBorder Border,
    XfaCorner? Corner) : XfaElement(Name, X, Y, W, H, MinH, Presence);

/// <summary>
/// A subform container that holds child elements with a specific layout.
/// </summary>
public record XfaSubformDef(
    string? Name,
    double? X,
    double? Y,
    double? W,
    double? H,
    double? MinH,
    string? Presence,
    string Layout,
    List<XfaElement> Children,
    int OccurMin,
    int OccurMax,
    string? BindRef,
    string? BindMatch,
    XfaMargin Margin,
    XfaBorder Border,
    string? ColumnWidths,
    bool HasBreakBefore,
    string? BreakTarget,
    string? BreakTargetType = null,
    List<XfaScript>? Scripts = null,
    List<XfaNamedScript>? NamedScripts = null) : XfaElement(Name, X, Y, W, H, MinH, Presence);

/// <summary>
/// A subformSet that controls which child subform is chosen (choice/relation).
/// </summary>
public record XfaSubformSetDef(
    string? Name,
    string Relation,
    List<XfaElement> Children) : XfaElement(Name, null, null, null, null, null, null);

/// <summary>
/// Font specification for text rendering.
/// </summary>
/// <param name="Typeface">Font family name (e.g., "Arial").</param>
/// <param name="SizePt">Font size in points. Defaults to 10pt if not specified.</param>
/// <param name="Bold">Whether the font is bold.</param>
/// <param name="Italic">Whether the font is italic.</param>
/// <param name="Underline">Whether the text is underlined.</param>
public record XfaFont(
    string Typeface = "Arial",
    double SizePt = 10,
    bool Bold = false,
    bool Italic = false,
    bool Underline = false);

/// <summary>
/// Paragraph alignment settings.
/// </summary>
public record XfaPara(
    string HAlign = "left",
    string VAlign = "top",
    double? LineHeight = null,
    double? SpaceBefore = null,
    double? SpaceAfter = null);

/// <summary>
/// Inset margins around an element.
/// </summary>
public record XfaMargin(
    double Top = 0,
    double Right = 0,
    double Bottom = 0,
    double Left = 0);

/// <summary>
/// Border definition for elements.
/// </summary>
public record XfaBorder(
    bool Visible = false,
    double Thickness = 0.2,
    string? FillColor = null,
    string? StrokeColor = null);

/// <summary>
/// Corner definition for rectangles.
/// </summary>
public record XfaCorner(double Radius = 0);

// ===================== Script Models =====================

/// <summary>
/// A JavaScript/FormCalc script attached to a template element via an event or calculate block.
/// </summary>
/// <param name="Source">The script source code.</param>
/// <param name="Event">The event trigger: "calculate", "initialize", "ready", "enter", "exit", etc.</param>
/// <param name="Activity">The activity attribute from the event element (e.g., "ready", "initialize").</param>
/// <param name="RunAt">Where the script runs: "client" (default) or "server".</param>
public record XfaScript(string Source, string Event, string Activity = "", string RunAt = "client");

/// <summary>
/// A named script from a subform's &lt;variables&gt; block, acting as a library/function set.
/// </summary>
/// <param name="Name">The name attribute (e.g., "oDynamicTable", "dsv_common").</param>
/// <param name="Source">The JavaScript source code.</param>
public record XfaNamedScript(string Name, string Source);

// ===================== Layout Output Models =====================

/// <summary>
/// A positioned item ready for rendering on a specific page.
/// </summary>
public record LayoutItem(
    int PageIndex,
    double X,
    double Y,
    double W,
    double H,
    string Text,
    XfaFont Font,
    XfaPara Para,
    LayoutItemType ItemType = LayoutItemType.Text,
    double Rotate = 0,
    string? StrokeColor = null,
    double StrokeThicknessPt = 0,
    string? FillColor = null);

/// <summary>
/// Types of layout items for rendering.
/// </summary>
public enum LayoutItemType
{
    Text,
    Line,
    Rectangle,
    FilledRectangle
}

/// <summary>
/// A segment of rich text with uniform formatting (bold/italic/underline/font size).
/// </summary>
/// <param name="Text">The text content of this segment.</param>
/// <param name="Bold">Whether the text is bold.</param>
/// <param name="Italic">Whether the text is italic.</param>
/// <param name="Underline">Whether the text is underlined.</param>
/// <param name="FontSizePt">Font size in points, or null to use the field's default.</param>
/// <param name="FontFamily">Font family name from CSS, or null to use the field's default.</param>
public record RichTextSegment(string Text, bool Bold, bool Italic, bool Underline, double? FontSizePt = null, string? FontFamily = null);

// ===================== Data Models =====================

/// <summary>
/// Hierarchical representation of XFA datasets.
/// </summary>
public sealed class XfaData
{
    /// <summary>Root data node.</summary>
    public XfaDataNode Root { get; set; } = new("root");

    /// <summary>Index of all nodes by name for fast lookup.</summary>
    public Dictionary<string, List<XfaDataNode>> NodesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A single node in the XFA data tree.
/// </summary>
public sealed class XfaDataNode
{
    public XfaDataNode(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public string? TextValue { get; set; }
    public string? RichTextHtml { get; set; }
    public List<XfaDataNode> Children { get; } = new();
    public XfaDataNode? Parent { get; set; }

    /// <summary>
    /// Finds direct children with the given name.
    /// </summary>
    public List<XfaDataNode> GetChildren(string name)
    {
        var result = new List<XfaDataNode>();
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                result.Add(child);
        }
        return result;
    }

    /// <summary>
    /// Navigates a dot-separated path (e.g., "PRINTJOB.PMSDATA.fieldName").
    /// </summary>
    public XfaDataNode? Navigate(string path)
    {
        var parts = path.Split('.');
        XfaDataNode? current = this;
        foreach (var part in parts)
        {
            if (current is null) return null;
            // Handle array index notation like "block[*]" or "zeile[0]"
            string cleanName = part;
            int bracketIdx = part.IndexOf('[');
            if (bracketIdx >= 0)
                cleanName = part[..bracketIdx];

            var children = current.GetChildren(cleanName);
            current = children.Count > 0 ? children[0] : null;
        }
        return current;
    }
}
