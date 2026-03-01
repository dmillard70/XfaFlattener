namespace XfaFlatten.Analysis;

/// <summary>
/// Identifies the type of XFA content found in a PDF document.
/// </summary>
public enum XfaType
{
    /// <summary>No XFA content; pure AcroForm or no form at all.</summary>
    None,

    /// <summary>Static XFA with a fixed layout.</summary>
    Static,

    /// <summary>Dynamic XFA where layout adapts to data.</summary>
    Dynamic,

    /// <summary>Hybrid form with both AcroForm fields and an XFA stream.</summary>
    Hybrid
}
