using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace XfaFlatten.Rendering.XfaDirect;

/// <summary>
/// Executes XFA JavaScript scripts using Jint, providing a mini XFA SOM (Scripting Object Model)
/// so scripts can read/write field values, presence, and query layout info.
/// </summary>
public sealed class XfaScriptEngine : IDisposable
{
    private readonly Engine _engine;
    private readonly Dictionary<string, string> _namedScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _verbose;

    // Layout info set after first layout pass (for layout:ready scripts)
    private int _totalPages;
    private readonly Dictionary<string, int> _elementPageMap = new(StringComparer.OrdinalIgnoreCase);

    // Track script-modified values: elementPath -> modified value
    private readonly Dictionary<string, string?> _modifiedValues = new(StringComparer.OrdinalIgnoreCase);
    // Track script-modified presence: elementPath -> presence string
    private readonly Dictionary<string, string> _modifiedPresence = new(StringComparer.OrdinalIgnoreCase);

    public XfaScriptEngine(bool verbose = false)
    {
        _verbose = verbose;

        _engine = new Engine(opts => opts
            .LimitRecursion(64)
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .MaxStatements(10_000)
            .Strict(false));
    }

    /// <summary>
    /// Register a named script library from a &lt;variables&gt; block.
    /// The script is evaluated once to define functions/objects.
    /// </summary>
    public void RegisterNamedScript(string name, string source)
    {
        _namedScripts[name] = source;
    }

