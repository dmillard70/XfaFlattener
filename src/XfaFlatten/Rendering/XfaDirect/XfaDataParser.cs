using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Parses the XFA datasets XML packet into a hierarchical data structure for merging with templates.
/// </summary>
public sealed partial class XfaDataParser
{
    /// <summary>
    /// Parses the datasets XML string into an <see cref="XfaData"/> structure.
    /// </summary>
    public XfaData Parse(string datasetsXml)
    {
        var data = new XfaData();

        var doc = new XmlDocument();
        doc.LoadXml(datasetsXml);

        // Find the <xfa:data> element inside <xfa:datasets>
        XmlNode? dataRoot = FindDataElement(doc);
        if (dataRoot is null)
        {
            // Try treating the whole document as data
            dataRoot = doc.DocumentElement;
        }

        if (dataRoot is null)
            return data;

        // Parse recursively
        data.Root = ParseNode(dataRoot, null);
        IndexNodes(data.Root, data.NodesByName);

        return data;
    }

    private static XmlNode? FindDataElement(XmlDocument doc)
    {
        // Look for xfa:data element
        var datasets = doc.DocumentElement;
        if (datasets is null) return null;

        // If root is <xfa:datasets>, find <xfa:data> child
        if (datasets.LocalName == "datasets")
        {
            foreach (XmlNode child in datasets.ChildNodes)
            {
                if (child.LocalName == "data")
                    return child;
            }
        }

        // If root is already <xfa:data> or has data directly
        if (datasets.LocalName == "data")
            return datasets;

        return datasets;
    }

    private XfaDataNode ParseNode(XmlNode xmlNode, XfaDataNode? parent)
    {
        var node = new XfaDataNode(xmlNode.LocalName) { Parent = parent };

        // Store XML attributes (used by data-driven dynamic tables)
        if (xmlNode.Attributes is { Count: > 0 })
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlAttribute attr in xmlNode.Attributes)
            {
                // Skip namespace declarations
                if (attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
                if (attr.Prefix == "xfa") continue;
                attrs[attr.LocalName] = attr.Value;
            }
            if (attrs.Count > 0)
                node.Attributes = attrs;
        }

