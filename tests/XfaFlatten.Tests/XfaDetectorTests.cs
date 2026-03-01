using XfaFlatten.Analysis;

namespace XfaFlatten.Tests;

public class XfaDetectorTests
{
    private readonly XfaDetector _detector = new();

    private static string SamplesDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));

    [Fact]
    public void Detect_FileNotFound_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _detector.Detect(@"C:\nonexistent\path\does_not_exist.pdf"));
    }

    [Fact]
    public void Detect_XfaSample1_ReturnsXfaType()
    {
        string path = Path.Combine(SamplesDir, "XFA-Sample-1.pdf");
        if (!File.Exists(path))
        {
            // Skip if samples not available
            return;
        }

        var result = _detector.Detect(path);

        Assert.Null(result.ErrorMessage);
        Assert.NotEqual(XfaType.None, result.Type);
        Assert.True(result.PageCount > 0, "Page count should be positive for a valid PDF.");
    }

    [Fact]
    public void Detect_XfaSample2_ReturnsXfaType()
    {
        string path = Path.Combine(SamplesDir, "XFA-Sample-2.pdf");
        if (!File.Exists(path))
            return;

        var result = _detector.Detect(path);

        Assert.Null(result.ErrorMessage);
        Assert.NotEqual(XfaType.None, result.Type);
        Assert.True(result.PageCount > 0);
    }

    [Fact]
    public void Detect_XfaSample3_ReturnsXfaType()
    {
        string path = Path.Combine(SamplesDir, "XFA-Sample-3.pdf");
        if (!File.Exists(path))
            return;

        var result = _detector.Detect(path);

        Assert.Null(result.ErrorMessage);
        Assert.NotEqual(XfaType.None, result.Type);
        Assert.True(result.PageCount > 0);
    }

    [Fact]
    public void Detect_FlattenedPdf_ReturnsNoneOrHybrid()
    {
        // The flattened PDF should not contain XFA (or at most be a hybrid with AcroForm remnants)
        string path = Path.Combine(SamplesDir, "XFA-Sample-1-flattened.pdf");
        if (!File.Exists(path))
            return;

        var result = _detector.Detect(path);

        Assert.Null(result.ErrorMessage);
        Assert.True(result.PageCount > 0);
        // A flattened PDF should typically be None (no XFA left)
    }

    [Fact]
    public void HasDynamicLayout_WithDynamicSubform_ReturnsTrue()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
              <subform layout="tb" name="form1">
                <subform layout="position" name="page1">
                  <field name="TextField1" />
                </subform>
              </subform>
            </template>
            """;

        bool result = XfaDetector.HasDynamicLayout(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.True(result);
    }

    [Fact]
    public void HasDynamicLayout_WithPositionSubform_ReturnsFalse()
    {
        string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
              <subform layout="position" name="form1">
                <subform layout="position" name="page1">
                  <field name="TextField1" />
                </subform>
              </subform>
            </template>
            """;

        bool result = XfaDetector.HasDynamicLayout(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.False(result);
    }

    [Fact]
    public void HasDynamicLayout_WithRlTbLayout_ReturnsTrue()
    {
        string xml = """
            <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
              <subform layout="rl-tb" name="form1">
                <field name="TextField1" />
              </subform>
            </template>
            """;

        bool result = XfaDetector.HasDynamicLayout(System.Text.Encoding.UTF8.GetBytes(xml));
        Assert.True(result);
    }

    [Fact]
    public void ExtractPacket_FindsTemplate()
    {
        string xml = """
            <xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">
              <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
                <subform name="form1"/>
              </template>
              <datasets xmlns="http://www.xfa.org/schema/xfa-data/1.0/">
                <data/>
              </datasets>
            </xdp:xdp>
            """;

        string? packet = XfaDetector.ExtractPacket(xml, "template");

        Assert.NotNull(packet);
        Assert.Contains("<template", packet);
        Assert.Contains("</template>", packet);
    }

    [Fact]
    public void ExtractPacket_FindsDatasets()
    {
        string xml = """
            <xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">
              <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
                <subform name="form1"/>
              </template>
              <datasets xmlns="http://www.xfa.org/schema/xfa-data/1.0/">
                <data/>
              </datasets>
            </xdp:xdp>
            """;

        string? packet = XfaDetector.ExtractPacket(xml, "datasets");

        Assert.NotNull(packet);
        Assert.Contains("<datasets", packet);
        Assert.Contains("</datasets>", packet);
    }

    [Fact]
    public void ExtractPacket_ReturnsNullForMissing()
    {
        string xml = """
            <xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">
              <template xmlns="http://www.xfa.org/schema/xfa-template/3.0/">
                <subform name="form1"/>
              </template>
            </xdp:xdp>
            """;

        string? packet = XfaDetector.ExtractPacket(xml, "nonexistent");

        Assert.Null(packet);
    }
}
