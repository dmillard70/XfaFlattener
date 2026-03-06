using System.Text.RegularExpressions;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Computes element positions by walking the template tree, merging data,
/// and producing a flat list of <see cref="LayoutItem"/>s for rendering.
/// </summary>
public sealed class XfaLayoutEngine
{
    private readonly List<XfaPageArea> _pageAreas;
    private readonly Dictionary<string, int> _pageAreaByName;
    private readonly XfaData _data;
    private readonly bool _verbose;
    private readonly List<LayoutItem> _items = new();
    private int _currentPage;
    private int _totalPages;
    private int _currentPageAreaIdx; // Track which page area is active
    private readonly List<int> _pageToAreaMap = new(); // Maps page index → page area index
    private readonly XfaScriptEngine? _scriptEngine;

    // Track element → page assignments for layout:ready scripts
    private readonly Dictionary<string, int> _elementPageMap = new(StringComparer.OrdinalIgnoreCase);

    // Track script-created element proxies for parent chain navigation
    private readonly Dictionary<string, XfaElementProxy> _proxyCache = new(StringComparer.OrdinalIgnoreCase);

    // Caption reserve overrides from JS scripts: "this.parent.wert.caption.reserve = this.rawValue"
    // Key = data context path + "." + target field name, Value = reserve in mm
    private readonly Dictionary<string, double> _captionReserveOverrides = new(StringComparer.OrdinalIgnoreCase);

    // Active overflow leader/trailer stack (XFA 3.3 Ch.8 p.318-326):
    // When a subform with <overflow leader="X" trailer="Y"> is being laid out:
    // - Push leader template + context. On page advance, emit leader at top of new page.
    // - Push trailer template + context. Before page advance, emit trailer at bottom of old page.
    private record OverflowLeaderContext(
        XfaSubformDef LeaderTemplate,
        double X,
        double AvailW,
        XfaDataNode? DataCtx);
    private readonly Stack<OverflowLeaderContext> _overflowLeaderStack = new();

    private record OverflowTrailerContext(
        XfaSubformDef TrailerTemplate,
        double X,
        double AvailW,
        XfaDataNode? DataCtx);
    private readonly Stack<OverflowTrailerContext> _overflowTrailerStack = new();

    // Accumulated top margin from ancestor subforms (XFA box model continuation).
    // When content overflows to a new page within a subform, the ancestor subforms'
    // top margins must be re-applied on the continuation page so content doesn't
    // start at the raw content area top.
    private double _continuationTopMargin = 0;

    // Number of content-flow items emitted during PerformLayoutPass.
    // Used to distinguish content items (which need X adjustment on page breaks)
    // from page-area static items (which have their own absolute coordinates).
    private int _contentItemCount;