        // Check if this node has rich text (XHTML body content)
        bool hasChildElements = false;
        bool hasBodyElement = false;
        foreach (XmlNode child in xmlNode.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Element)
            {
                hasChildElements = true;
                if (child.LocalName == "body")
                {
                    hasBodyElement = true;
                    break;
                }
            }
        }

        if (hasBodyElement)
        {
            // Rich text: extract the body content as HTML
            var bodyNode = FindChildByLocalName(xmlNode, "body");
            if (bodyNode is not null)
            {
                node.RichTextHtml = bodyNode.InnerXml;
                node.TextValue = StripHtmlToPlainText(bodyNode.InnerXml);
            }
        }
        else if (hasChildElements)
        {
            // Container node - recurse into children
            foreach (XmlNode child in xmlNode.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                var childNode = ParseNode(child, node);
                node.Children.Add(childNode);
            }
        }
        else
        {
            // Leaf node - get text value
            string text = xmlNode.InnerText;
            if (!string.IsNullOrEmpty(text))
                node.TextValue = text;
        }

        return node;
    }

    private static void IndexNodes(XfaDataNode node, Dictionary<string, List<XfaDataNode>> index)
    {
        if (!index.TryGetValue(node.Name, out var list))
        {
            list = new List<XfaDataNode>();
            index[node.Name] = list;
        }
        list.Add(node);

        foreach (var child in node.Children)
            IndexNodes(child, index);
    }

    /// <summary>
    /// Detects if rich text HTML content has predominantly bold formatting.
    /// Returns true if the text is wrapped in &lt;b&gt; tags or has font-weight:bold.
    /// </summary>
    internal static bool IsHtmlBold(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        string trimmed = html.Trim();

        // Check for <b> wrapping
        if (trimmed.Contains("<b>", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("<b ", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check for namespace-prefixed bold: <html:b>, <xhtml:b>, etc.
        if (trimmed.Contains(":b>", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(":b ", StringComparison.OrdinalIgnoreCase))
            return true;

        // Check for CSS font-weight:bold
        if (trimmed.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Detects if rich text HTML content has italic formatting.
    /// </summary>
    internal static bool IsHtmlItalic(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return html.Contains("<i>", StringComparison.OrdinalIgnoreCase)
               || html.Contains("<i ", StringComparison.OrdinalIgnoreCase)
               // Namespace-prefixed italic: <html:i>, <xhtml:i>, etc.
               || html.Contains(":i>", StringComparison.OrdinalIgnoreCase)
               || html.Contains(":i ", StringComparison.OrdinalIgnoreCase)
               || html.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
               || html.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips XHTML/HTML tags from rich text, preserving paragraph breaks.
    /// </summary>
    internal static string StripHtmlToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var sb = new StringBuilder();

        // Replace <p> and <br> tags with newlines
        string processed = html;
        processed = ParagraphEndRegex().Replace(processed, "\n");
        processed = BreakRegex().Replace(processed, "\n");

        // Handle xfa-spacerun spans (preserve spaces)
        processed = SpaceRunRegex().Replace(processed, " ");

        // Handle xfa-tab-count spans (preserve tab-like spacing)
        processed = TabCountRegex().Replace(processed, "\t");

        // Strip all remaining HTML tags
        processed = HtmlTagRegex().Replace(processed, "");

        // Decode HTML entities
        processed = processed
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("&#xA;", "\n");

        // Normalize whitespace per line, preserving internal blank lines
        // (empty <p> paragraphs in XFA rich text act as line spacers)
        var lines = processed.Split('\n');
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
            result.Add(line.Trim());

        // Trim leading and trailing empty lines for the plain-text path.
        // The per-segment rich text path (ParseRichTextSegments) handles
        // intentional blank paragraphs with finer control.
        while (result.Count > 0 && result[0].Length == 0)
            result.RemoveAt(0);
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);

        return string.Join('\n', result);
    }

    /// <summary>
    /// Parses rich text HTML into segments with per-paragraph bold/italic formatting.
    /// Each segment represents a contiguous block of text with the same formatting.
    /// </summary>
    internal static List<RichTextSegment> ParseRichTextSegments(string html)
    {
        var segments = new List<RichTextSegment>();
        if (string.IsNullOrEmpty(html)) return segments;

        try
        {
            // Wrap in a root element for XML parsing
            string xmlStr = $"<root xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">{html}</root>";
            var doc = new XmlDocument();
            doc.LoadXml(xmlStr);

            if (doc.DocumentElement is null) return segments;

            // Walk the DOM tree collecting paragraph-level segments
            WalkForSegments(doc.DocumentElement, false, false, false, null, segments);
        }
        catch
        {
            // Fallback: treat entire text as single segment with overall bold/italic detection
            string plainText = StripHtmlToPlainText(html);
            if (!string.IsNullOrEmpty(plainText))
            {
                segments.Add(new RichTextSegment(plainText, IsHtmlBold(html), IsHtmlItalic(html),
                    html.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase)));
            }
        }

        // Consolidate consecutive segments with identical formatting
        if (segments.Count > 1)
        {
            var consolidated = new List<RichTextSegment>();
            var current = segments[0];
            for (int i = 1; i < segments.Count; i++)
            {
                var next = segments[i];
                if (next.Bold == current.Bold && next.Italic == current.Italic
                    && next.Underline == current.Underline && next.FontSizePt == current.FontSizePt
                    && next.FontFamily == current.FontFamily)
                {
                    // Merge: preserve existing newlines (including blank lines from empty paragraphs)
                    current = current.Text.EndsWith('\n')
                        ? current with { Text = current.Text + next.Text }
                        : current with { Text = current.Text + "\n" + next.Text };
                }
                else
                {
                    consolidated.Add(current);
                    current = next;
                }
            }
            consolidated.Add(current);
            segments = consolidated;
        }

        // Trailing blank paragraphs in XFA rich text (xfa-spacerun spacers) contribute to
        // field height. Adobe's renderer preserves some trailing blanks but not unlimited.
        // Cap trailing blank lines to 3 to better match reference block heights.
        if (segments.Count > 0)
        {
            var last = segments[^1];
            string text = last.Text;
            // Count trailing newlines
            int trailingNewlines = 0;
            for (int i = text.Length - 1; i >= 0 && text[i] == '\n'; i--)
                trailingNewlines++;
            // Keep at most 4 trailing blank lines (each \n = one blank paragraph boundary)
            if (trailingNewlines > 4)
            {
                string trimmed = text[..^(trailingNewlines - 4)];
                segments[^1] = last with { Text = trimmed };
            }
        }

        return segments;
    }

    /// <summary>
    /// Parses rich text HTML into a list of paragraphs, each containing inline runs with
    /// individual formatting. This preserves per-span bold/italic/font within a line,
    /// conforming to the XFA 3.3 spec (Chapter 27) inline flow model.
    /// </summary>
    internal static List<RichTextParagraph> ParseRichTextRuns(string html)
    {
        if (string.IsNullOrEmpty(html)) return new List<RichTextParagraph>();

        XmlDocument doc;
        try
        {
            string xmlStr = $"<root xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\">{html}</root>";
            doc = new XmlDocument();
            doc.LoadXml(xmlStr);
        }
        catch
        {
            // XML parse failure — fall back to single paragraph with plain text
            string plainText = StripHtmlToPlainText(html);
            if (string.IsNullOrEmpty(plainText)) return new List<RichTextParagraph>();
            var seg = new RichTextSegment(plainText, IsHtmlBold(html), IsHtmlItalic(html),
                html.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase));
            return new List<RichTextParagraph> { new(new List<RichTextSegment> { seg }) };
        }

        if (doc.DocumentElement is null) return new List<RichTextParagraph>();

        // Check if there are any <p> elements
        bool hasParagraphs = false;
        foreach (XmlNode child in doc.DocumentElement.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Element &&
                child.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                hasParagraphs = true;
                break;
            }
        }

        if (!hasParagraphs)
        {
            // No <p> elements — treat entire content as single paragraph
            var runs = new List<RichTextSegment>();
            WalkForInlineRuns(doc.DocumentElement, false, false, false, null, null, runs);
            if (runs.Count == 0) return new List<RichTextParagraph>();
            return new List<RichTextParagraph> { new(runs) };
        }

        var paragraphs = new List<RichTextParagraph>();

        foreach (XmlNode child in doc.DocumentElement.ChildNodes)
        {
            if (child.NodeType != XmlNodeType.Element) continue;
            if (!child.LocalName.Equals("p", StringComparison.OrdinalIgnoreCase)) continue;

            // Parse <p>-level style for inherited formatting and margins
            bool pBold = false, pItalic = false, pUnderline = false;
            double? pFontSize = null;
            string? pFontFamily = null;
            double? marginTopPt = null, marginBottomPt = null;
            double? marginLeftPt = null, textIndentPt = null;

            string? pStyle = child.Attributes?["style"]?.Value;
            if (pStyle is not null)
            {
                if (pStyle.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
                    || pStyle.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase))
                    pBold = true;
                if (pStyle.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
                    || pStyle.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase))
                    pItalic = true;
                if (pStyle.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase)
                    || pStyle.Contains("text-decoration: underline", StringComparison.OrdinalIgnoreCase))
                    pUnderline = true;

                var fontSizeMatch = FontSizeRegex().Match(pStyle);
                if (fontSizeMatch.Success && double.TryParse(fontSizeMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double ps))
                    pFontSize = ps;

                var fontFamilyMatch = FontFamilyRegex().Match(pStyle);
                if (fontFamilyMatch.Success)
                    pFontFamily = fontFamilyMatch.Groups[1].Value.Trim().Trim('\'', '"');

                var marginTopMatch = MarginTopRegex().Match(pStyle);
                if (marginTopMatch.Success && double.TryParse(marginTopMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double mt))
                    marginTopPt = mt;

                var marginBottomMatch = MarginBottomRegex().Match(pStyle);
                if (marginBottomMatch.Success && double.TryParse(marginBottomMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double mb))
                    marginBottomPt = mb;

                marginLeftPt = ParseCssMeasurementPt(MarginLeftRegex().Match(pStyle));
                textIndentPt = ParseCssMeasurementPt(TextIndentRegex().Match(pStyle));
            }

            // Collect inline runs within this <p>
            var runs = new List<RichTextSegment>();
            WalkForInlineRuns(child, pBold, pItalic, pUnderline, pFontSize, pFontFamily, runs);

            if (runs.Count == 0)
            {
                // Empty paragraph — blank line spacer
                paragraphs.Add(new RichTextParagraph(
                    new List<RichTextSegment> { new("", false, false, false) },
                    marginTopPt, marginBottomPt, marginLeftPt, textIndentPt));
            }
            else
            {
                paragraphs.Add(new RichTextParagraph(runs, marginTopPt, marginBottomPt,
                    marginLeftPt, textIndentPt));
            }
        }

        // Cap trailing blank paragraphs to 2 (matches ParseRichTextSegments behavior)
        int trailingBlanks = 0;
        for (int i = paragraphs.Count - 1; i >= 0; i--)
        {
            if (paragraphs[i].Runs.Count == 1 && string.IsNullOrEmpty(paragraphs[i].Runs[0].Text))
                trailingBlanks++;
            else
                break;
        }
        if (trailingBlanks > 2)
        {
            paragraphs.RemoveRange(paragraphs.Count - (trailingBlanks - 2), trailingBlanks - 2);
        }

        return paragraphs;
    }

    /// <summary>
    /// Walks child nodes of a single paragraph element, collecting inline runs
    /// with per-span formatting. Does NOT handle &lt;p&gt; boundaries — those are
    /// handled by the caller in <see cref="ParseRichTextRuns"/>.
    /// </summary>
    private static void WalkForInlineRuns(XmlNode node, bool parentBold, bool parentItalic,
        bool parentUnderline, double? parentFontSize, string? parentFontFamily,
        List<RichTextSegment> runs)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Text)
            {
                string rawText = child.Value ?? "";
                // Collapse consecutive whitespace into single spaces (preserves inter-element spacing)
                string text = CollapseWhitespace(rawText);

                if (string.IsNullOrWhiteSpace(text))
                {
                    // Pure whitespace node: ensure spacing between adjacent elements
                    if (text.Length > 0 && runs.Count > 0)
                    {
                        var prev = runs[^1];
                        if (prev.Text.Length > 0 && !prev.Text.EndsWith(' '))
                            runs[^1] = prev with { Text = prev.Text + " " };
                    }
                    continue;
                }

                string trimmed = text.Trim();
                bool hasLeadingSpace = text.Length > 0 && text[0] == ' ';

                // Add inter-element space to previous run if needed
                if (hasLeadingSpace && runs.Count > 0)
                {
                    var prev = runs[^1];
                    if (prev.Text.Length > 0 && !prev.Text.EndsWith(' '))
                        runs[^1] = prev with { Text = prev.Text + " " };
                }

                // Merge with previous run if same formatting
                if (runs.Count > 0 && runs[^1].Bold == parentBold
                    && runs[^1].Italic == parentItalic && runs[^1].Underline == parentUnderline
                    && runs[^1].FontSizePt == parentFontSize
                    && runs[^1].FontFamily == parentFontFamily)
                {
                    var prev = runs[^1];
                    // If previous ends with space, no need for another separator
                    if (prev.Text.EndsWith(' ') || hasLeadingSpace)
                        runs[^1] = prev with { Text = prev.Text + trimmed };
                    else
                        runs[^1] = prev with { Text = prev.Text + " " + trimmed };
                }
                else
                {
                    runs.Add(new RichTextSegment(trimmed, parentBold, parentItalic, parentUnderline,
                        parentFontSize, parentFontFamily));
                }

                // Preserve trailing space
                if (text.Length > 0 && text[^1] == ' ' && trimmed.Length > 0)
                {
                    var last = runs[^1];
                    if (!last.Text.EndsWith(' '))
                        runs[^1] = last with { Text = last.Text + " " };
                }
                continue;
            }

            if (child.NodeType != XmlNodeType.Element) continue;

            string localName = child.LocalName.ToLowerInvariant();
            bool bold = parentBold, italic = parentItalic, underline = parentUnderline;
            double? fontSize = parentFontSize;
            string? fontFamily = parentFontFamily;

            if (localName is "b" or "strong") bold = true;
            if (localName is "i" or "em") italic = true;
            if (localName == "u") underline = true;

            string? style = child.Attributes?["style"]?.Value;
            if (style is not null)
            {
                if (style.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase))
                    bold = true;
                if (style.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase))
                    italic = true;
                if (style.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("text-decoration: underline", StringComparison.OrdinalIgnoreCase))
                    underline = true;

                var fontSizeMatch = FontSizeRegex().Match(style);
                if (fontSizeMatch.Success && double.TryParse(fontSizeMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                    fontSize = fs;

                var fontFamilyMatch = FontFamilyRegex().Match(style);
                if (fontFamilyMatch.Success)
                    fontFamily = fontFamilyMatch.Groups[1].Value.Trim().Trim('\'', '"');

                // Handle xfa-spacerun: append space to previous run
                if (style.Contains("xfa-spacerun:yes", StringComparison.OrdinalIgnoreCase))
                {
                    if (runs.Count > 0)
                    {
                        var prev = runs[^1];
                        if (!prev.Text.EndsWith(' '))
                            runs[^1] = prev with { Text = prev.Text + " " };
                    }
                    continue;
                }

                // Handle xfa-tab-count: append tab as spaces
                if (style.Contains("xfa-tab-count", StringComparison.OrdinalIgnoreCase))
                {
                    if (runs.Count > 0)
                    {
                        var prev = runs[^1];
                        runs[^1] = prev with { Text = prev.Text + "    " };
                    }
                    continue;
                }
            }

            // Recurse into child elements (b, i, span, etc.)
            WalkForInlineRuns(child, bold, italic, underline, fontSize, fontFamily, runs);
        }
    }

    /// <summary>
    /// Collapses consecutive whitespace characters (spaces, tabs, newlines) into single spaces.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        if (text.Length == 0) return text;
        // Fast path: if no multi-char whitespace, return as-is
        bool hasConsecutive = false;
        bool hasSpecial = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == '\t') { hasSpecial = true; break; }
            if (c == ' ' && i + 1 < text.Length && text[i + 1] == ' ') { hasConsecutive = true; break; }
        }
        if (!hasSpecial && !hasConsecutive) return text;

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private static void WalkForSegments(XmlNode node, bool parentBold, bool parentItalic,
        bool parentUnderline, double? parentFontSize, List<RichTextSegment> segments,
        string? parentFontFamily = null)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Text)
            {
                string text = child.Value?.Trim() ?? "";
                if (text.Length > 0)
                {
                    // Merge with previous segment if same formatting, else create new
                    if (segments.Count > 0 && segments[^1].Bold == parentBold
                        && segments[^1].Italic == parentItalic && segments[^1].Underline == parentUnderline
                        && segments[^1].FontSizePt == parentFontSize
                        && segments[^1].FontFamily == parentFontFamily
                        && !segments[^1].Text.EndsWith('\n'))
                    {
                        var prev = segments[^1];
                        segments[^1] = prev with { Text = prev.Text + " " + text };
                    }
                    else
                    {
                        segments.Add(new RichTextSegment(text, parentBold, parentItalic, parentUnderline,
                            parentFontSize, parentFontFamily));
                    }
                }
                continue;
            }

            if (child.NodeType != XmlNodeType.Element) continue;

            string localName = child.LocalName.ToLowerInvariant();
            bool bold = parentBold;
            bool italic = parentItalic;
            bool underline = parentUnderline;
            double? fontSize = parentFontSize;
            string? fontFamily = parentFontFamily;

            // Check element name for formatting
            if (localName == "b" || localName == "strong")
                bold = true;
            if (localName == "i" || localName == "em")
                italic = true;
            if (localName == "u")
                underline = true;

            // Check style attribute for formatting, font-size, and font-family
            string? style = child.Attributes?["style"]?.Value;
            if (style is not null)
            {
                if (style.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase))
                    bold = true;
                if (style.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase))
                    italic = true;
                if (style.Contains("text-decoration:underline", StringComparison.OrdinalIgnoreCase)
                    || style.Contains("text-decoration: underline", StringComparison.OrdinalIgnoreCase))
                    underline = true;

                // Parse font-size from style (e.g., "font-size:10pt" or "font-size: 8.5pt")
                var fontSizeMatch = FontSizeRegex().Match(style);
                if (fontSizeMatch.Success && double.TryParse(fontSizeMatch.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedSize))
                {
                    fontSize = parsedSize;
                }

                // Parse font-family from style (e.g., "font-family:'Sparkasse Rg'")
                var fontFamilyMatch = FontFamilyRegex().Match(style);
                if (fontFamilyMatch.Success)
                {
                    fontFamily = fontFamilyMatch.Groups[1].Value.Trim().Trim('\'', '"');
                }
            }

            // For <p> elements, handle paragraph breaks and empty paragraph detection
            if (localName == "p")
            {
                // Add paragraph break if there's existing content
                if (segments.Count > 0 && !segments[^1].Text.EndsWith('\n'))
                {
                    var prev = segments[^1];
                    segments[^1] = prev with { Text = prev.Text + "\n" };
                }

                int countBefore = segments.Count;
                string? lastTextBefore = segments.Count > 0 ? segments[^1].Text : null;

                WalkForSegments(child, bold, italic, underline, fontSize, segments, fontFamily);

                // If this <p> had no visible text (empty/whitespace-only paragraph like
                // xfa-spacerun spacers), preserve it as a blank line in the output.
                bool wasEmpty = (segments.Count == countBefore) &&
                                (lastTextBefore is null || segments[^1].Text == lastTextBefore);
                if (wasEmpty)
                {
                    if (segments.Count > 0)
                    {
                        var prev = segments[^1];
                        segments[^1] = prev with { Text = prev.Text + "\n" };
                    }
                    else
                    {
                        // Very first paragraph is empty — create initial segment with blank line
                        segments.Add(new RichTextSegment("\n", bold, italic, underline, fontSize, fontFamily));
                    }
                }

                continue;
            }

            WalkForSegments(child, bold, italic, underline, fontSize, segments, fontFamily);
        }
    }

    private static XmlNode? FindChildByLocalName(XmlNode parent, string localName)
    {
        foreach (XmlNode child in parent.ChildNodes)
        {
            if (child.LocalName == localName) return child;
        }
        return null;
    }

    [GeneratedRegex(@"</p\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEndRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<span[^>]*xfa-spacerun:yes[^>]*>[^<]*</span\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex SpaceRunRegex();

    [GeneratedRegex(@"<span[^>]*xfa-tab-count[^>]*>[^<]*</span\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TabCountRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"font-size:\s*([0-9.]+)pt", RegexOptions.IgnoreCase)]
    private static partial Regex FontSizeRegex();

    [GeneratedRegex(@"font-family:\s*'?""?([^;'""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FontFamilyRegex();

    [GeneratedRegex(@"margin-top:\s*([0-9.]+)pt", RegexOptions.IgnoreCase)]
    private static partial Regex MarginTopRegex();

    [GeneratedRegex(@"margin-bottom:\s*([0-9.]+)pt", RegexOptions.IgnoreCase)]
    private static partial Regex MarginBottomRegex();

    [GeneratedRegex(@"(?<![a-z-])margin-left:\s*(-?[0-9.]+)(pt|in)", RegexOptions.IgnoreCase)]
    private static partial Regex MarginLeftRegex();

    [GeneratedRegex(@"text-indent:\s*(-?[0-9.]+)(pt|in)", RegexOptions.IgnoreCase)]
    private static partial Regex TextIndentRegex();

    /// <summary>
    /// Parses a CSS measurement value with pt or in unit, returning the value in points.
    /// </summary>
    private static double? ParseCssMeasurementPt(Match match)
    {
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            return null;
        string unit = match.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "in" => val * 72.0, // inches to points
            _ => val // pt
        };
    }
}
