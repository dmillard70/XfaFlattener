using System.Globalization;
using System.Xml;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Parses the XFA template XML packet into structured page areas and a root subform tree.
/// </summary>
public sealed class XfaTemplateParser
{
    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null
    };

    /// <summary>
    /// Parse result containing page areas and the root content subform.
    /// </summary>
    public record ParseResult(List<XfaPageArea> PageAreas, XfaSubformDef? RootSubform);

    /// <summary>
    /// Parses the template XML string into page areas and content subforms.
    /// </summary>
    public ParseResult Parse(string templateXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(templateXml);

        var templateNode = FindTemplateRoot(doc);
        if (templateNode is null)
            return new ParseResult(new List<XfaPageArea>(), null);

        var pageAreas = new List<XfaPageArea>();
        XfaSubformDef? rootSubform = null;

        // The template has a root subform (e.g., name="OSPDMS") containing:
        // 1. A pageSet with pageArea definitions
        // 2. Content subforms (the flowing content)
        var rootSubformNode = FindChild(templateNode, "subform")
                              ?? templateNode;

        // Parse pageSet - search recursively for pageAreas since pageSets can be nested
        var pageSetNode = FindDescendant(rootSubformNode, "pageSet");
        if (pageSetNode is not null)
        {
            CollectPageAreas(pageSetNode, pageAreas);
        }

        // Parse content subforms (everything after pageSet at the root subform level)
        var contentChildren = new List<XfaElement>();
        foreach (XmlNode child in rootSubformNode.ChildNodes)
        {
            if (child.LocalName == "pageSet") continue;
            if (child.LocalName == "bind") continue;
            if (child.NodeType != XmlNodeType.Element) continue;
            if (child.LocalName is "variables" or "proto" or "event" or "calculate") continue;

            var element = ParseElement(child);
            if (element is not null)
                contentChildren.Add(element);
        }

        // Parse named scripts from <variables> block
        var namedScripts = ParseVariables(rootSubformNode);

        // Parse scripts from root subform events
        var rootScripts = ParseScripts(rootSubformNode);

        rootSubform = new XfaSubformDef(
            Name: GetAttr(rootSubformNode, "name") ?? "root",
            X: null, Y: null,
            W: ParseMeasurement(GetAttr(rootSubformNode, "w")),
            H: null, MinH: null, Presence: null,
            Layout: GetAttr(rootSubformNode, "layout") ?? "tb",
            Children: contentChildren,
            OccurMin: 1, OccurMax: 1,
            BindRef: ParseBindRef(rootSubformNode),
            BindMatch: ParseBindMatch(rootSubformNode),
            Margin: ParseMarginElement(rootSubformNode),
            Border: ParseBorderElement(rootSubformNode),
            ColumnWidths: null,
            HasBreakBefore: false,
            BreakTarget: null,
            Scripts: rootScripts.Count > 0 ? rootScripts : null,
            NamedScripts: namedScripts.Count > 0 ? namedScripts : null);

        return new ParseResult(pageAreas, rootSubform);
    }

    /// <summary>
    /// Recursively collects all pageArea elements from nested pageSet structures.
    /// </summary>
    private void CollectPageAreas(XmlNode pageSetNode, List<XfaPageArea> pageAreas)
    {
        foreach (XmlNode child in pageSetNode.ChildNodes)
        {
            if (child.LocalName == "pageArea")
            {
                var pageArea = ParsePageArea(child);
                if (pageArea is not null)
                    pageAreas.Add(pageArea);
            }
            else if (child.LocalName == "pageSet")
            {
                // Recurse into nested pageSets
                CollectPageAreas(child, pageAreas);
            }
        }
    }

    private XfaPageArea? ParsePageArea(XmlNode node)
    {
        string name = GetAttr(node, "name") ?? "Page";
        double widthMm = 210, heightMm = 297; // A4 default

        // Parse medium element for page size
        var mediumNode = FindChild(node, "medium");
        if (mediumNode is not null)
        {
            double shortDim = ParseMeasurement(GetAttr(mediumNode, "short")) ?? 210;
            double longDim = ParseMeasurement(GetAttr(mediumNode, "long")) ?? 297;
            string? orientation = GetAttr(mediumNode, "orientation");

            if (orientation == "landscape")
            {
                widthMm = longDim;   // Width = longer dimension
                heightMm = shortDim; // Height = shorter dimension
            }
            else
            {
                widthMm = shortDim;
                heightMm = longDim;
            }
        }

        // Parse content area
        var contentAreaNode = FindChild(node, "contentArea");
        XfaContentArea contentArea;
        if (contentAreaNode is not null)
        {
            contentArea = new XfaContentArea(
                X: ParseMeasurement(GetAttr(contentAreaNode, "x")) ?? 0,
                Y: ParseMeasurement(GetAttr(contentAreaNode, "y")) ?? 0,
                W: ParseMeasurement(GetAttr(contentAreaNode, "w")) ?? widthMm,
                H: ParseMeasurement(GetAttr(contentAreaNode, "h")) ?? heightMm);
        }
        else
        {
            contentArea = new XfaContentArea(0, 0, widthMm, heightMm);
        }

        // Parse static elements (fields, draws, subforms positioned on the page)
        var staticElements = new List<XfaElement>();
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.LocalName is "medium" or "contentArea" or "occur") continue;
            if (child.NodeType != XmlNodeType.Element) continue;

            var element = ParseElement(child);
            if (element is not null)
                staticElements.Add(element);
        }

        return new XfaPageArea(name, widthMm, heightMm, contentArea, staticElements);
    }

    internal XfaElement? ParseElement(XmlNode node)
    {
        return node.LocalName switch
        {
            "field" => ParseField(node),
            "draw" => ParseDraw(node),
            "subform" => ParseSubform(node),
            "subformSet" => ParseSubformSet(node),
            _ => null
        };
    }

    private XfaFieldDef ParseField(XmlNode node)
    {
        var font = ParseFontElement(node);
        var para = ParseParaElement(node);
        var margin = ParseMarginElement(node);
        string? bindRef = ParseBindRef(node);
        string? bindMatch = ParseBindMatch(node);

        // Static value from <value><text>...</text></value>
        string? staticValue = null;
        bool isRichText = false;
        var valueNode = FindChild(node, "value");
        if (valueNode is not null)
        {
            var textNode = FindChild(valueNode, "text");
            if (textNode is not null)
                staticValue = textNode.InnerText;

            var exDataNode = FindChild(valueNode, "exData");
            if (exDataNode is not null)
            {
                string? contentType = GetAttr(exDataNode, "contentType");
                if (contentType == "text/html")
                {
                    isRichText = true;
                    staticValue = exDataNode.InnerXml;
                }
            }
        }

        // Check for multiline
        bool isMultiLine = false;
        var uiNode = FindChild(node, "ui");
        if (uiNode is not null)
        {
            var textEditNode = FindChild(uiNode, "textEdit");
            if (textEditNode is not null)
                isMultiLine = GetAttr(textEditNode, "multiLine") == "1";
        }

        // Caption
        double? captionReserve = null;
        string? captionText = null;
        XfaFont? captionFont = null;
        XfaPara? captionPara = null;
        var captionNode = FindChild(node, "caption");
        if (captionNode is not null)
        {
            captionReserve = ParseMeasurement(GetAttr(captionNode, "reserve"));
            var captionValueNode = FindChild(captionNode, "value");
            if (captionValueNode is not null)
            {
                var captTextNode = FindChild(captionValueNode, "text");
                if (captTextNode is not null)
                    captionText = captTextNode.InnerText;
            }
            // Only set captionFont if the caption explicitly defines a <font> element.
            // Otherwise leave null so the field's own font is used as fallback.
            if (FindChild(captionNode, "font") is not null)
                captionFont = ParseFontElement(captionNode);
            if (FindChild(captionNode, "para") is not null)
                captionPara = ParseParaElement(captionNode);
        }

        double rotate = ParseDouble(GetAttr(node, "rotate")) ?? 0;
        var border = ParseBorderElement(node);

        // Check for <setProperty target="caption.value.#text" ref="fieldName"/>
        // This dynamically sets the caption text from a data field
        string? captionBindRef = null;
        bool hideIfEmpty = false;
        var scripts = ParseScripts(node);
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.LocalName == "setProperty")
            {
                string? target = GetAttr(child, "target");
                string? propRef = GetAttr(child, "ref");
                if (target is not null && target.Contains("caption") && propRef is not null)
                {
                    captionBindRef = propRef;
                }
            }
        }

        // Derive HideIfEmpty flag from scripts as a fallback for when script engine is not used.
        // Pattern: if (this.rawValue == null || this.rawValue == "") { this.presence = "hidden"; }
        // Only match this.rawValue checks (self-value). Do NOT match this.isNull — that has
        // different semantics. Do NOT match external field checks like "BBUNTERSCHRIFT.rawValue != '1'".
        foreach (var script in scripts)
        {
            if (script.Source.Contains("this.presence") && script.Source.Contains("hidden", StringComparison.OrdinalIgnoreCase)
                && script.Source.Contains("this.rawValue"))
            {
                hideIfEmpty = true;
                break;
            }
        }

        return new XfaFieldDef(
            Name: GetAttr(node, "name"),
            X: ParseMeasurement(GetAttr(node, "x")),
            Y: ParseMeasurement(GetAttr(node, "y")),
            W: ParseMeasurement(GetAttr(node, "w")),
            H: ParseMeasurement(GetAttr(node, "h")),
            MinH: ParseMeasurement(GetAttr(node, "minH")),
            Presence: GetAttr(node, "presence"),
            Font: font,
            Para: para,
            Margin: margin,
            BindRef: bindRef,
            BindMatch: bindMatch,
            StaticValue: staticValue,
            IsRichText: isRichText,
            IsMultiLine: isMultiLine,
            CaptionReserve: captionReserve,
            CaptionText: captionText,
            CaptionFont: captionFont,
            CaptionPara: captionPara,
            Rotate: rotate,
            Border: border,
            CaptionBindRef: captionBindRef,
            HideIfEmpty: hideIfEmpty,
            Scripts: scripts.Count > 0 ? scripts : null);
    }

    private XfaDrawDef ParseDraw(XmlNode node)
    {
        var font = ParseFontElement(node);
        var para = ParseParaElement(node);
        var margin = ParseMarginElement(node);
        var border = ParseBorderElement(node);
        XfaCorner? corner = null;

        string? textValue = null;
        bool isRichText = false;
        bool isLine = false;
        bool isRectangle = false;

        var valueNode = FindChild(node, "value");
        if (valueNode is not null)
        {
            var textNode = FindChild(valueNode, "text");
            if (textNode is not null)
                textValue = textNode.InnerText;

            var exDataNode = FindChild(valueNode, "exData");
            if (exDataNode is not null)
            {
                string? contentType = GetAttr(exDataNode, "contentType");
                if (contentType == "text/html")
                {
                    isRichText = true;
                    textValue = exDataNode.InnerXml;
                }
            }

            var lineNode = FindChild(valueNode, "line");
            if (lineNode is not null)
            {
                isLine = true;
                var edgeNode = FindChild(lineNode, "edge");
                if (edgeNode is not null)
                {
                    double thickness = ParseMeasurement(GetAttr(edgeNode, "thickness")) ?? 0.2;
                    border = border with { Visible = true, Thickness = thickness };
                }
            }

            var rectNode = FindChild(valueNode, "rectangle");
            if (rectNode is not null)
            {
                isRectangle = true;
                var edgeNode = FindChild(rectNode, "edge");
                if (edgeNode is not null)
                {
                    double thickness = ParseMeasurement(GetAttr(edgeNode, "thickness")) ?? 0.2;
                    border = border with { Visible = true, Thickness = thickness };
                }
                var cornerNode = FindChild(rectNode, "corner");
                if (cornerNode is not null)
                {
                    double radius = ParseMeasurement(GetAttr(cornerNode, "radius")) ?? 0;
                    corner = new XfaCorner(radius);
                }
            }
        }

        return new XfaDrawDef(
            Name: GetAttr(node, "name"),
            X: ParseMeasurement(GetAttr(node, "x")),
            Y: ParseMeasurement(GetAttr(node, "y")),
            W: ParseMeasurement(GetAttr(node, "w")),
            H: ParseMeasurement(GetAttr(node, "h")),
            MinH: ParseMeasurement(GetAttr(node, "minH")),
            Presence: GetAttr(node, "presence"),
            Font: font,
            Para: para,
            Margin: margin,
            TextValue: textValue,
            IsRichText: isRichText,
            IsLine: isLine,
            IsRectangle: isRectangle,
            Border: border,
            Corner: corner);
    }

    private XfaSubformDef ParseSubform(XmlNode node)
    {
        var children = new List<XfaElement>();
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;
            if (child.LocalName is "bind" or "occur" or "keep" or "breakBefore"
                or "breakAfter" or "break" or "border" or "margin" or "event"
                or "calculate" or "validate" or "variables" or "proto" or "assist") continue;

            var element = ParseElement(child);
            if (element is not null)
                children.Add(element);
        }

        // Extract scripts from events and calculate blocks
        var scripts = ParseScripts(node);
        var namedScripts = ParseVariables(node);

        // Parse occur
        int occurMin = 1, occurMax = 1;
        var occurNode = FindChild(node, "occur");
        if (occurNode is not null)
        {
            occurMin = (int)(ParseDouble(GetAttr(occurNode, "min")) ?? 1);
            occurMax = (int)(ParseDouble(GetAttr(occurNode, "max")) ?? 1);
        }

        // Parse breakBefore (new-style) or break (old-style)
        bool hasBreakBefore = false;
        string? breakTarget = null;
        string? breakTargetType = null;
        var breakBeforeNode = FindChild(node, "breakBefore");
        if (breakBeforeNode is not null)
        {
            hasBreakBefore = true;
            breakTarget = GetAttr(breakBeforeNode, "target");
            breakTargetType = GetAttr(breakBeforeNode, "targetType");
        }

        // Old-style <break> element: <break before="pageArea" beforeTarget="DS_V1_SET0"/>
        var breakNode = FindChild(node, "break");
        if (breakNode is not null && !hasBreakBefore)
        {
            string? beforeType = GetAttr(breakNode, "before");
            if (beforeType is "pageArea" or "contentArea")
            {
                hasBreakBefore = true;
                breakTarget = GetAttr(breakNode, "beforeTarget");
                breakTargetType = beforeType;
            }
        }

        // columnWidths for table layout
        string? columnWidths = GetAttr(node, "columnWidths");

        return new XfaSubformDef(
            Name: GetAttr(node, "name"),
            X: ParseMeasurement(GetAttr(node, "x")),
            Y: ParseMeasurement(GetAttr(node, "y")),
            W: ParseMeasurement(GetAttr(node, "w")),
            H: ParseMeasurement(GetAttr(node, "h")),
            MinH: ParseMeasurement(GetAttr(node, "minH")),
            Presence: GetAttr(node, "presence"),
            Layout: GetAttr(node, "layout") ?? "position",
            Children: children,
            OccurMin: occurMin,
            OccurMax: occurMax,
            BindRef: ParseBindRef(node),
            BindMatch: ParseBindMatch(node),
            Margin: ParseMarginElement(node),
            Border: ParseBorderElement(node),
            ColumnWidths: columnWidths,
            HasBreakBefore: hasBreakBefore,
            BreakTarget: breakTarget,
            BreakTargetType: breakTargetType,
            Scripts: scripts.Count > 0 ? scripts : null,
            NamedScripts: namedScripts.Count > 0 ? namedScripts : null);
    }

    private XfaSubformSetDef ParseSubformSet(XmlNode node)
    {
        string relation = GetAttr(node, "relation") ?? "ordered";
        var children = new List<XfaElement>();
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;
            var element = ParseElement(child);
            if (element is not null)
                children.Add(element);
        }

        return new XfaSubformSetDef(
            Name: GetAttr(node, "name"),
            Relation: relation,
            Children: children);
    }

    // ===================== Script Extraction =====================

    /// <summary>
    /// Extracts all scripts from &lt;event&gt; and &lt;calculate&gt; child elements.
    /// </summary>
    internal static List<XfaScript> ParseScripts(XmlNode parentNode)
    {
        var scripts = new List<XfaScript>();

        foreach (XmlNode child in parentNode.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;

            if (child.LocalName == "event")
            {
                string activity = GetAttr(child, "activity") ?? "ready";
                var scriptNode = FindChild(child, "script");
                if (scriptNode is not null)
                {
                    string source = scriptNode.InnerText;
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        string contentType = GetAttr(scriptNode, "contentType") ?? "application/x-javascript";
                        string runAt = GetAttr(scriptNode, "runAt") ?? "client";

                        // Only extract JavaScript scripts (skip FormCalc)
                        if (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                            || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase))
                        {
                            scripts.Add(new XfaScript(source.Trim(), "event", activity, runAt));
                        }
                    }
                }
            }
            else if (child.LocalName == "calculate")
            {
                var scriptNode = FindChild(child, "script");
                if (scriptNode is not null)
                {
                    string source = scriptNode.InnerText;
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        string contentType = GetAttr(scriptNode, "contentType") ?? "application/x-javascript";
                        if (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                            || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase))
                        {
                            scripts.Add(new XfaScript(source.Trim(), "calculate", "calculate"));
                        }
                    }
                }
            }
        }

        return scripts;
    }

    /// <summary>
    /// Extracts named scripts from a &lt;variables&gt; block within a subform.
    /// These are script libraries referenced by name from other scripts.
    /// </summary>
    internal static List<XfaNamedScript> ParseVariables(XmlNode subformNode)
    {
        var namedScripts = new List<XfaNamedScript>();

        var variablesNode = FindChild(subformNode, "variables");
        if (variablesNode is null) return namedScripts;

        foreach (XmlNode child in variablesNode.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;

            if (child.LocalName == "script")
            {
                string? name = GetAttr(child, "name");
                string source = child.InnerText;
                string contentType = GetAttr(child, "contentType") ?? "application/x-javascript";

                if (name is not null && !string.IsNullOrWhiteSpace(source)
                    && (contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)))
                {
                    namedScripts.Add(new XfaNamedScript(name, source.Trim()));
                }
            }
        }

        return namedScripts;
    }

    // ===================== Helper Parsers =====================

    internal static XfaFont ParseFontElement(XmlNode parentNode)
    {
        var fontNode = FindChild(parentNode, "font");
        if (fontNode is null)
            return new XfaFont();

        return new XfaFont(
            Typeface: GetAttr(fontNode, "typeface") ?? "Arial",
            SizePt: ParsePtValue(GetAttr(fontNode, "size")) ?? 10,
            Bold: GetAttr(fontNode, "weight") == "bold",
            Italic: GetAttr(fontNode, "posture") == "italic",
            Underline: GetAttr(fontNode, "underline") == "1");
    }

    internal static XfaPara ParseParaElement(XmlNode parentNode)
    {
        var paraNode = FindChild(parentNode, "para");
        if (paraNode is null)
            return new XfaPara();

        return new XfaPara(
            HAlign: GetAttr(paraNode, "hAlign") ?? "left",
            VAlign: GetAttr(paraNode, "vAlign") ?? "top",
            LineHeight: ParsePtValue(GetAttr(paraNode, "lineHeight")),
            SpaceBefore: ParseMeasurement(GetAttr(paraNode, "spaceBefore")),
            SpaceAfter: ParseMeasurement(GetAttr(paraNode, "spaceAfter")));
    }

    internal static XfaMargin ParseMarginElement(XmlNode parentNode)
    {
        var marginNode = FindChild(parentNode, "margin");
        if (marginNode is null)
            return new XfaMargin();

        return new XfaMargin(
            Top: ParseMeasurement(GetAttr(marginNode, "topInset")) ?? 0,
            Right: ParseMeasurement(GetAttr(marginNode, "rightInset")) ?? 0,
            Bottom: ParseMeasurement(GetAttr(marginNode, "bottomInset")) ?? 0,
            Left: ParseMeasurement(GetAttr(marginNode, "leftInset")) ?? 0);
    }

    internal static XfaBorder ParseBorderElement(XmlNode parentNode)
    {
        var borderNode = FindChild(parentNode, "border");
        if (borderNode is null)
            return new XfaBorder();

        // Check border-level presence
        string? borderPresence = GetAttr(borderNode, "presence");
        if (borderPresence == "hidden")
            return new XfaBorder(Visible: false);

        // Parse all edge elements (XFA borders can have 1 or 4 edges: top, right, bottom, left)
        double thickness = 0.2;
        string? strokeColor = null;
        bool anyEdgeVisible = false;

        foreach (XmlNode child in borderNode.ChildNodes)
        {
            if (child.LocalName != "edge") continue;

            string? edgePresence = GetAttr(child, "presence");
            if (edgePresence == "hidden") continue;

            anyEdgeVisible = true;
            double edgeThickness = ParseMeasurement(GetAttr(child, "thickness")) ?? 0.2;
            if (edgeThickness > thickness || strokeColor is null)
                thickness = edgeThickness;

            // Parse edge color
            var edgeColorNode = FindChild(child, "color");
            if (edgeColorNode is not null)
                strokeColor ??= GetAttr(edgeColorNode, "value");
        }

        string? fillColor = null;
        var fillNode = FindChild(borderNode, "fill");
        if (fillNode is not null)
        {
            var colorNode = FindChild(fillNode, "color");
            if (colorNode is not null)
                fillColor = GetAttr(colorNode, "value");
        }

        // Visible controls stroke border rendering — only true when edges are explicitly visible.
        // Fill backgrounds are rendered independently via FillColor, regardless of Visible.
        return new XfaBorder(anyEdgeVisible, thickness, fillColor, strokeColor);
    }

    private static string? ParseBindRef(XmlNode node)
    {
        var bindNode = FindChild(node, "bind");
        if (bindNode is null) return null;
        return GetAttr(bindNode, "ref");
    }

    private static string? ParseBindMatch(XmlNode node)
    {
        var bindNode = FindChild(node, "bind");
        if (bindNode is null) return null;
        return GetAttr(bindNode, "match");
    }

    // ===================== XML Helpers =====================

    private static XmlNode? FindTemplateRoot(XmlDocument doc)
    {
        // Look for <template> element at any depth
        var templates = doc.GetElementsByTagName("template");
        if (templates.Count > 0) return templates[0];

        // Check if the root itself is a template-containing element
        if (doc.DocumentElement?.LocalName == "template")
            return doc.DocumentElement;

        // Try XDP wrapper
        if (doc.DocumentElement?.LocalName == "xdp")
        {
            foreach (XmlNode child in doc.DocumentElement.ChildNodes)
            {
                if (child.LocalName == "template")
                    return child;
            }
        }

        return doc.DocumentElement;
    }

    internal static XmlNode? FindChild(XmlNode parent, string localName)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child.LocalName == localName)
                return child;
        }
        return null;
    }

    /// <summary>
    /// Recursively searches for a descendant element with the given local name.
    /// </summary>
    internal static XmlNode? FindDescendant(XmlNode parent, string localName)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child.LocalName == localName)
                return child;
            var found = FindDescendant(child, localName);
            if (found is not null)
                return found;
        }
        return null;
    }

    internal static string? GetAttr(XmlNode node, string name)
    {
        return node.Attributes?[name]?.Value;
    }

    // ===================== Measurement Parsing =====================

    /// <summary>
    /// Parses an XFA measurement string (e.g., "21mm", "4.665222in", "13pt", "=0mm")
    /// and returns the value in millimeters.
    /// </summary>
    internal static double? ParseMeasurement(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Handle "=0mm" format (computed values that indicate zero)
        value = value.TrimStart('=');

        if (value.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mm))
                return mm;
        }
        else if (value.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double inches))
                return inches * 25.4;
        }
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
                return pt * 25.4 / 72.0;
        }
        else if (value.EndsWith("cm", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double cm))
                return cm * 10.0;
        }
        else
        {
            // Bare number - assume pt (XFA default unit)
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
                return bare * 25.4 / 72.0;
        }

        return null;
    }

    /// <summary>
    /// Parses a point-value string (e.g., "13pt", "8.5pt") returning the value in points.
    /// </summary>
    internal static double? ParsePtValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
                return pt;
        }
        else
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pt))
                return pt;
        }

        return null;
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return result;
        return null;
    }
}