    public XfaLayoutEngine(List<XfaPageArea> pageAreas, XfaData data, bool verbose,
        XfaScriptEngine? scriptEngine = null)
    {
        _pageAreas = pageAreas;
        _data = data;
        _verbose = verbose;
        _scriptEngine = scriptEngine;

        // Give the script engine access to the data tree for resolveNode() support
        if (_scriptEngine is not null)
            _scriptEngine.SetData(data);

        // Build name→index map for page area lookup
        _pageAreaByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pageAreas.Count; i++)
        {
            _pageAreaByName[pageAreas[i].Name] = i;
        }
    }

    /// <summary>
    /// Layout result containing all positioned items and the total page count.
    /// </summary>
    /// <param name="Items">All positioned items for rendering.</param>
    /// <param name="TotalPages">Total number of pages.</param>
    /// <param name="PageAreaMap">Maps each page index to the page area index to use.</param>
    public record LayoutResult(List<LayoutItem> Items, int TotalPages, List<int>? PageAreaMap = null);

    /// <summary>
    /// Performs layout of the root subform, producing positioned items.
    /// Supports two-pass layout: if a script engine is present and layout:ready scripts exist,
    /// the first pass computes page assignments, executes layout:ready scripts, then re-layouts.
    /// </summary>
    public LayoutResult Layout(XfaSubformDef rootSubform)
    {
        // Register named scripts from the root subform (and recursively from children)
        if (_scriptEngine is not null)
        {
            RegisterNamedScriptsRecursive(rootSubform);
            _scriptEngine.InitializeNamedScripts();

            if (_verbose)
            {
                int contentScripts = CountScriptsRecursive(rootSubform);
                int pageAreaScripts = 0;
                foreach (var pa in _pageAreas)
                    foreach (var el in pa.StaticElements)
                        pageAreaScripts += CountScriptsRecursive(el);
                Console.WriteLine($"  [Script] Total scripts: {contentScripts + pageAreaScripts} ({contentScripts} content, {pageAreaScripts} page area)");
            }
        }

        // Pass 1: Layout with calculate/initialize scripts
        PerformLayoutPass(rootSubform);

        // Check if layout:ready scripts exist and need a second pass
        if (_scriptEngine is not null && HasLayoutReadyScripts(rootSubform))
        {
            // Set layout info so page() and pageCount() work
            _scriptEngine.SetLayoutInfo(_totalPages, _elementPageMap);

            // Execute layout:ready scripts
            ExecuteLayoutReadyScripts(rootSubform, _data.Root);

            if (_scriptEngine.HasLayoutReadyModifications)
            {
                if (_verbose)
                    Console.WriteLine("  [Layout] Re-layout triggered by layout:ready script modifications");

                // Pass 2: Re-layout with script-modified values
                PerformLayoutPass(rootSubform);
            }
        }

        // Post-process: inject page numbers where possible
        InjectPageNumbers();

        // Post-process: adjust content item X positions for pages with different content areas.
        // During layout, x is passed by value from the initial page's content area. When content
        // overflows to pages with different content areas (e.g., DS_V1_SET0 x=21mm → DS_V2_SET0 x=16mm),
        // the X positions of items on those pages need correction.
        AdjustContentAreaXForPageBreaks();

        return new LayoutResult(_items, _totalPages, new List<int>(_pageToAreaMap));
    }

    /// <summary>
    /// Performs a single layout pass.
    /// </summary>
    private void PerformLayoutPass(XfaSubformDef rootSubform)
    {
        _currentPage = 0;
        _currentPageAreaIdx = 0;
        _pageToAreaMap.Clear();
        _pageToAreaMap.Add(_currentPageAreaIdx); // Page 0 uses first page area
        _items.Clear();
        _elementPageMap.Clear();
        _proxyCache.Clear();
        _captionReserveOverrides.Clear();
        _overflowLeaderStack.Clear();
        _overflowTrailerStack.Clear();

        // Start data context from the root data node
        var dataCtx = _data.Root;

        // Get the content area for the first page
        var ca = GetContentArea(_currentPage);
        double curY = ca.Y;

        LayoutSubform(rootSubform, ca.X, ref curY, ca.W, ca.Y + ca.H, dataCtx);

        _totalPages = _currentPage + 1;
        _contentItemCount = _items.Count;
    }

    private double LayoutElement(XfaElement element, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        // Skip hidden elements — but still process caption.reserve scripts
        if (element.Presence is "hidden" or "inactive")
        {
            if (element is XfaFieldDef hiddenField)
            {
                if (hiddenField.Scripts is { Count: > 0 })
                    ProcessCaptionReserveScripts(hiddenField, dataCtx);
            }
            return 0;
        }

        return element switch
        {
            XfaSubformSetDef subformSet => LayoutSubformSet(subformSet, x, ref curY, availW, pageBottom, dataCtx),
            XfaSubformDef subform => LayoutSubform(subform, x, ref curY, availW, pageBottom, dataCtx),
            XfaFieldDef field => LayoutField(field, x, ref curY, availW, pageBottom, dataCtx),
            XfaDrawDef draw => LayoutDraw(draw, x, ref curY, availW, pageBottom),
            _ => 0
        };
    }

    private double LayoutSubformSet(XfaSubformSetDef subformSet, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        double totalH = 0;

        if (subformSet.Relation == "choice" && dataCtx is not null)
        {
            // Data-driven choice: iterate data children in order
            // and instantiate the matching template subform for each.
            totalH = LayoutChoiceSetByData(subformSet, x, ref curY, availW, pageBottom, dataCtx);
        }
        else
        {
            // Ordered: process all children in template order
            foreach (var child in subformSet.Children)
            {
                double h = LayoutElement(child, x, ref curY, availW, pageBottom, dataCtx);
                totalH += h;
            }
        }

        return totalH;
    }

    /// <summary>
    /// For a subformSet with relation="choice", iterate through data children
    /// and instantiate the matching template subform for each data node.
    /// This implements XFA data-driven subform instantiation.
    /// </summary>
    private double LayoutChoiceSetByData(XfaSubformSetDef subformSet, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode dataCtx)
    {
        double totalH = 0;

        // Build a lookup of template children by the data name they match
        var templateByDataName = BuildTemplateNameMap(subformSet.Children);

        // Collect all data children that could match template subforms
        // Navigate to the data scope (e.g., PMSDATA) where the children live
        var dataScope = FindDataScopeForChoice(subformSet, dataCtx);

        if (dataScope is null || dataScope.Children.Count == 0)
        {
            // No data children — fall back to template order for non-repeating items
            foreach (var child in subformSet.Children)
            {
                if (child is XfaSubformDef sub)
                {
                    var childData = ResolveDataContext(sub, dataCtx);
                    if (childData is not null && HasContent(childData, sub))
                    {
                        double h = LayoutSubform(sub, x, ref curY, availW, pageBottom, dataCtx);
                        totalH += h;
                    }
                }
            }
            return totalH;
        }

        // Walk data children in order and instantiate matching template subforms
        int matchCount = 0, missCount = 0;
        var dataChildren = dataScope.Children;
        for (int di = 0; di < dataChildren.Count; di++)
        {
            var dataChild = dataChildren[di];
            XfaSubformDef? matchedTemplate = null;

            // Try to find template subform by data child name
            if (templateByDataName.TryGetValue(dataChild.Name, out var candidates))
            {
                matchedTemplate = candidates;
            }

            if (matchedTemplate is not null)
            {
                matchCount++;
                // Always pass the specific data child as context.
                // For bind match="none" templates in a choice set, the data child
                // is still the relevant context — fields inside use bind ref to
                // reference the specific data element (e.g., h2[*] matches THIS h2).
                XfaDataNode? instanceData = dataChild;

                // Section header keep-with-next: if this is a section header (h2/h1)
                // and the next data sibling is a table section, check whether the header
                // plus the table's first rows fit on the current page. If not, advance
                // to the next page BEFORE rendering the header so both move together.
                // This emulates Adobe's JS-driven keep behavior (resolveNode("break").before).
                if (IsKeepWithNextHeader(dataChild, matchedTemplate, di, dataChildren,
                    templateByDataName, curY, pageBottom, availW))
                {
                    EmitOverflowTrailer(ref curY, pageBottom);
                    AdvanceToNextPage(ref curY, ref pageBottom);
                }

                // Invisible spacer keep-with-next: when a spacer subform (all children invisible,
                // e.g., element_leerzeile) is near the page bottom and the next data child's
                // content would overflow, advance the page BEFORE the spacer so its space
                // appears at the top of the new page (not wasted at the bottom of the current).
                // This matches Adobe's rendering behavior for invisible XFA spacers.
                bool isSpacerSubform = matchedTemplate.Children.Count > 0
                    && matchedTemplate.Children.All(c => c.Presence is "invisible");
                if (isSpacerSubform && curY < pageBottom)
                {
                    // Estimate spacer height
                    double spacerH = EstimateMinChildHeight(matchedTemplate, availW, dataChild);
                    // Find next data child with a matching visible template
                    for (int ndi = di + 1; ndi < dataChildren.Count; ndi++)
                    {
                        var nextDataChild = dataChildren[ndi];
                        if (templateByDataName.TryGetValue(nextDataChild.Name, out var nextTemplate))
                        {
                            bool nextIsInvisible = nextTemplate.Children.Count > 0
                                && nextTemplate.Children.All(c => c.Presence is "invisible");
                            if (!nextIsInvisible)
                            {
                                double nextH = EstimateMinChildHeight(nextTemplate, availW, nextDataChild);
                                if (curY + spacerH + nextH > pageBottom)
                                {
                                    // Next visible element would overflow — move spacer to new page
                                    EmitOverflowTrailer(ref curY, pageBottom);
                                    AdvanceToNextPage(ref curY, ref pageBottom);
                                    EmitOverflowLeader(ref curY, pageBottom);
                                }
                                break;
                            }
                        }
                        else break; // unmatched data child, stop looking
                    }
                }

                double h = LayoutSubformSingleInstance(matchedTemplate, x, ref curY, availW, pageBottom, instanceData);
                totalH += h;
            }
            else
            {
                missCount++;
                if (_verbose)
                    Console.WriteLine($"    [Layout] Unmatched data child: '{dataChild.Name}' in {subformSet.Name}");
            }
        }

        if (_verbose)
            Console.WriteLine($"  [Layout] Choice set '{subformSet.Name}': {matchCount} matched, {missCount} unmatched, scope '{dataScope.Name}' ({dataScope.Children.Count} children)");

        return totalH;
    }

    /// <summary>
    /// Builds a map from data element names to their matching template subforms.
    /// For each template child, extracts the data name it expects to bind to.
    /// </summary>
    private static Dictionary<string, XfaSubformDef> BuildTemplateNameMap(List<XfaElement> children)
    {
        var map = new Dictionary<string, XfaSubformDef>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in children)
        {
            if (child is not XfaSubformDef subform) continue;

            // The data name is typically the subform name or extracted from bind ref
            string? dataName = null;

            if (subform.BindRef is not null)
            {
                // Extract last segment: "$record.PRINTJOB.PMSDATA.block[*]" → "block"
                string normalized = NormalizeBindRef(subform.BindRef);
                int dotIdx = normalized.LastIndexOf('.');
                dataName = dotIdx >= 0 ? normalized[(dotIdx + 1)..] : normalized;
            }

            dataName ??= subform.Name;

            if (dataName is not null && !map.ContainsKey(dataName))
            {
                map[dataName] = subform;
            }
        }

        return map;
    }

    /// <summary>
    /// Finds the data scope node whose children should drive the choice selection.
    /// For bind refs like "$record.PRINTJOB.PMSDATA.block[*]", the scope is "PMSDATA".
    /// </summary>
    private XfaDataNode? FindDataScopeForChoice(XfaSubformSetDef subformSet, XfaDataNode dataCtx)
    {
        // Look at the template children's bind refs to find the parent data scope
        foreach (var child in subformSet.Children)
        {
            if (child is XfaSubformDef sub && sub.BindRef is not null)
            {
                string normalized = NormalizeBindRef(sub.BindRef);
                int dotIdx = normalized.LastIndexOf('.');
                if (dotIdx >= 0)
                {
                    string parentPath = normalized[..dotIdx];
                    var node = dataCtx.Navigate(parentPath);
                    if (node is not null) return node;

                    node = _data.Root.Navigate(parentPath);
                    if (node is not null) return node;
                }
            }
        }

        // Fall back to current data context
        return dataCtx;
    }

    private double LayoutSubform(XfaSubformDef subform, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        if (subform.Presence is "hidden" or "inactive" or "invisible")
            return 0;

        // Handle page break with optional target page area
        if (subform.HasBreakBefore)
        {
            AdvanceToNextPage(ref curY, ref pageBottom, subform.BreakTarget, subform.BreakTargetType);
        }

        // Resolve data context for this subform
        var resolvedData = ResolveDataContext(subform, dataCtx) ?? dataCtx;

        // Get instances for repeating subforms
        var instances = GetDataInstances(subform, dataCtx);

        double totalH = 0;
        double subformW = subform.W ?? availW;
        double subformX = x + (subform.X ?? 0);

        foreach (var instanceData in instances)
        {
            double marginTop = subform.Margin.Top;
            double marginBottom = subform.Margin.Bottom;
            double marginLeft = subform.Margin.Left;
            double marginRight = subform.Margin.Right;

            // keep intact="contentArea": if the subform doesn't fit on the current page,
            // advance to the next page before starting layout. Estimate height from children.
            if (subform.KeepIntact && curY + marginTop < pageBottom)
            {
                double estimatedH = EstimateSubformHeight(subform, subformW - marginLeft - marginRight, instanceData);
                double remaining = pageBottom - (curY + marginTop);
                if (estimatedH > remaining && remaining < pageBottom * 0.5)
                {
                    AdvanceToNextPage(ref curY, ref pageBottom);
                }
            }

            double innerX = subformX + marginLeft;
            double innerW = subformW - marginLeft - marginRight;
            double startY = curY + marginTop;

            double innerY = startY;

            // Save the starting page before children are laid out (children may advance pages)
            int startPage = _currentPage;

            // Push overflow leader/trailer context if this subform defines them (XFA 3.3 Ch.8)
            bool pushedLeader = false;
            bool pushedTrailer = false;
            if (subform.OverflowLeader is not null)
            {
                var leader = FindOverflowLeader(subform.OverflowLeader, subform.Children);
                if (leader is not null)
                {
                    _overflowLeaderStack.Push(new OverflowLeaderContext(leader, innerX, innerW, instanceData));
                    pushedLeader = true;
                }
            }
            if (subform.OverflowTrailer is not null)
            {
                var trailer = FindOverflowLeader(subform.OverflowTrailer, subform.Children);
                if (trailer is not null)
                {
                    _overflowTrailerStack.Push(new OverflowTrailerContext(trailer, innerX, innerW, instanceData));
                    pushedTrailer = true;
                }
            }

            _continuationTopMargin += marginTop;
            switch (subform.Layout)
            {
                case "tb":
                    LayoutTb(subform.Children, innerX, ref innerY, innerW, pageBottom, instanceData);
                    break;

                case "lr-tb":
                    LayoutLrTb(subform.Children, innerX, ref innerY, innerW, pageBottom, instanceData);
                    break;

                case "table":
                    LayoutTable(subform, innerX, ref innerY, innerW, pageBottom, instanceData);
                    break;

                case "row":
                    LayoutRow(subform, innerX, ref innerY, innerW, pageBottom, instanceData);
                    break;

                default:
                    // position layout or unknown - use tb as default
                    LayoutTb(subform.Children, innerX, ref innerY, innerW, pageBottom, instanceData);
                    break;
            }
            _continuationTopMargin -= marginTop;

            if (pushedTrailer)
                _overflowTrailerStack.Pop();
            if (pushedLeader)
                _overflowLeaderStack.Pop();

            double subformH = innerY - startY + marginBottom;
            // Enforce maxH constraint (XFA 3.3 Ch.8)
            if (subform.MaxH.HasValue)
                subformH = Math.Min(subformH, subform.MaxH.Value);

            // Emit subform border/fill on the page where the subform started.
            // If the subform spans multiple pages (children advanced _currentPage),
            // clamp the border height to the remaining space on the starting page.
            int savedPage = _currentPage;
            if (_currentPage != startPage)
            {
                // Subform spans pages — emit border on start page, clipped to page bottom
                _currentPage = startPage;
                double clampedH = pageBottom - (startY - marginTop);
                EmitSubformBorder(subform, subformX, startY - marginTop, subformW, clampedH, dataCtx);
                _currentPage = savedPage;
            }
            else
            {
                EmitSubformBorder(subform, subformX, startY - marginTop, subformW, subformH + marginTop, dataCtx);
            }

            curY = startY + subformH;
            totalH += subformH + marginTop;

            // Handle break after each instance (XFA 3.3 Ch.7b)
            if (subform.HasBreakAfter)
            {
                AdvanceToNextPage(ref curY, ref pageBottom, subform.BreakAfterTarget, subform.BreakAfterTargetType);
            }
        }

        return totalH;
    }

    /// <summary>
    /// Lays out a subform as a single instance with the given data context.
    /// Used by data-driven choice sets where each data child maps to exactly one template instance.
    /// Skips GetDataInstances (the data child IS the instance).
    /// </summary>
    private double LayoutSubformSingleInstance(XfaSubformDef subform, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        if (subform.Presence is "hidden" or "inactive" or "invisible")
            return 0;

        // Handle page break with optional target page area
        if (subform.HasBreakBefore)
        {
            AdvanceToNextPage(ref curY, ref pageBottom, subform.BreakTarget, subform.BreakTargetType);
        }

        double subformW = subform.W ?? availW;
        double subformX = x + (subform.X ?? 0);

        double marginTop = subform.Margin.Top;
        double marginBottom = subform.Margin.Bottom;
        double marginLeft = subform.Margin.Left;
        double marginRight = subform.Margin.Right;

        double innerX = subformX + marginLeft;
        double innerW = subformW - marginLeft - marginRight;
        double startY = curY + marginTop;
        double innerY = startY;

        // Save the starting page before children are laid out
        int startPage = _currentPage;

        // Push overflow leader/trailer context if this subform defines them (XFA 3.3 Ch.8)
        bool pushedLeader = false;
        bool pushedTrailer = false;
        if (subform.OverflowLeader is not null)
        {
            var leader = FindOverflowLeader(subform.OverflowLeader, subform.Children);
            if (leader is not null)
            {
                _overflowLeaderStack.Push(new OverflowLeaderContext(leader, innerX, innerW, dataCtx));
                pushedLeader = true;
            }
        }
        if (subform.OverflowTrailer is not null)
        {
            var trailer = FindOverflowLeader(subform.OverflowTrailer, subform.Children);
            if (trailer is not null)
            {
                _overflowTrailerStack.Push(new OverflowTrailerContext(trailer, innerX, innerW, dataCtx));
                pushedTrailer = true;
            }
        }

        _continuationTopMargin += marginTop;
        switch (subform.Layout)
        {
            case "tb":
                LayoutTb(subform.Children, innerX, ref innerY, innerW, pageBottom, dataCtx);
                break;

            case "lr-tb":
                LayoutLrTb(subform.Children, innerX, ref innerY, innerW, pageBottom, dataCtx);
                break;

            case "table":
                LayoutTable(subform, innerX, ref innerY, innerW, pageBottom, dataCtx);
                break;

            case "row":
                LayoutRow(subform, innerX, ref innerY, innerW, pageBottom, dataCtx);
                break;

            default:
                LayoutTb(subform.Children, innerX, ref innerY, innerW, pageBottom, dataCtx);
                break;
        }
        _continuationTopMargin -= marginTop;

        if (pushedTrailer)
            _overflowTrailerStack.Pop();
        if (pushedLeader)
            _overflowLeaderStack.Pop();

        double contentH = innerY - startY;
        double subformH = contentH + marginBottom;
        // XFA 3.3 Ch.8 Growable Containers: when a data-driven subform has no explicit H/minH
        // and its children produce zero content height, collapse the bottom margin.
        // This targets empty choice-set subforms (e.g., element_komplexer_dmstext with empty
        // data node) where the subform has a subformSet child that found no matching data.
        // Only collapse when data context is an empty leaf node to avoid cascading to
        // structural wrappers (Abstandshalter, Rahmen, block) which share parent data.
        if (contentH <= 0 && !subform.H.HasValue && !subform.MinH.HasValue
            && marginBottom > 0
            && dataCtx is not null && dataCtx.Children.Count == 0
            && subform.Children.Any(c => c is XfaSubformSetDef))
        {
            subformH = 0;
        }
        // Enforce maxH constraint (XFA 3.3 Ch.8)
        if (subform.MaxH.HasValue)
            subformH = Math.Min(subformH, subform.MaxH.Value);

        // Emit subform border/fill on the page where the subform started.
        int savedPage = _currentPage;
        if (_currentPage != startPage)
        {
            _currentPage = startPage;
            double clampedH = pageBottom - (startY - marginTop);
            EmitSubformBorder(subform, subformX, startY - marginTop, subformW, clampedH, dataCtx);
            _currentPage = savedPage;
        }
        else
        {
            EmitSubformBorder(subform, subformX, startY - marginTop, subformW, subformH + marginTop, dataCtx);
        }

        curY = startY + subformH;

        // Handle break after (XFA 3.3 Ch.7b)
        if (subform.HasBreakAfter)
        {
            AdvanceToNextPage(ref curY, ref pageBottom, subform.BreakAfterTarget, subform.BreakAfterTargetType);
        }

        return subformH + marginTop;
    }

    private void LayoutTb(List<XfaElement> children, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // Check if we need a new page
            if (curY >= pageBottom - 1)
            {
                EmitOverflowTrailer(ref curY, pageBottom);
                AdvanceToNextPage(ref curY, ref pageBottom);
                EmitOverflowLeader(ref curY, pageBottom);
            }

            // Keep invisible spacers with next visible sibling:
            // If an invisible field (e.g., element_leerzeile) would be placed near the page bottom
            // and the next visible sibling would overflow to the next page, advance the page BEFORE
            // placing the spacer so its space appears at the top of the new page (not wasted at
            // the bottom of the current page). This matches Adobe's XFA renderer behavior.
            if (child is XfaFieldDef { Presence: "invisible" } invisField)
            {
                double invH = invisField.H ?? invisField.MinH ?? invisField.Margin.Bottom;
                double remaining = pageBottom - curY;
                if (invH < remaining) // spacer fits on current page
                {
                    var nextVisible = FindNextVisibleSibling(children, i + 1);
                    if (nextVisible is not null)
                    {
                        double nextMinH = EstimateMinChildHeight(nextVisible, availW, dataCtx);
                        if (curY + invH + nextMinH > pageBottom)
                        {
                            // Next element would overflow — move spacer to new page
                            EmitOverflowTrailer(ref curY, pageBottom);
                            AdvanceToNextPage(ref curY, ref pageBottom);
                            EmitOverflowLeader(ref curY, pageBottom);
                        }
                    }
                }
            }

            LayoutElement(child, x, ref curY, availW, pageBottom, dataCtx);

            // Enforce keep next="contentArea" (XFA 3.3 Ch.8 Adhesion):
            // When a subform has KeepNext, the next visible sibling must start
            // on the same content area. If not enough room, advance page now.
            if (child is XfaSubformDef { KeepNext: true })
            {
                var nextChild = FindNextVisibleSibling(children, i + 1);
                if (nextChild is not null)
                {
                    double nextMinH = EstimateMinChildHeight(nextChild, availW, dataCtx);
                    double remaining = pageBottom - curY;
                    if (remaining < nextMinH && remaining < pageBottom * 0.5)
                    {
                        EmitOverflowTrailer(ref curY, pageBottom);
                        AdvanceToNextPage(ref curY, ref pageBottom);
                        EmitOverflowLeader(ref curY, pageBottom);
                    }
                }
            }
        }
    }

    private void LayoutLrTb(List<XfaElement> children, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        double curX = x;
        double rowH = 0;
        double rowStartY = curY;

        foreach (var child in children)
        {
            if (child.Presence is "hidden" or "inactive")
            {
                // Still process caption.reserve scripts on hidden fields (e.g., abstand → wert)
                if (child is XfaFieldDef hiddenField && hiddenField.Scripts is { Count: > 0 })
                    ProcessCaptionReserveScripts(hiddenField, dataCtx);
                continue;
            }

            double childW = child.W ?? availW;

            // Wrap to next line if no room
            if (curX + childW > x + availW + 0.5 && curX > x + 0.5)
            {
                curY = rowStartY + rowH;
                curX = x;
                rowH = 0;
                rowStartY = curY;
            }

            // Check page overflow
            if (curY >= pageBottom - 1)
            {
                EmitOverflowTrailer(ref curY, pageBottom);
                AdvanceToNextPage(ref curY, ref pageBottom);
                EmitOverflowLeader(ref curY, pageBottom);
                curX = x;
                rowH = 0;
                rowStartY = curY;
            }

            double tempY = curY;
            // In lr-tb flow, explicit x/y on children should be ignored — positions
            // are determined by the flow engine. Subtract child.X so LayoutField's
            // fieldX = x + (field.X ?? 0) nets out to just curX.
            double childH = LayoutElement(child, curX - (child.X ?? 0), ref tempY, childW, pageBottom, dataCtx);
            if (childH <= 0)
            {
                // Don't override height for fields hidden by HideIfEmpty JS evaluation —
                // LayoutField intentionally returned 0 because the field's script hides it when empty.
                bool isJsHidden = child is XfaFieldDef f && f.HideIfEmpty
                    && string.IsNullOrEmpty(ResolveFieldValue(f, dataCtx));
                if (!isJsHidden)
                    childH = child.H ?? child.MinH ?? 0;
            }

            curX += childW;
            rowH = Math.Max(rowH, childH);
        }

        curY = rowStartY + rowH;
    }

    private void LayoutTable(XfaSubformDef table, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        // Parse column widths
        double[] colWidths = ParseColumnWidths(table.ColumnWidths, availW);

        // Apply data-driven table settings (oDynamicTable pattern):
        // Data nodes like <tabelle_4 width="170mm"> with <defaults><spalteN width="3" horizontal="left"/></defaults>
        // define column widths and alignment that override template defaults.
        var tableData = ResolveDataContext(table, dataCtx) ?? dataCtx;
        var dynSettings = ReadDynamicTableSettings(tableData, colWidths.Length, availW);
        if (dynSettings is not null)
        {
            colWidths = dynSettings.Value.columnWidths;
        }

        foreach (var child in table.Children)
        {
            if (child is XfaSubformDef row && row.Layout == "row")
            {
                // Get instances for repeating rows
                var rowInstances = GetDataInstances(row, dataCtx);
                foreach (var rowData in rowInstances)
                {
                    if (curY >= pageBottom - 1)
                    {
                        EmitOverflowTrailer(ref curY, pageBottom);
                        AdvanceToNextPage(ref curY, ref pageBottom);
                        EmitOverflowLeader(ref curY, pageBottom);
                    }

                    LayoutTableRow(row, x, ref curY, colWidths, pageBottom, rowData,
                        dynSettings?.columnAligns);
                }
            }
            else
            {
                LayoutElement(child, x, ref curY, availW, pageBottom, dataCtx);
            }
        }
    }

    private void LayoutTableRow(XfaSubformDef row, double x, ref double curY,
        double[] colWidths, double pageBottom, XfaDataNode? dataCtx,
        string[]? columnAligns = null)
    {
        double rowH = 0;
        double curX = x;
        int colIdx = 0;

        var resolvedData = ResolveDataContext(row, dataCtx) ?? dataCtx;

        // Read per-cell alignment overrides from data node attributes (oDynamicTable pattern).
        // Cell data nodes may have horizontal="left"/"right"/"center" that override template defaults.
        string[]? cellAligns = null;
        if (columnAligns is not null || resolvedData is not null)
        {
            cellAligns = ReadCellAligns(row, resolvedData, columnAligns);
        }

        // Track cell positions for border emission
        var cellPositions = new List<(double x, double w, XfaBorder border)>();

        foreach (var cell in row.Children)
        {
            if (cell.Presence is "hidden" or "inactive") continue;

            // XFA 3.3 Ch.8 colSpan support (p.330-331):
            // colSpan > 0: cell spans that many columns
            // colSpan = -1: cell spans all remaining columns
            int span = cell.ColSpan;
            if (span == -1)
                span = colWidths.Length - colIdx; // span all remaining
            span = Math.Max(1, Math.Min(span, colWidths.Length - colIdx)); // clamp

            // Compute combined width for spanned columns
            double cellW = 0;
            for (int s = 0; s < span && (colIdx + s) < colWidths.Length; s++)
                cellW += colWidths[colIdx + s];

            // Skip zero-width columns (hidden/conditional columns)
            if (cellW < 0.5)
            {
                colIdx += span;
                continue;
            }

            // Apply data-driven alignment override to field
            var layoutCell = cell;
            if (cellAligns is not null && colIdx < cellAligns.Length && cellAligns[colIdx] is not null
                && cell is XfaFieldDef field)
            {
                layoutCell = field with { Para = field.Para with { HAlign = cellAligns[colIdx] } };
            }

            // Get cell border info
            XfaBorder cellBorder = layoutCell switch
            {
                XfaFieldDef f => f.Border,
                XfaSubformDef s => s.Border,
                _ => new XfaBorder()
            };
            cellPositions.Add((curX, cellW, cellBorder));

            double tempY = curY;
            // In table rows, the column width overrides the field's W attribute
            // (fields often have placeholder widths from the template designer)
            double cellH = LayoutTableCell(layoutCell, curX, ref tempY, cellW, pageBottom, resolvedData);
            double usedH = cellH > 0 ? cellH : (cell.H ?? cell.MinH ?? 4);
            rowH = Math.Max(rowH, usedH);

            curX += cellW;
            colIdx += span;
        }

        double finalRowH = Math.Max(rowH, row.H ?? row.MinH ?? 4);

        // Emit borders for table cells using the final row height.
        foreach (var (cellX, cellW, cellBorder) in cellPositions)
        {
            EmitBorder(cellBorder, cellX, curY, cellW, finalRowH);
        }

        // Emit row-level border
        EmitBorder(row.Border, x, curY, colWidths.Sum(), finalRowH);

        curY += finalRowH;
    }

    /// <summary>
    /// Lays out a table cell, forcing the column width to override the field's template width.
    /// </summary>
    private double LayoutTableCell(XfaElement cell, double x, ref double curY,
        double columnW, double pageBottom, XfaDataNode? dataCtx)
    {
        if (cell is XfaFieldDef field)
        {
            // Force column width by creating a modified field reference
            return LayoutFieldWithWidth(field, x, ref curY, columnW, pageBottom, dataCtx);
        }
        // Non-field cells (subforms, draws) use the standard path
        return LayoutElement(cell, x, ref curY, columnW, pageBottom, dataCtx);
    }

    private void LayoutRow(XfaSubformDef row, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx)
    {
        // Row layout is handled by the parent table
        // If encountered standalone, treat as lr-tb
        LayoutLrTb(row.Children, x, ref curY, availW, pageBottom, dataCtx);
    }

    /// <summary>
    /// Lays out a field using a forced width (for table cells where column width overrides field W).
    /// </summary>
    private double LayoutFieldWithWidth(XfaFieldDef field, double x, ref double curY,
        double forcedW, double pageBottom, XfaDataNode? dataCtx)
    {
        return LayoutField(field, x, ref curY, forcedW, pageBottom, dataCtx, forceWidth: forcedW);
    }

    private double LayoutField(XfaFieldDef field, double x, ref double curY,
        double availW, double pageBottom, XfaDataNode? dataCtx, double? forceWidth = null)
    {
        // Check script-modified presence first (from previous script execution)
        string? effectivePresence = field.Presence;
        string fieldPath = BuildElementPath(field, dataCtx);

        if (_scriptEngine is not null)
        {
            string? scriptPresence = _scriptEngine.GetModifiedPresence(fieldPath);
            if (scriptPresence is not null)
                effectivePresence = scriptPresence;
        }

        if (effectivePresence is "hidden" or "inactive")
        {
            // Even hidden fields may have scripts that affect siblings (e.g., caption.reserve).
            // Process caption.reserve scripts before returning 0.
            ProcessCaptionReserveScripts(field, dataCtx);
            return 0;
        }

        // Skip invisible but still return height so layout flows correctly
        bool invisible = effectivePresence == "invisible";

        // Resolve value and detect rich text formatting
        string text = ResolveFieldValue(field, dataCtx);

        // Execute calculate scripts via Jint if available.
        // Phase 1: only "calculate" scripts (value computation, parent value copy chains).
        // "ready" event scripts that handle visibility are covered by the HideIfEmpty fallback.
        // "ready" layout scripts (xfa.layout.page) are handled in the two-pass layout (Phase 2).
        if (_scriptEngine is not null && field.Scripts is not null)
        {
            foreach (var script in field.Scripts)
            {
                if (script.Event == "calculate")
                {
                    var proxy = BuildFieldProxy(field, dataCtx, text, fieldPath);
                    _scriptEngine.Execute(script.Source, proxy, $"{field.Name}/{script.Event}");
                }
            }

            // Check if a calculate script modified the value
            string? modifiedValue = _scriptEngine.GetModifiedValue(fieldPath);
            if (modifiedValue is not null)
                text = modifiedValue;
        }

        // Track page assignment for layout:ready scripts
        _elementPageMap[fieldPath] = _currentPage;

        // Evaluate JS "hide if empty" pattern as fallback (when script engine didn't handle it):
        // fields with scripts like if (this.rawValue == null) { this.presence = "hidden"; }
        // When the resolved value is empty, treat the field as hidden (0 height).
        if (field.HideIfEmpty && string.IsNullOrEmpty(text))
            return 0;

        // Caption reserve: template default for height estimation (layout), JS override for rendering.
        // XFA form:ready scripts run AFTER layout — they modify visual properties, not layout flow.
        double? captionReserveForLayout = field.CaptionReserve;
        double? captionReserve = GetCaptionReserveOverride(field, dataCtx) ?? field.CaptionReserve;

        var (dataBold, dataItalic) = DetectRichTextFormatting(field, dataCtx);

        // Add spaceBefore from paragraph settings
        if (field.Para.SpaceBefore.HasValue && field.Para.SpaceBefore.Value > 0)
            curY += field.Para.SpaceBefore.Value;

        double fieldW = forceWidth ?? field.W ?? availW;

        // For table cells (forceWidth set), X/Y in the template are relative to the row,
        // not absolute page coordinates. The caller already provides correct x and curY.
        // For flowing content (tb/lr-tb layouts), Y should always advance with curY.
        // Only truly positioned fields (in positioned layout subforms with explicit X/Y
        // and no forceWidth) should use template coordinates as-is.
        double fieldX;
        double fieldY;

        if (forceWidth.HasValue)
        {
            // Table cell: x is already the column position, field.X is relative offset within cell
            fieldX = x + (field.X ?? 0);
            fieldY = curY;
        }
        else
        {
            fieldX = x + (field.X ?? 0);
            fieldY = curY;
        }

        // Compute height: invisible/hidden fields take up layout space but are not rendered.
        // Per XFA 3.3 spec, invisible fields occupy the same space as if visible.
        // Height is derived from font metrics + margins (single-line), clamped to minH/maxH.
        double fieldH;
        if (invisible)
        {
            if (field.H.HasValue)
            {
                fieldH = field.H.Value;
            }
            else
            {
                double fontHeightMm = field.Font.SizePt * 0.3528;
                double singleLineH = fontHeightMm + field.Margin.Top + field.Margin.Bottom;
                fieldH = Math.Max(singleLineH, field.MinH ?? 0);
            }
            if (field.MaxH.HasValue && fieldH > field.MaxH.Value)
                fieldH = field.MaxH.Value;
        }
        else
        {
            // If the field has a caption reserve, the text area is reduced accordingly.
            // For left/right captions: the text only occupies the remaining width.
            // For top/bottom captions: the text only occupies the remaining height (added after estimation).
            double estimateW = fieldW;
            string captionPlacement = field.CaptionPlacement ?? "left";
            bool hasCaptionReserve = captionReserveForLayout.HasValue
                && (field.CaptionText is not null || field.CaptionBindRef is not null);
            if (hasCaptionReserve && captionPlacement is "left" or "right" or "inline")
                estimateW -= captionReserveForLayout!.Value;
            // For rich text fields, compute height from per-paragraph structure instead of plain text.
            // ParseRichTextRuns preserves paragraph boundaries, CSS margins (margin-left, text-indent),
            // and per-paragraph font sizes. This avoids underestimation from:
            // - consolidation losing paragraph structure (ParseRichTextSegments merges same-format paragraphs)
            // - wrong char width factor (SparkasseRg=0.55 vs Arial=0.48)
            // - ignoring hanging indent (margin-left:18pt reduces available width for continuation lines)
            double? richTextH = null;
            var estimateDataNode = ResolveFieldDataNode(field, dataCtx);
            if (estimateDataNode?.RichTextHtml is not null && !field.H.HasValue)
            {
                var richParas = XfaDataParser.ParseRichTextRuns(estimateDataNode.RichTextHtml);
                if (richParas.Count > 0)
                {
                    double rtH = field.Margin.Top + field.Margin.Bottom;
                    double baseW = estimateW - field.Margin.Left - field.Margin.Right;
                    if (baseW <= 0) baseW = estimateW;
                    foreach (var para in richParas)
                    {
                        // Include CSS paragraph margins
                        rtH += (para.MarginTopPt ?? 0) * 0.3528;
                        rtH += (para.MarginBottomPt ?? 0) * 0.3528;

                        // Determine paragraph font from first run (or field default)
                        double paraFontPt = para.Runs.Count > 0 && para.Runs[0].FontSizePt is { } pfs
                            ? pfs : field.Font.SizePt;
                        string? paraFontFamily = para.Runs.Count > 0 ? para.Runs[0].FontFamily : null;

                        // Blank paragraph (spacer)
                        bool isEmpty = para.Runs.Count == 0
                            || (para.Runs.Count == 1 && string.IsNullOrEmpty(para.Runs[0].Text));
                        if (isEmpty)
                        {
                            rtH += paraFontPt * 0.3528 * 1.15;
                            continue;
                        }

                        // Char width estimation (0.48 calibrated for Arial; works well for most fonts)
                        double paraCharW = paraFontPt * 0.3528 * 0.48;
                        if (para.Runs.Any(r => r.Bold)) paraCharW *= 1.08;

                        // Account for paragraph margin-left reducing available width
                        double paraW = baseW;
                        if (para.MarginLeftPt.HasValue)
                            paraW -= para.MarginLeftPt.Value * 0.3528;
                        if (paraW <= 0) paraW = baseW;

                        // Combine all run texts for this paragraph
                        string paraText = string.Join("", para.Runs.Select(r => r.Text));

                        // Word-wrap estimation
                        int lines = 0;
                        foreach (var hardLine in paraText.Split('\n'))
                        {
                            if (hardLine.Length == 0) { lines++; continue; }
                            var words = hardLine.Split(' ');
                            int lineCount = 1;
                            double lineW = 0;
                            foreach (var word in words)
                            {
                                double wordW = word.Length * paraCharW;
                                if (lineW > 0 && lineW + paraCharW + wordW > paraW)
                                { lineCount++; lineW = wordW; }
                                else
                                { lineW += (lineW > 0 ? paraCharW : 0) + wordW; }
                            }
                            lines += lineCount;
                        }
                        rtH += lines * paraFontPt * 0.3528 * 1.15;
                    }
                    richTextH = rtH;
                }
            }
            fieldH = field.H ?? richTextH ?? EstimateTextHeight(text, field.Font, estimateW, field.Margin, field.Para.LineHeight);
            // For top/bottom captions, add the caption reserve to the total height
            if (hasCaptionReserve && captionPlacement is "top" or "bottom" && !field.H.HasValue)
                fieldH += captionReserveForLayout!.Value;
            fieldH = Math.Max(fieldH, field.MinH ?? 0);
            // Enforce maxH constraint (XFA 3.3 Ch.8)
            if (field.MaxH.HasValue && fieldH > field.MaxH.Value)
                fieldH = field.MaxH.Value;
        }

        // Handle page overflow: if this field is taller than remaining space on the page,
        // split it across pages. This handles long rich text blocks.
        double remainingOnPage = pageBottom - fieldY;
        if (!invisible && fieldH > remainingOnPage && remainingOnPage < fieldH * 0.3)
        {
            // Not enough room and would waste > 70% of the field — start on next page
            EmitOverflowTrailer(ref curY, pageBottom);
            AdvanceToNextPage(ref curY, ref pageBottom);
            EmitOverflowLeader(ref curY, pageBottom);
            fieldY = curY;
            remainingOnPage = pageBottom - fieldY;
        }

        // If field is taller than a single page, emit it in page-sized chunks.
        // Uses line-level chunking for accurate page layout (matching old behavior),
        // with per-run formatting within each chunk where applicable.
        if (!invisible && !string.IsNullOrEmpty(text) && fieldH > remainingOnPage)
        {
            double textX = fieldX + field.Margin.Left;
            double textW = fieldW - field.Margin.Left - field.Margin.Right;
            double calcLineH = field.Font.SizePt * 0.3528 * 1.2;
            double paraLineH = field.Para.LineHeight.HasValue ? field.Para.LineHeight.Value * 0.3528 : 0;
            double lineHeightMm = Math.Max(calcLineH, paraLineH);

            // Build fmtLines from rich text paragraphs or plain text
            var dataNode = ResolveFieldDataNode(field, dataCtx);
            double charWidthMm = field.Font.SizePt * 0.3528 * 0.48;
            double charsPerLine = textW / Math.Max(charWidthMm, 0.5);

            var fmtLines = new List<(string Text, bool Bold, bool Italic, bool Underline, double? FontSizePt, string? FontFamily, List<TextRun>? Runs)>();

            List<RichTextParagraph>? paragraphs = null;
            if (dataNode?.RichTextHtml is not null)
            {
                paragraphs = XfaDataParser.ParseRichTextRuns(dataNode.RichTextHtml);
                if (paragraphs.Count == 0) paragraphs = null;
            }

            if (paragraphs is not null)
            {
                foreach (var para in paragraphs)
                {
                    // Build TextRun list and concatenated text for this paragraph
                    var textRuns = new List<TextRun>();
                    var concatText = new System.Text.StringBuilder();
                    bool hasMultipleFormats = para.Runs.Count > 1;
                    foreach (var run in para.Runs)
                    {
                        var runFont = field.Font with
                        {
                            Typeface = run.FontFamily ?? field.Font.Typeface,
                            SizePt = run.FontSizePt ?? field.Font.SizePt,
                            Bold = field.Font.Bold || run.Bold,
                            Italic = field.Font.Italic || run.Italic,
                            Underline = field.Font.Underline || run.Underline
                        };
                        textRuns.Add(new TextRun(run.Text, runFont));
                        concatText.Append(run.Text);
                    }

                    string paraText = concatText.ToString().Trim();
                    if (string.IsNullOrEmpty(paraText))
                    {
                        fmtLines.Add(("", false, false, false, null, null, null));
                        continue;
                    }

                    // Use first content run's formatting as dominant for line-level chunking
                    var firstRun = para.Runs.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Text));
                    if (firstRun is null) firstRun = para.Runs[0];

                    // Pre-wrap long paragraphs into individual visual lines so that page
                    // chunking can split mid-paragraph. Without this, a paragraph that wraps
                    // to N lines is treated as an atomic unit — if only M<N lines fit on the
                    // current page, the remaining N-M lines are lost (clipped by page boundary).
                    double paraFontSizePt = firstRun.FontSizePt ?? field.Font.SizePt;
                    double paraCharWidthMm = paraFontSizePt * 0.3528 * 0.48;
                    if (firstRun.Bold) paraCharWidthMm *= 1.08;
                    double paraCharsPerLine = textW / Math.Max(paraCharWidthMm, 0.5);
                    var wrappedLines = WordWrapText(paraText, paraCharsPerLine);

                    if (wrappedLines.Count <= 1)
                    {
                        // Single line or short paragraph — keep as-is
                        fmtLines.Add((paraText, firstRun.Bold, firstRun.Italic, firstRun.Underline,
                            firstRun.FontSizePt, firstRun.FontFamily,
                            hasMultipleFormats ? textRuns : null));
                    }
                    else
                    {
                        // Multi-line paragraph — add each visual line as a separate fmtLine.
                        // First line gets inline runs (if any), subsequent lines get plain text.
                        for (int wi = 0; wi < wrappedLines.Count; wi++)
                        {
                            fmtLines.Add((wrappedLines[wi], firstRun.Bold, firstRun.Italic, firstRun.Underline,
                                firstRun.FontSizePt, firstRun.FontFamily,
                                wi == 0 && hasMultipleFormats ? textRuns : null));
                        }
                    }
                }
            }

            // Fallback: use plain text lines with overall dataBold/dataItalic
            if (fmtLines.Count == 0)
            {
                foreach (var line in text.Split('\n'))
                    fmtLines.Add((line, dataBold, dataItalic, false, null, null, null));
            }

            double emittedY = fieldY;
            int lineOffset = 0;
            while (lineOffset < fmtLines.Count)
            {
                double availH = pageBottom - emittedY - field.Margin.Top - field.Margin.Bottom;
                int linesPerChunk = Math.Max((int)(availH / lineHeightMm), 1);

                // Collect lines for this page chunk
                int sourceLinesUsed = 0;
                int renderedLines = 0;
                int chunkEnd = lineOffset;

                for (int i = lineOffset; i < fmtLines.Count && renderedLines < linesPerChunk; i++)
                {
                    int wrappedCount = fmtLines[i].Text.Length == 0 ? 1
                        : (int)Math.Ceiling(fmtLines[i].Text.Length / Math.Max(charsPerLine, 1));
                    renderedLines += Math.Max(wrappedCount, 1);
                    sourceLinesUsed++;
                    chunkEnd = i + 1;
                }

                // Emit formatting-grouped sub-chunks within this page chunk
                double subY = emittedY + field.Margin.Top;
                int subStart = lineOffset;
                while (subStart < chunkEnd)
                {
                    // Find run of lines with same formatting
                    bool curBold = fmtLines[subStart].Bold;
                    bool curItalic = fmtLines[subStart].Italic;
                    bool curUnderline = fmtLines[subStart].Underline;
                    double? curFontSize = fmtLines[subStart].FontSizePt;
                    string? curFontFamily = fmtLines[subStart].FontFamily;
                    int subEnd = subStart + 1;
                    while (subEnd < chunkEnd && fmtLines[subEnd].Bold == curBold
                           && fmtLines[subEnd].Italic == curItalic && fmtLines[subEnd].Underline == curUnderline
                           && fmtLines[subEnd].FontSizePt == curFontSize
                           && fmtLines[subEnd].FontFamily == curFontFamily)
                        subEnd++;

                    // Build sub-chunk text and count rendered lines
                    var subChunk = new System.Text.StringBuilder();
                    int subRendered = 0;
                    double subFontSizePt = curFontSize ?? field.Font.SizePt;
                    double subCharWidthMm = subFontSizePt * 0.3528 * 0.48;
                    double subCharsPerLine = textW / Math.Max(subCharWidthMm, 0.5);
                    // Collect TextRuns from single-line sub-chunks for inline rendering
                    List<TextRun>? singleLineRuns = null;
                    for (int i = subStart; i < subEnd; i++)
                    {
                        if (subChunk.Length > 0) subChunk.Append('\n');
                        subChunk.Append(fmtLines[i].Text);
                        int wc = fmtLines[i].Text.Length == 0 ? 1
                            : (int)Math.Ceiling(fmtLines[i].Text.Length / Math.Max(subCharsPerLine, 1));
                        subRendered += Math.Max(wc, 1);
                        // If this is a single-line sub-chunk with inline runs, use them
                        if (subStart + 1 == subEnd && fmtLines[i].Runs is not null)
                            singleLineRuns = fmtLines[i].Runs;
                    }

                    double subLineH = subFontSizePt * 0.3528 * 1.2;
                    double subH = subRendered * subLineH;
                    // Clamp to page boundary to prevent visual overlaps
                    if (subY + subH > pageBottom)
                        subH = Math.Max(pageBottom - subY, 0);

                    if (subChunk.Length > 0)
                    {
                        var subFont = field.Font with
                        {
                            Typeface = curFontFamily ?? field.Font.Typeface,
                            SizePt = subFontSizePt,
                            Bold = field.Font.Bold || curBold,
                            Italic = field.Font.Italic || curItalic,
                            Underline = field.Font.Underline || curUnderline
                        };
                        if (subH > 0)
                        {
                            _items.Add(new LayoutItem(
                                PageIndex: _currentPage,
                                X: textX, Y: subY, W: textW, H: subH,
                                Text: subChunk.ToString(),
                                Font: subFont,
                                Para: field.Para,
                                Rotate: field.Rotate,
                                Runs: singleLineRuns));
                        }
                    }
                    // Advance subY for both content and blank paragraphs —
                    // blank paragraphs don't emit a LayoutItem but still occupy space.
                    subY += subH;
                    subStart = subEnd;
                }
                lineOffset += sourceLinesUsed;
                if (lineOffset < fmtLines.Count)
                {
                    EmitOverflowTrailer(ref curY, pageBottom);
                    AdvanceToNextPage(ref curY, ref pageBottom);
                    EmitOverflowLeader(ref curY, pageBottom);
                    emittedY = curY;
                }
            }

            curY = emittedY + field.Margin.Top + field.Margin.Bottom;

            return fieldH + (field.Para.SpaceBefore ?? 0) + (field.Para.SpaceAfter ?? 0);
        }

        if (!invisible && !string.IsNullOrEmpty(text))
        {
            double textX = fieldX + field.Margin.Left;
            double textY = fieldY + field.Margin.Top;
            double textW = fieldW - field.Margin.Left - field.Margin.Right;
            double textH = fieldH - field.Margin.Top - field.Margin.Bottom;

            // Compute effective font: merge template font with detected rich text formatting
            var renderFont = field.Font;
            if ((dataBold && !renderFont.Bold) || (dataItalic && !renderFont.Italic))
            {
                renderFont = renderFont with
                {
                    Bold = renderFont.Bold || dataBold,
                    Italic = renderFont.Italic || dataItalic
                };
            }

            // Handle caption (static text or data-bound via setProperty)
            string? captionText = field.CaptionText;
            if (captionText is null && field.CaptionBindRef is not null && dataCtx is not null)
            {
                // Resolve caption text from data (setProperty target="caption.value.#text" ref="bezeichner2")
                captionText = ResolveSimpleRef(field.CaptionBindRef, dataCtx);
            }
            if (captionText is not null && captionReserve.HasValue)
            {
                string placement = field.CaptionPlacement ?? "left";
                double captionR = captionReserve.Value;
                var captionFont = field.CaptionFont ?? renderFont;
                var captionPara = field.CaptionPara ?? new XfaPara();

                if (placement is "top")
                {
                    _items.Add(new LayoutItem(
                        PageIndex: _currentPage,
                        X: textX, Y: textY, W: textW, H: captionR,
                        Text: captionText, Font: captionFont, Para: captionPara,
                        Rotate: field.Rotate));
                    textY += captionR;
                    textH -= captionR;
                }
                else if (placement is "bottom")
                {
                    double captionY = textY + textH - captionR;
                    _items.Add(new LayoutItem(
                        PageIndex: _currentPage,
                        X: textX, Y: captionY, W: textW, H: captionR,
                        Text: captionText, Font: captionFont, Para: captionPara,
                        Rotate: field.Rotate));
                    textH -= captionR;
                }
                else if (placement is "right")
                {
                    double captionX = textX + textW - captionR;
                    _items.Add(new LayoutItem(
                        PageIndex: _currentPage,
                        X: captionX, Y: textY, W: captionR, H: textH,
                        Text: captionText, Font: captionFont, Para: captionPara,
                        Rotate: field.Rotate));
                    textW -= captionR;
                }
                else // "left" (default) or "inline"
                {
                    _items.Add(new LayoutItem(
                        PageIndex: _currentPage,
                        X: textX, Y: textY, W: captionR, H: textH,
                        Text: captionText, Font: captionFont, Para: captionPara,
                        Rotate: field.Rotate));
                    textX += captionR;
                    textW -= captionR;
                }
            }

            if (field.Rotate != 0 && field.Rotate != 90)
            {
                // Non-standard rotation - skip for now
            }

            // Try inline-run rich text rendering: each paragraph emits a LayoutItem with
            // per-run TextRun list, preserving inline formatting (bold, font, size) within lines.
            var dataNode = ResolveFieldDataNode(field, dataCtx);
            if (dataNode?.RichTextHtml is not null)
            {
                var paragraphs = XfaDataParser.ParseRichTextRuns(dataNode.RichTextHtml);
                // Use inline-run path only when there are actual formatting differences:
                // - Multiple runs in a paragraph (mixed inline formatting like bold + normal)
                // - Any run with non-default formatting (bold, italic, font size, font family)
                // - Blank paragraph spacers (empty <p> elements)
                // Fields with multiple paragraphs of uniform formatting still use the
                // simple path (matching old behavior to preserve field height estimation).
                bool hasFormatting = false;
                foreach (var p in paragraphs)
                {
                    if (p.Runs.Count > 1) { hasFormatting = true; break; }
                    foreach (var r in p.Runs)
                    {
                        if (r.Bold || r.Italic || r.FontSizePt.HasValue || r.FontFamily is not null
                            || string.IsNullOrEmpty(r.Text))
                        { hasFormatting = true; break; }
                    }
                    if (hasFormatting) break;
                }
                if (hasFormatting)
                {
                    // Compute full rich text content height BEFORE rendering,
                    // so paragraphs like "siehe Anlage" aren't clipped by an
                    // underestimated initial fieldH from EstimateTextHeight.
                    var heightSegments = XfaDataParser.ParseRichTextSegments(dataNode.RichTextHtml);
                    double fullContentH = 0;
                    foreach (var seg2 in heightSegments)
                    {
                        double fs2 = seg2.FontSizePt ?? renderFont.SizePt;
                        double lh2 = fs2 * 0.3528 * 1.2;
                        int lc2 = 1;
                        foreach (char c in seg2.Text)
                            if (c == '\n') lc2++;
                        fullContentH += lc2 * lh2;
                    }
                    double fullH = fullContentH + field.Margin.Top + field.Margin.Bottom;
                    if (fullH > fieldH)
                        fieldH = fullH;

                    double segY = textY;
                    // fieldBottom is the maximum Y the paragraphs can extend to.
                    // Paragraphs beyond this boundary are clamped to prevent visual overlaps.
                    double fieldBottom = fieldY + fieldH;

                    foreach (var para in paragraphs)
                    {
                        // Add margin-top spacing
                        if (para.MarginTopPt.HasValue)
                            segY += para.MarginTopPt.Value * 0.3528;

                        // Build TextRun list for rendering
                        var textRuns = new List<TextRun>();
                        var concatText = new System.Text.StringBuilder();
                        double maxFontSizePt = renderFont.SizePt;
                        foreach (var run in para.Runs)
                        {
                            double runFontSize = run.FontSizePt ?? renderFont.SizePt;
                            if (runFontSize > maxFontSizePt) maxFontSizePt = runFontSize;
                            var runFont = renderFont with
                            {
                                Typeface = run.FontFamily ?? renderFont.Typeface,
                                SizePt = runFontSize,
                                Bold = renderFont.Bold || run.Bold,
                                Italic = renderFont.Italic || run.Italic,
                                Underline = renderFont.Underline || run.Underline
                            };
                            textRuns.Add(new TextRun(run.Text, runFont));
                            concatText.Append(run.Text);
                        }

                        string paraText = concatText.ToString();
                        bool isBlank = string.IsNullOrWhiteSpace(paraText);

                        // Estimate paragraph height for rendering position
                        double paraLineH = maxFontSizePt * 0.3528 * 1.2;
                        double paraH;
                        if (isBlank)
                        {
                            paraH = paraLineH;
                        }
                        else
                        {
                            double charW = maxFontSizePt * 0.3528 * 0.48;
                            double cpl = textW / Math.Max(charW, 0.5);
                            int lc = Math.Max(1, (int)Math.Ceiling(paraText.Length / Math.Max(cpl, 1)));
                            paraH = lc * paraLineH;
                        }

                        // Clamp paragraph to field boundary to prevent visual overlaps
                        if (segY + paraH > fieldBottom)
                            paraH = Math.Max(fieldBottom - segY, 0);

                        if (!isBlank && paraH > 0)
                        {
                            // Convert CSS margin-left and text-indent from pt to mm
                            double marginLeftMm = (para.MarginLeftPt ?? 0) * 0.3528;
                            double textIndentMm = (para.TextIndentPt ?? 0) * 0.3528;

                            _items.Add(new LayoutItem(
                                PageIndex: _currentPage,
                                X: textX, Y: segY, W: textW, H: paraH,
                                Text: paraText,
                                Font: textRuns[0].Font,
                                Para: field.Para,
                                Rotate: field.Rotate,
                                Runs: textRuns,
                                MarginLeftMm: marginLeftMm,
                                TextIndentMm: textIndentMm));
                        }

                        segY += paraH;

                        // Add margin-bottom spacing
                        if (para.MarginBottomPt.HasValue)
                            segY += para.MarginBottomPt.Value * 0.3528;

                        // Stop emitting paragraphs beyond field boundary
                        if (segY >= fieldBottom) break;
                    }

                    // Height already computed before the rendering loop above.
                    goto fieldDone;
                }
            }

            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: textX, Y: textY, W: textW, H: textH,
                Text: text,
                Font: renderFont,
                Para: field.Para,
                Rotate: field.Rotate));
            fieldDone:;
        }

        // Emit border/fill for fields. Skip background fill for fields with no data at all
        // (truly empty instances). Fields with whitespace-only data (like h3 with " ") still
        // get their fill because they're structural spacers in the reference output.
        if (!invisible)
        {
            var fillDataNode = ResolveFieldDataNode(field, dataCtx);
            bool hasNoData = fillDataNode is null;
            if (hasNoData && field.Border.FillColor is not null
                && field.CaptionText is null)
            {
                // Emit stroke border only, no fill
                if (field.Border.Visible && field.Border.Thickness > 0)
                {
                    double thicknessPt = field.Border.Thickness * (72.0 / 25.4);
                    _items.Add(new LayoutItem(
                        PageIndex: _currentPage,
                        X: fieldX, Y: fieldY, W: fieldW, H: fieldH,
                        Text: "",
                        Font: new XfaFont(),
                        Para: new XfaPara(),
                        ItemType: LayoutItemType.Rectangle,
                        StrokeColor: field.Border.StrokeColor,
                        StrokeThicknessPt: thicknessPt));
                }
            }
            else
            {
                EmitBorder(field.Border, fieldX, fieldY, fieldW, fieldH);
            }
        }

        // Always advance curY for flowing content (tb, lr-tb, table cell contexts).
        // The field's template Y is treated as relative, not absolute positioning.
        curY = fieldY + fieldH;
        // Add spaceAfter from paragraph settings
        if (field.Para.SpaceAfter.HasValue && field.Para.SpaceAfter.Value > 0)
            curY += field.Para.SpaceAfter.Value;

        double totalH = fieldH + (field.Para.SpaceBefore ?? 0) + (field.Para.SpaceAfter ?? 0);
        return totalH;
    }

    private double LayoutDraw(XfaDrawDef draw, double x, ref double curY,
        double availW, double pageBottom)
    {
        if (draw.Presence is "hidden" or "inactive")
            return 0;

        // Add spaceBefore for text draws (not decorative)
        bool isDecorative = draw.IsLine || draw.IsRectangle;
        if (!isDecorative && draw.Para.SpaceBefore.HasValue && draw.Para.SpaceBefore.Value > 0)
            curY += draw.Para.SpaceBefore.Value;

        double drawX = x + (draw.X ?? 0);
        // In flowing layouts, use curY as the base position.
        // Static page area draws are rendered separately by XfaPdfWriter.DrawStaticElements.
        double drawY = curY;
        double drawW = draw.W ?? availW;
        double drawH = draw.H ?? draw.MinH ?? (draw.TextValue is not null
            ? EstimateTextHeight(draw.TextValue, draw.Font, drawW, draw.Margin, draw.Para.LineHeight)
            : 0);

        if (draw.IsLine)
        {
            // Line stroke width comes from edge thickness, not draw height
            double strokeThicknessPt = draw.Border.Thickness * (72.0 / 25.4);
            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: drawX, Y: drawY, W: drawW, H: Math.Max(drawH, 0.2),
                Text: "",
                Font: draw.Font,
                Para: draw.Para,
                ItemType: LayoutItemType.Line,
                StrokeThicknessPt: strokeThicknessPt));
            // Lines are decorative overlays — don't advance curY
        }
        else if (draw.IsRectangle)
        {
            double strokeThicknessPt = draw.Border.Thickness * (72.0 / 25.4);
            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: drawX, Y: drawY, W: drawW, H: drawH,
                Text: "",
                Font: draw.Font,
                Para: draw.Para,
                ItemType: LayoutItemType.Rectangle,
                StrokeThicknessPt: strokeThicknessPt));
            // Rectangles are decorative overlays — don't advance curY
        }
        else if (!string.IsNullOrEmpty(draw.TextValue))
        {
            string text = draw.IsRichText
                ? XfaDataParser.StripHtmlToPlainText(draw.TextValue)
                : draw.TextValue;

            // Detect bold/italic from rich text HTML
            var drawFont = draw.Font;
            if (draw.IsRichText)
            {
                bool rtBold = XfaDataParser.IsHtmlBold(draw.TextValue);
                bool rtItalic = XfaDataParser.IsHtmlItalic(draw.TextValue);
                if ((rtBold && !drawFont.Bold) || (rtItalic && !drawFont.Italic))
                {
                    drawFont = drawFont with
                    {
                        Bold = drawFont.Bold || rtBold,
                        Italic = drawFont.Italic || rtItalic
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                _items.Add(new LayoutItem(
                    PageIndex: _currentPage,
                    X: drawX + draw.Margin.Left,
                    Y: drawY + draw.Margin.Top,
                    W: drawW - draw.Margin.Left - draw.Margin.Right,
                    H: Math.Max(drawH, 4),
                    Text: text,
                    Font: drawFont,
                    Para: draw.Para));
            }

            // Text draws participate in the flow
            curY = drawY + drawH;
        }

        return drawH;
    }

    // ===================== Data Resolution =====================

    /// <summary>
    /// Detects bold/italic formatting from rich text HTML data bound to a field.
    /// </summary>
    private (bool Bold, bool Italic) DetectRichTextFormatting(XfaFieldDef field, XfaDataNode? dataCtx)
    {
        // If bind match="none", no data binding - use template font only
        if (field.BindMatch == "none")
            return (false, false);

        // Find the data node for this field
        XfaDataNode? node = null;

        // Try explicit bind ref
        if (field.BindRef is not null)
        {
            string resolvedRef = NormalizeBindRef(field.BindRef);
            node = FindDataNode(resolvedRef, dataCtx);
        }

        // Try by name
        if (node is null && field.Name is not null && dataCtx is not null)
        {
            var children = dataCtx.GetChildren(field.Name);
            if (children.Count > 0)
                node = children[0];
        }

        // Try global lookup
        if (node is null && field.Name is not null && _data.NodesByName.TryGetValue(field.Name, out var nodes) && nodes.Count > 0)
            node = nodes[0];

        if (node is null)
            return (false, false);

        // Check for bold/italic in rich text HTML
        bool bold = false;
        bool italic = false;

        if (node.RichTextHtml is not null)
        {
            bold = XfaDataParser.IsHtmlBold(node.RichTextHtml);
            italic = XfaDataParser.IsHtmlItalic(node.RichTextHtml);
        }

        return (bold, italic);
    }

    /// <summary>
    /// Resolves the data node for a field, returning the node with possible RichTextHtml.
    /// Used to get per-paragraph rich text segments.
    /// </summary>
    private XfaDataNode? ResolveFieldDataNode(XfaFieldDef field, XfaDataNode? dataCtx)
    {
        if (field.BindMatch == "none") return null;

        XfaDataNode? node = null;

        if (field.BindRef is not null)
        {
            string resolvedRef = NormalizeBindRef(field.BindRef);
            node = FindDataNode(resolvedRef, dataCtx);
        }

        if (node is null && field.Name is not null && dataCtx is not null)
        {
            if (string.Equals(field.Name, dataCtx.Name, StringComparison.OrdinalIgnoreCase))
                return dataCtx;

            var children = dataCtx.GetChildren(field.Name);
            if (children.Count > 0) node = children[0];
        }

        if (node is null && field.Name is not null && _data.NodesByName.TryGetValue(field.Name, out var nodes) && nodes.Count > 0)
            node = nodes[0];

        return node;
    }

    /// <summary>
    /// Finds a data node by bind ref path.
    /// </summary>
    private XfaDataNode? FindDataNode(string bindRef, XfaDataNode? dataCtx)
    {
        XfaDataNode? node = null;

        // Self-reference check: if bind ref matches the context node's name
        if (dataCtx is not null)
        {
            string simpleName = bindRef;
            int bracketIdx = simpleName.IndexOf('[');
            if (bracketIdx >= 0) simpleName = simpleName[..bracketIdx];
            int dotIdx = simpleName.LastIndexOf('.');
            if (dotIdx >= 0) simpleName = simpleName[(dotIdx + 1)..];

            if (string.Equals(simpleName, dataCtx.Name, StringComparison.OrdinalIgnoreCase))
                return dataCtx;
        }

        if (dataCtx is not null)
            node = dataCtx.Navigate(bindRef);
        node ??= _data.Root.Navigate(bindRef);
        if (node is null && _data.Root.Children.Count > 0)
            node = _data.Root.Children[0].Navigate(bindRef);
        return node;
    }

    private string ResolveFieldValue(XfaFieldDef field, XfaDataNode? dataCtx)
    {
        // If bind match="none", use static value only
        if (field.BindMatch == "none")
            return field.StaticValue ?? "";

        // Try explicit bind ref first
        if (field.BindRef is not null)
        {
            string resolvedRef = NormalizeBindRef(field.BindRef);
            string? value = ResolveBindRef(resolvedRef, dataCtx);
            if (value is not null) return value;
        }

        // Try matching by field name in current data context
        if (field.Name is not null && dataCtx is not null)
        {
            // Self-reference: if the context node's name matches the field name, use it directly
            if (string.Equals(field.Name, dataCtx.Name, StringComparison.OrdinalIgnoreCase))
            {
                return dataCtx.RichTextHtml is not null
                    ? XfaDataParser.StripHtmlToPlainText(dataCtx.RichTextHtml)
                    : dataCtx.TextValue ?? "";
            }

            var children = dataCtx.GetChildren(field.Name);
            if (children.Count > 0)
            {
                var child = children[0];
                return child.RichTextHtml is not null
                    ? XfaDataParser.StripHtmlToPlainText(child.RichTextHtml)
                    : child.TextValue ?? "";
            }
        }

        // Try global lookup by name
        if (field.Name is not null && _data.NodesByName.TryGetValue(field.Name, out var nodes) && nodes.Count > 0)
        {
            var node = nodes[0];
            return node.RichTextHtml is not null
                ? XfaDataParser.StripHtmlToPlainText(node.RichTextHtml)
                : node.TextValue ?? "";
        }

        return field.StaticValue ?? "";
    }

    /// <summary>
    /// Resolves a simple field name reference against the data context.
    /// Used for setProperty refs like "bezeichner2".
    /// </summary>
    private string? ResolveSimpleRef(string refName, XfaDataNode dataCtx)
    {
        // Try direct child lookup
        var children = dataCtx.GetChildren(refName);
        if (children.Count > 0)
            return children[0].TextValue;

        // Try parent scope (for bind match="none" templates, dataCtx is the parent)
        if (dataCtx.Parent is not null)
        {
            children = dataCtx.Parent.GetChildren(refName);
            if (children.Count > 0)
                return children[0].TextValue;
        }

        // Try global
        if (_data.NodesByName.TryGetValue(refName, out var nodes) && nodes.Count > 0)
            return nodes[0].TextValue;

        return null;
    }

    private string? ResolveBindRef(string bindRef, XfaDataNode? dataCtx)
    {
        // Navigate from the data root or current context
        XfaDataNode? node = null;

        // Self-reference check: if the bind ref (possibly with array index stripped)
        // matches the data context node's own name, return the context itself.
        // This handles choice set templates where bind match="none" on the subform
        // and bind ref="name[*]" on the field — the data context IS the matched node.
        if (dataCtx is not null)
        {
            string simpleName = bindRef;
            int bracketIdx = simpleName.IndexOf('[');
            if (bracketIdx >= 0) simpleName = simpleName[..bracketIdx];
            int dotIdx = simpleName.LastIndexOf('.');
            if (dotIdx >= 0) simpleName = simpleName[(dotIdx + 1)..];

            if (string.Equals(simpleName, dataCtx.Name, StringComparison.OrdinalIgnoreCase))
            {
                node = dataCtx;
                return node.RichTextHtml is not null
                    ? XfaDataParser.StripHtmlToPlainText(node.RichTextHtml)
                    : node.TextValue;
            }
        }

        // Try from current context first
        if (dataCtx is not null)
        {
            node = dataCtx.Navigate(bindRef);
        }

        // Try from data root
        node ??= _data.Root.Navigate(bindRef);

        // Try from top-level data record ($record = first child of root)
        // In XFA, $record refers to the first data element under <xfa:data>
        if (node is null && _data.Root.Children.Count > 0)
        {
            node = _data.Root.Children[0].Navigate(bindRef);
        }

        if (node is null) return null;

        return node.RichTextHtml is not null
            ? XfaDataParser.StripHtmlToPlainText(node.RichTextHtml)
            : node.TextValue;
    }

    private static string NormalizeBindRef(string bindRef)
    {
        // Remove XFA scope prefixes - data navigation starts from current/root data context
        string normalized = bindRef;
        if (normalized.StartsWith("$record.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["$record.".Length..];
        else if (normalized.StartsWith("$.", StringComparison.Ordinal))
            normalized = normalized["$.".Length..];
        else if (normalized.StartsWith("$data.", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["$data.".Length..];

        // Remove array index notation for navigation
        normalized = normalized.Replace("[*]", "").Replace("[0]", "");

        return normalized;
    }

    private XfaDataNode? ResolveDataContext(XfaSubformDef subform, XfaDataNode? parentCtx)
    {
        if (subform.BindMatch == "none")
            return parentCtx;

        if (subform.BindRef is not null)
        {
            string resolvedRef = NormalizeBindRef(subform.BindRef);

            // Remove array notation
            string navRef = resolvedRef.Replace("[*]", "").Replace("[0]", "");

            // Try navigation from parent context
            if (parentCtx is not null)
            {
                var node = parentCtx.Navigate(navRef);
                if (node is not null) return node;
            }

            // Try from root
            var rootNode = _data.Root.Navigate(navRef);
            if (rootNode is not null) return rootNode;

            // Try from top-level data record ($record = first child of root)
            if (_data.Root.Children.Count > 0)
            {
                var recordNode = _data.Root.Children[0].Navigate(navRef);
                if (recordNode is not null) return recordNode;
            }
        }

        // Match by name
        if (subform.Name is not null && parentCtx is not null)
        {
            var children = parentCtx.GetChildren(subform.Name);
            if (children.Count > 0) return children[0];
        }

        return parentCtx;
    }

    private List<XfaDataNode?> GetDataInstances(XfaSubformDef subform, XfaDataNode? parentCtx)
    {
        if (subform.OccurMax == 1 && subform.OccurMin >= 1)
            return new List<XfaDataNode?> { ResolveDataContext(subform, parentCtx) };

        if (subform.BindMatch == "none")
            return new List<XfaDataNode?> { parentCtx };

        // Repeating subform - find all matching data instances
        string? dataName = null;
        if (subform.BindRef is not null)
        {
            string normalized = NormalizeBindRef(subform.BindRef);
            // Get the last segment as the repeating element name
            int dotIdx = normalized.LastIndexOf('.');
            dataName = dotIdx >= 0 ? normalized[(dotIdx + 1)..] : normalized;
        }
        dataName ??= subform.Name;

        if (dataName is not null && parentCtx is not null)
        {
            var dataCtx = parentCtx;

            // If the bind ref has a path prefix, navigate to the parent first
            if (subform.BindRef is not null)
            {
                string normalized = NormalizeBindRef(subform.BindRef);
                int dotIdx = normalized.LastIndexOf('.');
                if (dotIdx >= 0)
                {
                    string parentPath = normalized[..dotIdx];
                    var parentNode = parentCtx.Navigate(parentPath);
                    if (parentNode is not null)
                        dataCtx = parentNode;
                    else
                    {
                        parentNode = _data.Root.Navigate(parentPath);
                        if (parentNode is not null)
                            dataCtx = parentNode;
                    }
                }
            }

            var instances = dataCtx.GetChildren(dataName);
            if (instances.Count > 0)
            {
                var result = new List<XfaDataNode?>();
                int max = subform.OccurMax < 0 ? instances.Count : Math.Min(instances.Count, subform.OccurMax);
                for (int i = 0; i < max; i++)
                    result.Add(instances[i]);
                return result;
            }
        }

        // No data instances found - check if min > 0
        if (subform.OccurMin > 0)
            return new List<XfaDataNode?> { parentCtx };

        return new List<XfaDataNode?>();
    }

    private static bool HasContent(XfaDataNode node, XfaSubformDef subform)
    {
        // A node has content if it has a text value or any children
        if (node.TextValue is not null) return true;
        if (node.RichTextHtml is not null) return true;
        if (node.Children.Count > 0) return true;

        // Check by subform name
        if (subform.Name is not null)
        {
            return node.GetChildren(subform.Name).Count > 0;
        }

        return false;
    }

    // ===================== Anchor Type =====================

    /// <summary>
    /// Adjusts x/y coordinates based on the XFA anchorType attribute.
    /// The anchorType specifies which point of the element is placed at (x, y).
    /// Default is topLeft (no adjustment). XFA 3.3 spec ref-template-spec.
    /// </summary>
    private static void ApplyAnchorType(string? anchorType, double w, double h, ref double x, ref double y)
    {
        if (anchorType is null or "topLeft") return;
        switch (anchorType)
        {
            case "topCenter":
                x -= w / 2;
                break;
            case "topRight":
                x -= w;
                break;
            case "middleLeft":
                y -= h / 2;
                break;
            case "middleCenter":
                x -= w / 2;
                y -= h / 2;
                break;
            case "middleRight":
                x -= w;
                y -= h / 2;
                break;
            case "bottomLeft":
                y -= h;
                break;
            case "bottomCenter":
                x -= w / 2;
                y -= h;
                break;
            case "bottomRight":
                x -= w;
                y -= h;
                break;
        }
    }

    // ===================== Page Management =====================

    private XfaContentArea GetContentArea(int pageIndex)
    {
        if (_pageAreas.Count == 0)
            return new XfaContentArea(20, 20, 170, 257);

        // Look up the page area assigned to this page
        int areaIdx = pageIndex < _pageToAreaMap.Count
            ? _pageToAreaMap[pageIndex]
            : _currentPageAreaIdx;

        if (areaIdx < 0 || areaIdx >= _pageAreas.Count)
            areaIdx = Math.Min(1, _pageAreas.Count - 1);

        return _pageAreas[areaIdx].ContentArea;
    }

    private void AdvanceToNextPage(ref double curY, ref double pageBottom,
        string? breakTarget = null, string? breakTargetType = null)
    {
        if (_verbose) Console.WriteLine($"  [Layout] Page break: page {_currentPage}→{_currentPage+1}, curY={curY:F1}, pageBottom={pageBottom:F1}");
        _currentPage++;

        // Resolve the target page area
        if (breakTarget is not null)
        {
            int targetIdx = ResolvePageAreaIndex(breakTarget, breakTargetType);
            if (targetIdx >= 0)
            {
                _currentPageAreaIdx = targetIdx;
                if (_verbose) Console.WriteLine($"  [Layout] Break to page area: {_pageAreas[targetIdx].Name} (page {_currentPage})");
            }
        }
        else
        {
            // No explicit target: use the continuation page area
            // Continuation = same page area set, second variant (V2)
            // Look for a continuation page area by naming convention
            int continuationIdx = FindContinuationPageArea(_currentPageAreaIdx);
            if (continuationIdx >= 0)
                _currentPageAreaIdx = continuationIdx;
        }

        // Register this page's page area
        while (_pageToAreaMap.Count <= _currentPage)
            _pageToAreaMap.Add(_currentPageAreaIdx);

        var ca = GetContentArea(_currentPage);
        curY = ca.Y + _continuationTopMargin * 0.5;
        pageBottom = ca.Y + ca.H;
    }

    /// <summary>
    /// Resolves a break target name to a page area index.
    /// Targets can be "PageAreaName" or "PageAreaName.ContentAreaName".
    /// </summary>
    private int ResolvePageAreaIndex(string target, string? targetType)
    {
        // Direct page area name match
        if (_pageAreaByName.TryGetValue(target, out int idx))
            return idx;

        // Target may be "PageAreaName.ContentAreaName" — extract the page area part
        int dotIdx = target.IndexOf('.');
        if (dotIdx >= 0)
        {
            string pageAreaName = target[..dotIdx];
            if (_pageAreaByName.TryGetValue(pageAreaName, out int idx2))
                return idx2;
        }

        // Partial name match (target might be embedded in the actual page area name)
        for (int i = 0; i < _pageAreas.Count; i++)
        {
            if (_pageAreas[i].Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Finds the continuation (V2) page area for a given page area.
    /// Convention: DS_V1_SET0 → DS_V2_SET0 (first→continuation).
    /// If already on V2, stay on V2.
    /// </summary>
    private int FindContinuationPageArea(int currentIdx)
    {
        if (currentIdx < 0 || currentIdx >= _pageAreas.Count)
            return -1;

        string currentName = _pageAreas[currentIdx].Name;

        // Try replacing V1 with V2 in the name
        if (currentName.Contains("_V1_", StringComparison.OrdinalIgnoreCase))
        {
            string continuationName = currentName.Replace("_V1_", "_V2_");
            if (_pageAreaByName.TryGetValue(continuationName, out int idx))
                return idx;
        }

        // If already V2 or no naming convention match, stay on current
        return currentIdx;
    }

    /// <summary>
    /// Post-processing: adjusts X positions of content-flow items when different pages
    /// use different content areas. During layout, the X base is set from page 0's content area
    /// and passed by value through the tree. When content overflows to pages with a different
    /// content area X, the items on those pages need their X shifted accordingly.
    /// Only adjusts items emitted during PerformLayoutPass (content items), not page-area statics.
    /// </summary>
    private void AdjustContentAreaXForPageBreaks()
    {
        if (_pageToAreaMap.Count <= 1 || _pageAreas.Count <= 1 || _contentItemCount == 0)
            return;

        double initialCaX = _pageAreas[_pageToAreaMap[0]].ContentArea.X;

        for (int i = 0; i < _contentItemCount && i < _items.Count; i++)
        {
            var item = _items[i];
            int areaIdx = item.PageIndex < _pageToAreaMap.Count
                ? _pageToAreaMap[item.PageIndex]
                : _pageToAreaMap[^1];
            if (areaIdx < 0 || areaIdx >= _pageAreas.Count)
                continue;

            double pageCaX = _pageAreas[areaIdx].ContentArea.X;
            double shift = pageCaX - initialCaX;
            if (Math.Abs(shift) > 0.001)
            {
                _items[i] = item with { X = item.X + shift };
            }
        }
    }

    /// <summary>
    /// Finds an overflow leader subform by name among a list of sibling elements.
    /// Leader subforms are typically presence="hidden" in the template.
    /// </summary>
    private static XfaSubformDef? FindOverflowLeader(string name, List<XfaElement> siblings)
    {
        foreach (var el in siblings)
        {
            if (el is XfaSubformDef sub && string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                return sub;
        }
        return null;
    }

    /// <summary>
    /// Emits the current overflow leader (e.g., repeated table header) after a page advance.
    /// Called immediately after AdvanceToNextPage when an overflow leader is active.
    /// The leader is laid out at the top of the new page, advancing curY past it.
    /// </summary>
    private void EmitOverflowLeader(ref double curY, double pageBottom)
    {
        if (_overflowLeaderStack.Count == 0) return;
        var ctx = _overflowLeaderStack.Peek();
        // Create a copy with presence=null to override "hidden" presence
        var visible = ctx.LeaderTemplate with { Presence = null };
        LayoutSubformSingleInstance(visible, ctx.X, ref curY, ctx.AvailW, pageBottom, ctx.DataCtx);
        if (_verbose) Console.WriteLine($"  [Layout] Emitted overflow leader '{ctx.LeaderTemplate.Name}' on page {_currentPage}");
    }

    /// <summary>
    /// Emits the current overflow trailer (e.g., table footer) before a page advance.
    /// Called immediately before AdvanceToNextPage when an overflow trailer is active.
    /// The trailer is laid out at the bottom of the current page.
    /// </summary>
    private void EmitOverflowTrailer(ref double curY, double pageBottom)
    {
        if (_overflowTrailerStack.Count == 0) return;
        var ctx = _overflowTrailerStack.Peek();
        var visible = ctx.TrailerTemplate with { Presence = null };
        LayoutSubformSingleInstance(visible, ctx.X, ref curY, ctx.AvailW, pageBottom, ctx.DataCtx);
        if (_verbose) Console.WriteLine($"  [Layout] Emitted overflow trailer '{ctx.TrailerTemplate.Name}' on page {_currentPage}");
    }

    private void InjectPageNumbers()
    {
        // Set layout info on the script engine so page() and pageCount() work
        if (_scriptEngine is not null)
        {
            _scriptEngine.SetLayoutInfo(_totalPages, _elementPageMap);
        }

        // Add page number items for each page from page area static elements that have
        // "Seite" or page count references
        for (int p = 0; p < _totalPages; p++)
        {
            if (_pageAreas.Count == 0) continue;
            int areaIdx = p < _pageToAreaMap.Count
                ? _pageToAreaMap[p]
                : (_pageAreas.Count > 1 ? 1 : 0);
            if (areaIdx < 0 || areaIdx >= _pageAreas.Count)
                areaIdx = 0;

            var pageArea = _pageAreas[areaIdx];
            foreach (var staticEl in pageArea.StaticElements)
            {
                if (staticEl is XfaDrawDef draw && draw.IsRichText && draw.TextValue is not null)
                {
                    if (draw.TextValue.Contains("floatingField") || draw.Name == "Seitenzahl" || draw.Name == "SeiteXvonY")
                    {
                        string pageText = $"Seite {p + 1} von {_totalPages}";
                        double dx = draw.X ?? 0;
                        double dy = draw.Y ?? 0;
                        double dw = draw.W ?? 30;
                        double dh = draw.H ?? 5;
                        ApplyAnchorType(draw.AnchorType, dw, dh, ref dx, ref dy);
                        _items.Add(new LayoutItem(
                            PageIndex: p,
                            X: dx + draw.Margin.Left,
                            Y: dy + draw.Margin.Top,
                            W: dw - draw.Margin.Left - draw.Margin.Right,
                            H: dh - draw.Margin.Top - draw.Margin.Bottom,
                            Text: pageText,
                            Font: draw.Font,
                            Para: draw.Para));
                    }
                }

                // Render positioned static fields (absolute X/Y) on each page
                if (staticEl is XfaFieldDef field && field.Y.HasValue && field.X.HasValue
                    && field.Presence is not "hidden" and not "inactive" and not "invisible")
                {
                    string text = ResolveFieldValue(field, _data.Root);

                    // Execute layout:ready scripts for page area fields (e.g., page number fields)
                    if (_scriptEngine is not null && field.Scripts is not null)
                    {
                        foreach (var script in field.Scripts)
                        {
                            if (script.Activity == "ready" && script.Source.Contains("xfa.layout"))
                            {
                                // Build a proxy with the current page number
                                string fieldPath = $"pageArea.{pageArea.Name}.{field.Name}.p{p}";
                                var proxy = new XfaElementProxy(
                                    _scriptEngine,
                                    fieldPath,
                                    field.Name,
                                    text,
                                    field.Presence ?? "visible",
                                    null, field, null);

                                // Override the page lookup for this specific element
                                _elementPageMap[field.Name ?? ""] = p;

                                _scriptEngine.Execute(script.Source, proxy, $"{field.Name}/layout:ready[p{p}]");

                                string? modifiedValue = _scriptEngine.GetModifiedValue(fieldPath);
                                if (modifiedValue is not null)
                                    text = modifiedValue;
                            }
                            else if (script.Event == "calculate")
                            {
                                string fieldPath = $"pageArea.{pageArea.Name}.{field.Name}.p{p}.calc";
                                var proxy = new XfaElementProxy(
                                    _scriptEngine,
                                    fieldPath,
                                    field.Name,
                                    text,
                                    field.Presence ?? "visible",
                                    null, field, null);

                                _scriptEngine.Execute(script.Source, proxy, $"{field.Name}/calculate[p{p}]");

                                string? modifiedValue = _scriptEngine.GetModifiedValue(fieldPath);
                                if (modifiedValue is not null)
                                    text = modifiedValue;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        double fx = field.X.Value;
                        double fy = field.Y.Value;
                        // Use content area width as fallback when field has no explicit width
                        double defaultW = pageArea.ContentArea.W + pageArea.ContentArea.X - fx;
                        double outerW = field.W ?? defaultW;
                        double outerH = field.H ?? 5;
                        // Apply anchor type adjustment before margins
                        ApplyAnchorType(field.AnchorType, outerW, outerH, ref fx, ref fy);
                        fx += field.Margin.Left;
                        fy += field.Margin.Top;
                        double fw = outerW - field.Margin.Left - field.Margin.Right;
                        double fh = outerH - field.Margin.Top - field.Margin.Bottom;

                        // Handle caption with placement support
                        if (field.CaptionText is not null && field.CaptionReserve.HasValue)
                        {
                            string cp = field.CaptionPlacement ?? "left";
                            double cr = field.CaptionReserve.Value;
                            var cFont = field.CaptionFont ?? field.Font;
                            var cPara = field.CaptionPara ?? new XfaPara();
                            if (cp is "right")
                            {
                                _items.Add(new LayoutItem(PageIndex: p,
                                    X: fx + fw - cr, Y: fy, W: cr, H: fh,
                                    Text: field.CaptionText, Font: cFont, Para: cPara, Rotate: field.Rotate));
                                fw -= cr;
                            }
                            else if (cp is "top")
                            {
                                _items.Add(new LayoutItem(PageIndex: p,
                                    X: fx, Y: fy, W: fw, H: cr,
                                    Text: field.CaptionText, Font: cFont, Para: cPara, Rotate: field.Rotate));
                                fy += cr; fh -= cr;
                            }
                            else if (cp is "bottom")
                            {
                                _items.Add(new LayoutItem(PageIndex: p,
                                    X: fx, Y: fy + fh - cr, W: fw, H: cr,
                                    Text: field.CaptionText, Font: cFont, Para: cPara, Rotate: field.Rotate));
                                fh -= cr;
                            }
                            else // left (default)
                            {
                                _items.Add(new LayoutItem(PageIndex: p,
                                    X: fx, Y: fy, W: cr, H: fh,
                                    Text: field.CaptionText, Font: cFont, Para: cPara, Rotate: field.Rotate));
                                fx += cr; fw -= cr;
                            }
                        }

                        _items.Add(new LayoutItem(
                            PageIndex: p,
                            X: fx, Y: fy, W: fw, H: fh,
                            Text: text,
                            Font: field.Font,
                            Para: field.Para,
                            Rotate: field.Rotate));
                    }
                }

                // Render positioned subforms from page areas (headers, footers)
                if (staticEl is XfaSubformDef subform)
                {
                    RenderPageAreaSubform(subform, p);
                }
            }
        }
    }

    /// <summary>
    /// Renders a subform from a page area's static elements onto a specific page.
    /// Handles both absolutely positioned fields and lr-tb flowing fields.
    /// </summary>
    private void RenderPageAreaSubform(XfaSubformDef subform, int pageIndex)
    {
        // Resolve data context for this subform
        XfaDataNode? dataCtx = subform.BindMatch == "none"
            ? _data.Root
            : ResolveDataContext(subform, _data.Root);

        double baseX = subform.X ?? 0;
        double baseY = subform.Y ?? 0;
        double availW = subform.W ?? 178;
        double subH = subform.H ?? subform.MinH ?? 0;
        // Apply anchor type on the subform itself (e.g., Impressum with anchorType="bottomLeft")
        ApplyAnchorType(subform.AnchorType, availW, subH, ref baseX, ref baseY);

        // lr-tb flow tracking for non-positioned children
        double curX = 0;
        double curY = 0;
        double rowH = 0;

        foreach (var child in subform.Children)
        {
            if (child.Presence is "hidden" or "inactive" or "invisible") continue;

            double childW = child.W ?? 0;
            double childH = child.H ?? 5;

            if (child is XfaFieldDef f && f.Presence is not "hidden" and not "inactive" and not "invisible")
            {
                string text = ResolveFieldValue(f, dataCtx);

                // Execute scripts on page area fields (page numbers, date fields, etc.)
                if (_scriptEngine is not null && f.Scripts is not null)
                {
                    foreach (var script in f.Scripts)
                    {
                        string fieldPath = $"pageAreaSub.{subform.Name}.{f.Name}.p{pageIndex}";
                        if (script.Activity == "ready" && script.Source.Contains("xfa.layout"))
                        {
                            var proxy = new XfaElementProxy(
                                _scriptEngine, fieldPath, f.Name, text,
                                f.Presence ?? "visible", null, f, null);
                            _elementPageMap[f.Name ?? ""] = pageIndex;
                            _scriptEngine.Execute(script.Source, proxy, $"{f.Name}/layout:ready[p{pageIndex}]");
                            string? modVal = _scriptEngine.GetModifiedValue(fieldPath);
                            if (modVal is not null) text = modVal;
                        }
                        else if (script.Event == "calculate")
                        {
                            string calcPath = fieldPath + ".calc";
                            var proxy = new XfaElementProxy(
                                _scriptEngine, calcPath, f.Name, text,
                                f.Presence ?? "visible", null, f, null);
                            _scriptEngine.Execute(script.Source, proxy, $"{f.Name}/calculate[p{pageIndex}]");
                            string? modVal = _scriptEngine.GetModifiedValue(calcPath);
                            if (modVal is not null) text = modVal;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(text) || f.CaptionText is not null)
                {
                    double fx, fy, fw, fh;

                    double outerFw = f.W ?? childW;
                    double outerFh = f.H ?? childH;

                    if (f.X.HasValue && f.Y.HasValue)
                    {
                        // Absolutely positioned within subform
                        fx = baseX + f.X.Value;
                        fy = baseY + f.Y.Value;
                        ApplyAnchorType(f.AnchorType, outerFw, outerFh, ref fx, ref fy);
                        fx += f.Margin.Left;
                        fy += f.Margin.Top;
                    }
                    else
                    {
                        // lr-tb flow position
                        if (subform.Layout is "lr-tb" or "rl-tb" && curX + childW > availW && curX > 0)
                        {
                            curY += rowH;
                            curX = 0;
                            rowH = 0;
                        }
                        fx = baseX + curX + f.Margin.Left;
                        fy = baseY + curY + f.Margin.Top;
                    }

                    fw = outerFw - f.Margin.Left - f.Margin.Right;
                    fh = outerFh - f.Margin.Top - f.Margin.Bottom;

                    // Handle caption with placement support
                    if (f.CaptionText is not null && f.CaptionReserve.HasValue)
                    {
                        string cp = f.CaptionPlacement ?? "left";
                        double cr = f.CaptionReserve.Value;
                        var cFont = f.CaptionFont ?? f.Font;
                        var cPara = f.CaptionPara ?? new XfaPara();
                        if (cp is "right")
                        {
                            _items.Add(new LayoutItem(PageIndex: pageIndex,
                                X: fx + fw - cr, Y: fy, W: cr, H: fh,
                                Text: f.CaptionText, Font: cFont, Para: cPara, Rotate: f.Rotate));
                            fw -= cr;
                        }
                        else if (cp is "top")
                        {
                            _items.Add(new LayoutItem(PageIndex: pageIndex,
                                X: fx, Y: fy, W: fw, H: cr,
                                Text: f.CaptionText, Font: cFont, Para: cPara, Rotate: f.Rotate));
                            fy += cr; fh -= cr;
                        }
                        else if (cp is "bottom")
                        {
                            _items.Add(new LayoutItem(PageIndex: pageIndex,
                                X: fx, Y: fy + fh - cr, W: fw, H: cr,
                                Text: f.CaptionText, Font: cFont, Para: cPara, Rotate: f.Rotate));
                            fh -= cr;
                        }
                        else // left (default)
                        {
                            _items.Add(new LayoutItem(PageIndex: pageIndex,
                                X: fx, Y: fy, W: cr, H: fh,
                                Text: f.CaptionText, Font: cFont, Para: cPara, Rotate: f.Rotate));
                            fx += cr; fw -= cr;
                        }
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        _items.Add(new LayoutItem(
                            PageIndex: pageIndex,
                            X: fx, Y: fy, W: fw, H: fh,
                            Text: text,
                            Font: f.Font,
                            Para: f.Para,
                            Rotate: f.Rotate));
                    }
                }
            }

            // Recurse into nested subforms within page area
            if (child is XfaSubformDef nestedSubform)
            {
                RenderPageAreaSubform(nestedSubform, pageIndex);
            }

            if (!child.X.HasValue || !child.Y.HasValue)
            {
                curX += childW;
                rowH = Math.Max(rowH, childH);
            }
        }
    }

    // ===================== Border Emission =====================

    /// <summary>
    /// Emits borders for subforms. Flow containers (tb, lr-tb) only render fill backgrounds
    /// and borders with explicit edge colors. Table/row subforms and subforms with fills always render.
    /// This prevents structural wrapper subforms from rendering unwanted border rectangles.
    /// </summary>
    private void EmitSubformBorder(XfaSubformDef subform, double x, double y, double w, double h,
        XfaDataNode? dataCtx = null)
    {
        if (w <= 0 || h <= 0) return;
        var border = subform.Border;
        bool borderVisible = border.Visible;
        double borderThickness = border.Thickness;

        // Simulate JavaScript-driven borders: XFA forms use a common pattern where a
        // hidden "inkl_rahmen" checkbox controls border visibility on a sibling "Rahmen"
        // subform via JS: if (inkl_rahmen != 1) Rahmen.borderWidth = "0pt".
        // Since we can't execute JS, check the data directly.
        if (!borderVisible && subform.Name == "Rahmen" && dataCtx is not null)
        {
            var inklRahmen = dataCtx.GetChildren("inkl_rahmen");
            if (inklRahmen.Count > 0 && inklRahmen[0].TextValue == "1")
            {
                borderVisible = true;
                if (borderThickness <= 0) borderThickness = 0.2; // default XFA border thickness
            }
        }


        // Always emit fill background
        if (border.FillColor is not null)
        {
            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: x, Y: y, W: w, H: h,
                Text: "",
                Font: new XfaFont(),
                Para: new XfaPara(),
                ItemType: LayoutItemType.FilledRectangle,
                FillColor: border.FillColor));
        }

        // Render stroked border if visible. Subforms without a <border> element get
        // Visible=false by default, so structural wrappers won't render unwanted borders.
        // Only subforms with an explicit visible <edge> in their <border> get Visible=true,
        // or JS-simulated borders are active (e.g., Rahmen with inkl_rahmen=1).
        if (borderVisible && borderThickness > 0)
        {
            double thicknessPt = borderThickness * (72.0 / 25.4);
            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: x, Y: y, W: w, H: h,
                Text: "",
                Font: new XfaFont(),
                Para: new XfaPara(),
                ItemType: LayoutItemType.Rectangle,
                StrokeColor: border.StrokeColor,
                StrokeThicknessPt: thicknessPt));
        }
    }

    /// <summary>
    /// Emits fill and/or stroke rectangles for a border definition.
    /// Fill is emitted first (behind content), stroke is emitted second (on top).
    /// </summary>
    private void EmitBorder(XfaBorder border, double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;

        // Emit fill background
        if (border.FillColor is not null)
        {
            _items.Add(new LayoutItem(
                PageIndex: _currentPage,
                X: x, Y: y, W: w, H: h,
                Text: "",
                Font: new XfaFont(),
                Para: new XfaPara(),
                ItemType: LayoutItemType.FilledRectangle,
                FillColor: border.FillColor));
        }

        // Emit stroked border
        if (border.Visible && border.Thickness > 0)
        {
            double thicknessPt = border.Thickness * (72.0 / 25.4);
            bool allEdges = border.TopEdge && border.RightEdge && border.BottomEdge && border.LeftEdge;

            if (allEdges)
            {
                // All 4 edges visible — draw full rectangle
                _items.Add(new LayoutItem(
                    PageIndex: _currentPage,
                    X: x, Y: y, W: w, H: h,
                    Text: "",
                    Font: new XfaFont(),
                    Para: new XfaPara(),
                    ItemType: LayoutItemType.Rectangle,
                    StrokeColor: border.StrokeColor,
                    StrokeThicknessPt: thicknessPt));
            }
            else
            {
                // Partial border — draw individual edge lines
                if (border.TopEdge)
                    EmitLine(x, y, w, thicknessPt, border.StrokeColor);
                if (border.BottomEdge)
                    EmitLine(x, y + h, w, thicknessPt, border.StrokeColor);
                if (border.LeftEdge)
                    EmitVLine(x, y, h, thicknessPt, border.StrokeColor);
                if (border.RightEdge)
                    EmitVLine(x + w, y, h, thicknessPt, border.StrokeColor);
            }
        }
    }

    private void EmitLine(double x, double y, double w, double thicknessPt, string? strokeColor)
    {
        _items.Add(new LayoutItem(
            PageIndex: _currentPage,
            X: x, Y: y, W: w, H: 0.2,
            Text: "",
            Font: new XfaFont(),
            Para: new XfaPara(),
            ItemType: LayoutItemType.Line,
            StrokeColor: strokeColor,
            StrokeThicknessPt: thicknessPt));
    }

    private void EmitVLine(double x, double y, double h, double thicknessPt, string? strokeColor)
    {
        _items.Add(new LayoutItem(
            PageIndex: _currentPage,
            X: x, Y: y, W: 0.2, H: h,
            Text: "",
            Font: new XfaFont(),
            Para: new XfaPara(),
            ItemType: LayoutItemType.Line,
            StrokeColor: strokeColor,
            StrokeThicknessPt: thicknessPt));
    }

    // ===================== Script Execution Helpers =====================

    /// <summary>
    /// Builds a unique path for an element used as a key for script modifications.
    /// </summary>
    private static string BuildElementPath(XfaElement element, XfaDataNode? dataCtx)
    {
        string name = element.Name ?? "anon";
        string dataPath = dataCtx?.Name ?? "root";
        // Use a combination of template name + data context to handle repeated elements
        return $"{dataPath}.{name}";
    }

    /// <summary>
    /// Creates a proxy for a field element that Jint scripts can interact with.
    /// </summary>
    private XfaElementProxy BuildFieldProxy(XfaFieldDef field, XfaDataNode? dataCtx,
        string resolvedValue, string fieldPath)
    {
        // Check cache first
        if (_proxyCache.TryGetValue(fieldPath, out var cached))
        {
            // Update the value in case data resolution changed it
            cached.rawValue = resolvedValue;
            return cached;
        }

        // Build parent proxy chain for navigation (this.parent.parent.kopfzeile.xxx.rawValue)
        XfaElementProxy? parentProxy = null;
        if (dataCtx?.Parent is not null)
        {
            parentProxy = BuildDataNodeProxy(dataCtx.Parent, fieldPath + ".$parent", 0);
        }

        var proxy = new XfaElementProxy(
            _scriptEngine!,
            fieldPath,
            field.Name,
            resolvedValue,
            field.Presence ?? "visible",
            dataCtx is not null ? FindDataNodeForField(field, dataCtx) : null,
            field,
            parentProxy);

        _proxyCache[fieldPath] = proxy; // Cache before adding children

        // Add sibling/child data nodes as shallow navigable children
        if (dataCtx is not null)
        {
            foreach (var sibling in dataCtx.Children)
            {
                if (!string.IsNullOrEmpty(sibling.Name))
                {
                    string childPath = $"{fieldPath}.{sibling.Name}";
                    if (!_proxyCache.ContainsKey(childPath))
                    {
                        var childProxy = new XfaElementProxy(
                            _scriptEngine!,
                            childPath,
                            sibling.Name,
                            sibling.TextValue,
                            "visible",
                            sibling,
                            null,
                            proxy);
                        _proxyCache[childPath] = childProxy;
                        proxy.AddChild(sibling.Name, childProxy);
                    }
                    else
                    {
                        proxy.AddChild(sibling.Name, _proxyCache[childPath]);
                    }
                }
            }
        }

        // Add caption proxy if field has a caption
        if (field.CaptionReserve.HasValue)
        {
            proxy.caption = new XfaCaptionProxy(field.CaptionReserve.Value);
        }

        return proxy;
    }

    /// <summary>
    /// Builds a proxy for a data node, used for parent chain navigation.
    /// Limited to a max depth to avoid stack overflow on deep data trees.
    /// Children are built lazily (only direct children, not recursive).
    /// </summary>
    private XfaElementProxy BuildDataNodeProxy(XfaDataNode dataNode, string basePath, int depth = 0)
    {
        if (_proxyCache.TryGetValue(basePath, out var cached))
            return cached;

        // Limit parent chain depth to prevent stack overflow
        XfaElementProxy? parentProxy = null;
        if (dataNode.Parent is not null && depth < 10)
        {
            string parentPath = basePath.Contains(".$parent")
                ? basePath + ".$parent"
                : basePath + ".$parent";
            parentProxy = BuildDataNodeProxy(dataNode.Parent, parentPath, depth + 1);
        }

        var proxy = new XfaElementProxy(
            _scriptEngine!,
            basePath,
            dataNode.Name,
            dataNode.TextValue,
            "visible",
            dataNode,
            null,
            parentProxy);

        _proxyCache[basePath] = proxy; // Cache before adding children to prevent cycles

        // Add only direct children (non-recursive) for property navigation
        foreach (var child in dataNode.Children)
        {
            if (!string.IsNullOrEmpty(child.Name))
            {
                string childPath = $"{basePath}.{child.Name}";
                if (!_proxyCache.ContainsKey(childPath))
                {
                    // Create a shallow child proxy (no recursion into grandchildren)
                    var childProxy = new XfaElementProxy(
                        _scriptEngine!,
                        childPath,
                        child.Name,
                        child.TextValue,
                        "visible",
                        child,
                        null,
                        proxy); // Parent is this proxy
                    _proxyCache[childPath] = childProxy;
                    proxy.AddChild(child.Name, childProxy);
                }
                else
                {
                    proxy.AddChild(child.Name, _proxyCache[childPath]);
                }
            }
        }

        return proxy;
    }

    /// <summary>
    /// Find the data node that corresponds to a field (for script proxy binding).
    /// </summary>
    private XfaDataNode? FindDataNodeForField(XfaFieldDef field, XfaDataNode dataCtx)
    {
        if (field.Name is null) return null;

        // Try direct child
        var children = dataCtx.GetChildren(field.Name);
        if (children.Count > 0) return children[0];

        // Try global
        if (_data.NodesByName.TryGetValue(field.Name, out var nodes) && nodes.Count > 0)
            return nodes[0];

        return null;
    }

    /// <summary>
    /// Counts total scripts across all elements recursively (diagnostic).
    /// </summary>
    private static int CountScriptsRecursive(XfaElement element)
    {
        int count = 0;
        if (element is XfaFieldDef f && f.Scripts is not null) count += f.Scripts.Count;
        if (element is XfaSubformDef s)
        {
            if (s.Scripts is not null) count += s.Scripts.Count;
            foreach (var c in s.Children) count += CountScriptsRecursive(c);
        }
        if (element is XfaSubformSetDef ss)
            foreach (var c in ss.Children) count += CountScriptsRecursive(c);
        return count;
    }

    /// <summary>
    /// Recursively register all named scripts from subform variables blocks.
    /// </summary>
    private void RegisterNamedScriptsRecursive(XfaSubformDef subform)
    {
        if (subform.NamedScripts is not null)
        {
            foreach (var ns in subform.NamedScripts)
            {
                _scriptEngine!.RegisterNamedScript(ns.Name, ns.Source);
            }
        }

        foreach (var child in subform.Children)
        {
            if (child is XfaSubformDef childSubform)
                RegisterNamedScriptsRecursive(childSubform);
        }
    }

    /// <summary>
    /// Check if any element in the tree has layout:ready scripts.
    /// </summary>
    private static bool HasLayoutReadyScripts(XfaSubformDef subform)
    {
        if (subform.Scripts is not null)
        {
            foreach (var s in subform.Scripts)
            {
                if (s.Activity == "ready" && s.Source.Contains("xfa.layout"))
                    return true;
            }
        }

        foreach (var child in subform.Children)
        {
            if (child is XfaFieldDef field && field.Scripts is not null)
            {
                foreach (var s in field.Scripts)
                {
                    if (s.Activity == "ready" && s.Source.Contains("xfa.layout"))
                        return true;
                }
            }
            else if (child is XfaSubformDef childSubform)
            {
                if (HasLayoutReadyScripts(childSubform))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Execute layout:ready scripts after the first layout pass.
    /// These scripts typically set page numbers: this.rawValue = xfa.layout.page(this)
    /// </summary>
    private void ExecuteLayoutReadyScripts(XfaElement element, XfaDataNode? dataCtx)
    {
        if (element is XfaFieldDef field && field.Scripts is not null)
        {
            string fieldPath = BuildElementPath(field, dataCtx);
            string text = ResolveFieldValue(field, dataCtx);

            foreach (var script in field.Scripts)
            {
                if (script.Activity == "ready" && script.Source.Contains("xfa.layout"))
                {
                    var proxy = BuildFieldProxy(field, dataCtx, text, fieldPath);
                    _scriptEngine!.Execute(script.Source, proxy, $"{field.Name}/layout:ready");
                }
            }
        }
        else if (element is XfaSubformDef subform)
        {
            // Execute subform-level layout:ready scripts
            if (subform.Scripts is not null)
            {
                foreach (var script in subform.Scripts)
                {
                    if (script.Activity == "ready" && script.Source.Contains("xfa.layout"))
                    {
                        string path = BuildElementPath(subform, dataCtx);
                        var proxy = new XfaElementProxy(
                            _scriptEngine!, path, subform.Name, null,
                            subform.Presence ?? "visible", null, subform, null);
                        _scriptEngine!.Execute(script.Source, proxy, $"{subform.Name}/layout:ready");
                    }
                }
            }

            // Recurse into children
            var resolvedData = ResolveDataContext(subform, dataCtx) ?? dataCtx;
            foreach (var child in subform.Children)
            {
                ExecuteLayoutReadyScripts(child, resolvedData);
            }
        }
        else if (element is XfaSubformSetDef subformSet)
        {
            foreach (var child in subformSet.Children)
            {
                ExecuteLayoutReadyScripts(child, dataCtx);
            }
        }
    }

    // ===================== Caption Reserve Overrides =====================

    /// <summary>
    /// Regex to extract target field name from caption.reserve scripts.
    /// Matches patterns like: this.parent.wert.caption.reserve = this.rawValue
    /// Also matches: this.parent.wert1.caption.reserve = this.rawValue
    /// </summary>
    private static readonly Regex CaptionReserveRegex = new(
        @"this\.parent\.(\w+)\.caption\.reserve\s*=\s*this\.rawValue",
        RegexOptions.Compiled);

    /// <summary>
    /// Process scripts on hidden fields that set caption.reserve on sibling fields.
    /// Pattern: "this.parent.FIELDNAME.caption.reserve = this.rawValue"
    /// The hidden field's data value (e.g., "25mm") is parsed and stored as an override.
    /// </summary>
    private void ProcessCaptionReserveScripts(XfaFieldDef field, XfaDataNode? dataCtx)
    {
        if (field.Scripts is null || field.Scripts.Count == 0) return;

        foreach (var script in field.Scripts)
        {
            var match = CaptionReserveRegex.Match(script.Source);
            if (!match.Success) continue;

            string targetFieldName = match.Groups[1].Value;

            // Resolve the hidden field's data value (e.g., "25mm", "35mm")
            string rawValue = ResolveFieldValue(field, dataCtx);
            if (string.IsNullOrEmpty(rawValue)) continue;

            // Parse the mm value
            double reserveMm = ParseMmValue(rawValue);
            if (reserveMm <= 0) continue;

            // Build a key that matches the target field in the same data context.
            // Use the data context's object reference hash to make keys instance-specific.
            // Different data instances of the same template (e.g., multiple zeile_zweispaltig)
            // may have different abstand values and must not overwrite each other.
            int ctxHash = dataCtx is not null
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dataCtx)
                : 0;
            string key = $"{ctxHash}.{targetFieldName}";
            _captionReserveOverrides[key] = reserveMm;
        }
    }

    /// <summary>
    /// Gets the caption reserve override for a field, if one was set by a sibling's script.
    /// </summary>
    private double? GetCaptionReserveOverride(XfaFieldDef field, XfaDataNode? dataCtx)
    {
        if (_captionReserveOverrides.Count == 0 || field.Name is null) return null;

        // Use the same hash-based key as ProcessCaptionReserveScripts.
        // The hidden abstand field and this target field share the same dataCtx
        // (siblings in the same subform's lr-tb layout).
        int ctxHash = dataCtx is not null
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dataCtx)
            : 0;
        string key = $"{ctxHash}.{field.Name}";
        if (_captionReserveOverrides.TryGetValue(key, out double val))
            return val;

        return null;
    }

    /// <summary>
    /// Parses a measurement string like "25mm" into a double value in mm.
    /// </summary>
    private static double ParseMmValue(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(trimmed[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double mm))
                return mm;
        }
        else if (trimmed.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(trimmed[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double inches))
                return inches * 25.4;
        }
        else if (trimmed.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(trimmed[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pt))
                return pt * 0.3528; // 1pt = 0.3528mm
        }
        else if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double plain))
        {
            return plain; // Assume mm if no unit
        }

        return 0;
    }

    // ===================== Text Measurement =====================

    private static double EstimateTextHeight(string text, XfaFont font, double widthMm,
        XfaMargin margin, double? paraLineHeightPt = null)
    {
        // Returns the total field box height (text content + vertical margins).
        // The caller subtracts margins to get the text drawing area, so we must include
        // them here to ensure multi-line text has enough room.
        // For minH-controlled fields, Math.Max(fieldH, minH) in the caller ensures
        // single-line fields still use the minH box height.
        double verticalMargins = margin.Top + margin.Bottom;
        // Single-line height uses 1.0x factor (just font em size) — no inter-line spacing
        // needed for a single line. This allows minH to correctly dominate for single-line
        // fields (e.g., kopfdaten: minH=4mm, font=9pt, bottomInset=0.5mm).
        // Per XFA 3.3 Ch.3: lineHeight on <para> sets minimum line height.
        double fontHeightMm = font.SizePt * 0.3528;
        double paraLineHeightMm = paraLineHeightPt.HasValue ? paraLineHeightPt.Value * 0.3528 : 0;
        double singleLineH = Math.Max(fontHeightMm, paraLineHeightMm) + verticalMargins;
        if (string.IsNullOrEmpty(text)) return Math.Max(singleLineH, 3);

        double availW = widthMm - margin.Left - margin.Right;
        if (availW <= 0) availW = widthMm;

        // Average character width is roughly 0.48 * font size in points (calibrated for Arial)
        // Convert to mm: 1pt = 0.3528mm
        double charWidthMm = font.SizePt * 0.3528 * 0.48;
        if (font.Bold) charWidthMm *= 1.08;

        double charsPerLine = availW / Math.Max(charWidthMm, 0.5);

        // Count lines using word-level wrapping simulation.
        // Simple char-division (line.Length / charsPerLine) underestimates because
        // word-wrapping can't break mid-word — this ensures correct line count.
        var lines = text.Split('\n');
        int totalLines = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                totalLines++; // empty line
            }
            else
            {
                // Simulate word wrapping
                var words = line.Split(' ');
                int lineCount = 1;
                double lineWidth = 0;
                foreach (var word in words)
                {
                    double wordWidth = word.Length * charWidthMm;
                    if (lineWidth > 0 && lineWidth + charWidthMm + wordWidth > availW)
                    {
                        lineCount++;
                        lineWidth = wordWidth;
                    }
                    else
                    {
                        lineWidth += (lineWidth > 0 ? charWidthMm : 0) + wordWidth;
                    }
                }
                totalLines += lineCount;
            }
        }

        // For single-line text, use 1.0x font height (no inter-line spacing needed).
        // For multi-line text, use 1.15x line height factor (calibrated against Adobe reference;
        // includes inter-line spacing/leading).
        // Per XFA 3.3 Ch.3: use the greater of calculated line height and para.lineHeight.
        double textH;
        if (totalLines <= 1)
        {
            textH = Math.Max(fontHeightMm, paraLineHeightMm) + verticalMargins;
        }
        else
        {
            double calcLineH = fontHeightMm * 1.15;
            double lineHeightMm = Math.Max(calcLineH, paraLineHeightMm);
            textH = totalLines * lineHeightMm + verticalMargins;
        }

        return Math.Max(textH, singleLineH);
    }

    private static int CountTextLines(string text, XfaFont font, double widthMm)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        double charWidthMm = font.SizePt * 0.3528 * 0.52;
        double charsPerLine = widthMm / Math.Max(charWidthMm, 0.5);
        var lines = text.Split('\n');
        int total = 0;
        foreach (var line in lines)
        {
            int wrapped = line.Length == 0 ? 1 : (int)Math.Ceiling(line.Length / Math.Max(charsPerLine, 1));
            total += Math.Max(wrapped, 1);
        }
        return total;
    }

    /// <summary>
    /// Word-wraps text into individual visual lines based on estimated characters per line.
    /// Uses word boundaries to avoid breaking mid-word (same logic as EstimateTextHeight).
    /// </summary>
    private static List<string> WordWrapText(string text, double charsPerLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add("");
            return result;
        }

        // Process each hard line break separately
        var hardLines = text.Split('\n');
        foreach (var hardLine in hardLines)
        {
            if (hardLine.Length == 0)
            {
                result.Add("");
                continue;
            }

            var words = hardLine.Split(' ');
            var currentLine = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                if (currentLine.Length > 0 && currentLine.Length + 1 + word.Length > charsPerLine)
                {
                    result.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLine.Append(word);
                }
                else
                {
                    if (currentLine.Length > 0) currentLine.Append(' ');
                    currentLine.Append(word);
                }
            }
            if (currentLine.Length > 0)
                result.Add(currentLine.ToString());
        }

        return result;
    }

    // ===================== Column Width Parsing =====================

    private static double[] ParseColumnWidths(string? columnWidths, double totalWidth)
    {
        if (string.IsNullOrEmpty(columnWidths))
            return new[] { totalWidth };

        var parts = columnWidths.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var widths = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            widths[i] = XfaTemplateParser.ParseMeasurement(parts[i]) ?? (totalWidth / parts.Length);
        }

        // Scale up placeholder column widths: when the total is much less than available
        // width, the template uses placeholder values (e.g., all 10mm) that the XFA engine
        // would redistribute at runtime. Scale proportionally to fill available width.
        double sum = widths.Sum();
        if (sum > 0 && sum < totalWidth * 0.6)
        {
            double scale = totalWidth / sum;
            for (int i = 0; i < widths.Length; i++)
                widths[i] *= scale;
        }

        return widths;
    }

    // ===================== Data-Driven Dynamic Table (oDynamicTable) =====================

    /// <summary>
    /// Estimates the total height of a subform for keep-intact page break decisions.
    /// Uses a rough estimate based on child elements and data instances.
    /// </summary>
    /// <summary>
    /// Determines whether a section header (h2/h1) should keep with its next sibling.
    /// In XFA forms, section headers like "Umschuldung/Ablösung" (h2) are always
    /// followed by their content (a table section). Adobe's JS engine uses
    /// resolveNode("break").before = "pageArea" to keep these together.
    /// This implements the same behavior natively: if a header is near the page bottom
    /// and the next sibling is a table section that won't fit, return true to trigger
    /// a page advance before the header.
    /// </summary>
    private bool IsKeepWithNextHeader(XfaDataNode dataChild, XfaSubformDef template,
        int currentIndex, List<XfaDataNode> dataChildren,
        Dictionary<string, XfaSubformDef> templateByDataName,
        double curY, double pageBottom, double availW)
    {
        // Only apply to section headers (h1, h2)
        string name = template.Name ?? "";
        if (name is not ("h1" or "h2"))
            return false;

        // Look ahead to find the next matched sibling
        XfaSubformDef? nextTemplate = null;
        XfaDataNode? nextDataChild = null;
        for (int j = currentIndex + 1; j < dataChildren.Count; j++)
        {
            if (templateByDataName.TryGetValue(dataChildren[j].Name, out var next))
            {
                nextTemplate = next;
                nextDataChild = dataChildren[j];
                break;
            }
        }

        if (nextTemplate is null)
            return false;

        // Only trigger for table-containing sections (absatz_tabelle_*)
        bool nextHasTable = false;
        foreach (var child in nextTemplate.Children)
        {
            if (child is XfaSubformDef sub && sub.Layout == "table")
            {
                nextHasTable = true;
                break;
            }
        }
        if (!nextHasTable)
            return false;

        // Estimate: header height + space for at least the table kopfzeile + one data row.
        // After the header is placed, the table section needs at least ~12mm for a meaningful
        // start (kopfzeile ≈ 6mm + one data row ≈ 6mm). If there's not enough room for
        // the header + that minimum, move both to the next page together.
        double headerH = EstimateSubformHeight(template, availW, dataChild);
        double minTableStart = 10; // kopfzeile minimum space to avoid orphan header
        double remaining = pageBottom - curY;

        // Only trigger when the table would start with less than one row of space
        // after the header. This avoids being too aggressive when there's moderate
        // room (e.g., 17mm remaining — enough for header + kopfzeile before natural overflow).
        double spaceAfterHeader = remaining - headerH;
        if (spaceAfterHeader < minTableStart)
            return true;

        return false;
    }

    private double EstimateSubformHeight(XfaSubformDef subform, double availW, XfaDataNode? dataCtx)
    {
        double totalH = 0;
        foreach (var child in subform.Children)
        {
            switch (child)
            {
                case XfaFieldDef field:
                    double fieldH = field.H ?? field.MinH ?? 5;
                    totalH += fieldH;
                    break;
                case XfaDrawDef draw:
                    totalH += draw.H ?? draw.MinH ?? 4;
                    break;
                case XfaSubformDef sub:
                    if (sub.Layout == "table")
                    {
                        // Estimate table height: header + data rows
                        int rowCount = 1; // at least header
                        var tableData = ResolveDataContext(sub, dataCtx) ?? dataCtx;
                        foreach (var tableChild in sub.Children)
                        {
                            if (tableChild is XfaSubformDef row && row.Layout == "row")
                            {
                                var rowInstances = GetDataInstances(row, tableData);
                                rowCount += rowInstances.Count;
                            }
                        }
                        totalH += rowCount * 6; // ~6mm per row
                    }
                    else
                    {
                        totalH += sub.H ?? sub.MinH ?? 5;
                        // Recurse for tb-layout children
                        if (sub.Layout == "tb" && sub.Children.Count > 0)
                        {
                            totalH += EstimateSubformHeight(sub, availW, dataCtx);
                        }
                    }
                    break;
            }
        }
        return totalH + subform.Margin.Top + subform.Margin.Bottom;
    }

    /// <summary>
    /// Finds the next visible sibling element starting from the given index.
    /// </summary>
    private static XfaElement? FindNextVisibleSibling(List<XfaElement> children, int startIdx)
    {
        for (int i = startIdx; i < children.Count; i++)
        {
            if (children[i].Presence is "hidden" or "inactive" or "invisible")
                continue;
            return children[i];
        }
        return null;
    }

    /// <summary>
    /// Estimates the minimum height of a child element for keep-next calculations.
    /// Returns a rough lower-bound height without performing full layout.
    /// </summary>
    private double EstimateMinChildHeight(XfaElement child, double availW, XfaDataNode? dataCtx)
    {
        return child switch
        {
            XfaFieldDef field => field.H ?? field.MinH ?? 5,
            XfaDrawDef draw => draw.H ?? draw.MinH ?? 4,
            XfaSubformDef sub => EstimateSubformHeight(sub, availW, dataCtx),
            _ => 5
        };
    }

    /// <summary>
    /// Reads data-driven table settings from the data node (oDynamicTable pattern).
    /// XFA forms use a JavaScript library (oDynamicTable.applySettings) that reads
    /// column widths, alignment, and border settings from data attributes.
    /// This implements the same logic natively in C#.
    /// </summary>
    private static (double[] columnWidths, string[] columnAligns)?
        ReadDynamicTableSettings(XfaDataNode? dataCtx, int templateColumnCount, double availW)
    {
        if (dataCtx is null) return null;

        // Look for a "defaults" child node with "spalteN" children defining column settings
        var defaults = dataCtx.Children.FirstOrDefault(
            c => string.Equals(c.Name, "defaults", StringComparison.OrdinalIgnoreCase));
        if (defaults is null) return null;

        // Read width attribute from table data node to determine unit
        string? tableWidthStr = dataCtx.GetAttribute("width");
        double tableWidth = availW;
        if (tableWidthStr is not null)
        {
            double? parsed = XfaTemplateParser.ParseMeasurement(tableWidthStr);
            if (parsed.HasValue && parsed.Value > 0)
                tableWidth = parsed.Value;
        }

        // Parse spalteN definitions from defaults
        var colDefs = new SortedDictionary<int, (double width, string hAlign)>();
        foreach (var child in defaults.Children)
        {
            // Extract column index from name like "spalte1", "spalte2", etc.
            if (!child.Name.StartsWith("spalte", StringComparison.OrdinalIgnoreCase)) continue;
            string idxStr = child.Name.Substring(6);
            if (!int.TryParse(idxStr, out int colNum) || colNum < 1) continue;
            int colIdx = colNum - 1;

            double width = 10; // default
            string? widthStr = child.GetAttribute("width");
            if (widthStr is not null && double.TryParse(widthStr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double w))
                width = w;

            string hAlign = child.GetAttribute("horizontal") ?? "left";
            colDefs[colIdx] = (width, hAlign);
        }

        if (colDefs.Count == 0) return null;

        int numCols = Math.Max(colDefs.Keys.Max() + 1, templateColumnCount);

        // Compute proportional column widths (data widths are relative/percentage-like)
        double totalRelative = colDefs.Values.Sum(d => d.width);
        double[] colWidths = new double[numCols];
        string[] colAligns = new string[numCols];
        for (int i = 0; i < numCols; i++)
        {
            if (colDefs.TryGetValue(i, out var def))
            {
                colWidths[i] = totalRelative > 0 ? (def.width / totalRelative) * tableWidth : tableWidth / numCols;
                colAligns[i] = def.hAlign;
            }
            else
            {
                colWidths[i] = tableWidth / numCols;
                colAligns[i] = null!; // no data-driven alignment — preserve template hAlign
            }
        }

        return (colWidths, colAligns);
    }

    /// <summary>
    /// Reads per-cell alignment overrides from the row's data node.
    /// Each cell data node may have a "horizontal" attribute that overrides the column default.
    /// </summary>
    private static string[]? ReadCellAligns(XfaSubformDef row, XfaDataNode? rowData,
        string[]? columnAligns)
    {
        if (columnAligns is null && rowData is null) return null;

        int numCols = row.Children.Count;
        var aligns = new string[numCols];

        // Start with column defaults from oDynamicTable settings.
        // Use null for columns without explicit alignment so we don't override template hAlign.
        for (int i = 0; i < numCols; i++)
            aligns[i] = columnAligns is not null && i < columnAligns.Length ? columnAligns[i] : null!;

        // Override with per-cell data attributes
        if (rowData is not null)
        {
            int cellIdx = 0;
            foreach (var child in row.Children)
            {
                if (cellIdx >= numCols) break;
                string? cellName = child.Name;
                if (cellName is not null)
                {
                    // Find matching data node for this cell
                    var cellData = rowData.Children.FirstOrDefault(
                        c => string.Equals(c.Name, cellName, StringComparison.OrdinalIgnoreCase));
                    if (cellData is not null)
                    {
                        string? hAlign = cellData.GetAttribute("horizontal");
                        if (hAlign is not null)
                            aligns[cellIdx] = hAlign;
                    }
                }
                cellIdx++;
            }
        }

        return aligns;
    }

    /// <summary>
    /// Removes layout items on the given page that were emitted after (or at) the given Y position.
    /// Used for orphan prevention when a table header needs to be moved to the next page.
    /// </summary>
}
