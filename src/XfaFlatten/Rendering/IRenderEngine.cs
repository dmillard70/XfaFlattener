namespace XfaFlatten.Rendering;

/// <summary>
/// Defines a rendering engine that can flatten XFA-based PDFs.
/// </summary>
public interface IRenderEngine
{
    /// <summary>
    /// Gets the display name of this rendering engine.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Renders the specified PDF file, flattening any XFA content.
    /// </summary>
    /// <param name="inputPath">Absolute path to the input PDF file.</param>
    /// <param name="dpi">Rendering resolution in dots per inch.</param>
    /// <param name="verbose">Whether to emit verbose log output.</param>
    /// <returns>A <see cref="RenderResult"/> containing rendered pages or PDF bytes.</returns>
    Task<RenderResult> RenderAsync(string inputPath, int dpi, bool verbose);
}
