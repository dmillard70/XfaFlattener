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

        // Normalize whitespace per line
        var lines = processed.Split('\n');
        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(trimmed);
            }
        }

        return sb.ToString();
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
}