    /// <summary>
    /// Evaluate all registered named scripts to define library functions.
    /// Call this once after all scripts are registered and before executing element scripts.
    /// </summary>
    public void InitializeNamedScripts()
    {
        foreach (var (name, source) in _namedScripts)
        {
            try
            {
                // Wrap library scripts as variable assignments so they're accessible by name.
                // Many XFA libraries define themselves as: var oDynamicTable = { ... };
                // If the script already does that, just evaluate it.
                _engine.Execute(source);
                if (_verbose)
                    Console.WriteLine($"  [Script] Registered named script: {name} ({source.Length} chars)");
            }
            catch (Exception ex)
            {
                if (_verbose)
                    Console.WriteLine($"  [Script] Warning: Failed to register '{name}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Execute a script with 'this' bound to the given element proxy.
    /// </summary>
    /// <param name="script">JavaScript source code.</param>
    /// <param name="proxy">The element proxy bound as 'this'.</param>
    /// <param name="eventName">Event name for logging.</param>
    public void Execute(string script, XfaElementProxy proxy, string eventName)
    {
        try
        {
            // Set up the xfa global object
            var xfaLayout = new XfaLayoutProxy(this);
            var xfaHost = new XfaHostProxy(_verbose);

            _engine.SetValue("xfa", new
            {
                layout = xfaLayout,
                host = xfaHost
            });

            // Bind 'this' via a wrapper: Jint doesn't let us override 'this' directly,
            // so we use a with/call pattern: wrap the script as a function and call it.
            _engine.SetValue("__thisProxy", proxy);

            // Execute the script with __thisProxy available as a local.
            // We wrap it so 'this' in the script references the proxy.
            string wrapped = $"(function() {{ var self = __thisProxy; " +
                             $"var _this = __thisProxy; " +
                             RewriteThisReferences(script) +
                             $" }})()";

            _engine.Execute(wrapped);
        }
        catch (TimeoutException)
        {
            if (_verbose)
                Console.WriteLine($"  [Script] Timeout executing {eventName} script");
        }
        catch (StatementsCountOverflowException)
        {
            if (_verbose)
                Console.WriteLine($"  [Script] Statement limit exceeded in {eventName} script");
        }
        catch (Exception ex)
        {
            if (_verbose)
                Console.WriteLine($"  [Script] Error in {eventName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Rewrite 'this.xxx' references to 'self.xxx' so the proxy is used.
    /// This is a simple text transformation for the most common patterns.
    /// </summary>
    private static string RewriteThisReferences(string script)
    {
        // Replace 'this.' with 'self.' for property access
        // This handles: this.rawValue, this.presence, this.isNull, this.parent, etc.
        return script.Replace("this.", "self.");
    }

    /// <summary>
    /// Set layout information for layout:ready scripts (Phase 2).
    /// </summary>
    public void SetLayoutInfo(int totalPages, Dictionary<string, int> pageMap)
    {
        _totalPages = totalPages;
        _elementPageMap.Clear();
        foreach (var (k, v) in pageMap)
            _elementPageMap[k] = v;
    }

    /// <summary>
    /// Get the page index for an element (used by XfaLayoutProxy).
    /// </summary>
    internal int GetPageForElement(string elementPath)
    {
        return _elementPageMap.TryGetValue(elementPath, out int page) ? page : 0;
    }

    /// <summary>
    /// Get the total page count (used by XfaLayoutProxy).
    /// </summary>
    internal int TotalPages => _totalPages;

    /// <summary>
    /// Record a value modification from a script.
    /// </summary>
    internal void SetModifiedValue(string path, string? value)
    {
        _modifiedValues[path] = value;
    }

    /// <summary>
    /// Record a presence modification from a script.
    /// </summary>
    internal void SetModifiedPresence(string path, string presence)
    {
        _modifiedPresence[path] = presence;
    }

    /// <summary>
    /// Check if a script has modified this element's value.
    /// </summary>
    public string? GetModifiedValue(string path)
    {
        return _modifiedValues.TryGetValue(path, out var val) ? val : null;
    }

    /// <summary>
    /// Check if a script has modified this element's presence.
    /// </summary>
    public string? GetModifiedPresence(string path)
    {
        return _modifiedPresence.TryGetValue(path, out var val) ? val : null;
    }

    /// <summary>
    /// Whether any layout:ready scripts modified values (triggers re-layout).
    /// </summary>
    public bool HasLayoutReadyModifications => _modifiedValues.Count > 0;

    /// <summary>
    /// Clear all script modifications (between layout passes).
    /// </summary>
    public void ClearModifications()
    {
        _modifiedValues.Clear();
        _modifiedPresence.Clear();
    }

    public void Dispose()
    {
        // Jint Engine doesn't implement IDisposable but we keep the pattern
        // for potential future resource cleanup
    }
}

/// <summary>
/// Proxy for an XFA template element, exposed to scripts as 'this'.
/// Provides rawValue, presence, isNull, parent, name, and child element navigation.
/// </summary>
public sealed class XfaElementProxy
{
    private readonly XfaScriptEngine _engine;
    private readonly string _path;
    private string? _rawValue;
    private string _presence;
    private readonly XfaDataNode? _dataNode;
    private readonly XfaElement? _templateElement;
    private readonly XfaElementProxy? _parentProxy;
    private readonly Dictionary<string, XfaElementProxy> _childProxies = new(StringComparer.OrdinalIgnoreCase);

    public XfaElementProxy(
        XfaScriptEngine engine,
        string path,
        string? name,
        string? rawValue,
        string presence,
        XfaDataNode? dataNode,
        XfaElement? templateElement,
        XfaElementProxy? parentProxy)
    {
        _engine = engine;
        _path = path;
        this.name = name ?? "";
        _rawValue = rawValue;
        _presence = presence;
        _dataNode = dataNode;
        _templateElement = templateElement;
        _parentProxy = parentProxy;
    }

    /// <summary>Element name.</summary>
    public string name { get; }

    /// <summary>
    /// The raw value of the field. Reading returns the current value;
    /// writing records the modification for the layout engine.
    /// </summary>
    public string? rawValue
    {
        get => _rawValue;
        set
        {
            _rawValue = value;
            _engine.SetModifiedValue(_path, value);
        }
    }

    /// <summary>
    /// The presence of the element: "visible", "hidden", or "inactive".
    /// Writing records the modification.
    /// </summary>
    public string presence
    {
        get => _presence;
        set
        {
            _presence = value;
            _engine.SetModifiedPresence(_path, value);
        }
    }

    /// <summary>Whether the rawValue is null or empty.</summary>
    public bool isNull => string.IsNullOrEmpty(_rawValue);

    /// <summary>The parent element proxy, or null for root.</summary>
    public XfaElementProxy? parent => _parentProxy;

    /// <summary>
    /// Add a child proxy for navigation via property access.
    /// </summary>
    public void AddChild(string childName, XfaElementProxy childProxy)
    {
        _childProxies[childName] = childProxy;
    }

    /// <summary>
    /// Get a child proxy by name (for JS property access like this.parent.wert).
    /// </summary>
    public XfaElementProxy? GetChild(string childName)
    {
        return _childProxies.TryGetValue(childName, out var child) ? child : null;
    }

    /// <summary>
    /// For Jint property access: allows scripts to use this.parent.fieldName.rawValue
    /// by returning child proxies on property access.
    /// </summary>
    public XfaElementProxy? this[string key]
    {
        get => GetChild(key);
    }

    /// <summary>
    /// Creates a caption sub-proxy for scripts that modify caption properties.
    /// </summary>
    public XfaCaptionProxy? caption { get; set; }

    /// <summary>
    /// For numeric comparisons in scripts (e.g., this.rawValue != 1).
    /// </summary>
    public override string? ToString() => _rawValue;
}

/// <summary>
/// Proxy for field caption properties (used by scripts that set caption.reserve etc.)
/// </summary>
public sealed class XfaCaptionProxy
{
    private double _reserve;

    public XfaCaptionProxy(double reserve)
    {
        _reserve = reserve;
    }

    public double reserve
    {
        get => _reserve;
        set => _reserve = value;
    }
}

/// <summary>
/// Proxy for xfa.layout, providing page() and pageCount() to scripts.
/// </summary>
public sealed class XfaLayoutProxy
{
    private readonly XfaScriptEngine _engine;

    public XfaLayoutProxy(XfaScriptEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Returns the 1-based page index for the given element.
    /// Scripts call: xfa.layout.page(this)
    /// Accepts any object type since Jint may pass the proxy directly or as a JsValue.
    /// </summary>
    public int page(object element)
    {
        if (element is XfaElementProxy proxy)
            return _engine.GetPageForElement(proxy.name) + 1; // 1-based
        return 1; // Default for unknown element types
    }

    /// <summary>
    /// Returns the total page count.
    /// Scripts call: xfa.layout.pageCount()
    /// </summary>
    public int pageCount()
    {
        return _engine.TotalPages;
    }

    /// <summary>
    /// Returns the total number of pages (some scripts use this variant).
    /// </summary>
    public int pageCount(object? ignored)
    {
        return _engine.TotalPages;
    }
}

/// <summary>
/// Proxy for xfa.host, providing messageBox() (no-op) and other host methods.
/// </summary>
public sealed class XfaHostProxy
{
    private readonly bool _verbose;

    public XfaHostProxy(bool verbose)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// No-op implementation of xfa.host.messageBox().
    /// </summary>
    public void messageBox(string message)
    {
        if (_verbose)
            Console.WriteLine($"  [Script] xfa.host.messageBox: {message}");
    }

    /// <summary>
    /// No-op implementation of xfa.host.messageBox with title.
    /// </summary>
    public void messageBox(string message, string title)
    {
        if (_verbose)
            Console.WriteLine($"  [Script] xfa.host.messageBox({title}): {message}");
    }
}
