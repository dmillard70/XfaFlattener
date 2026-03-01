using XfaFlatten.Analysis;
using Xunit.Abstractions;

namespace XfaFlatten.Tests;

public class XfaDetectorDiagnostics
{
    private readonly ITestOutputHelper _output;
    private readonly XfaDetector _detector = new();

    private static string SamplesDir =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"));

    public XfaDetectorDiagnostics(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DiagnoseAllSamples()
    {
        string[] sampleFiles =
        [
            "XFA-Sample-1.pdf",
            "XFA-Sample-1-flattened.pdf",
            "XFA-Sample-2.pdf",
            "XFA-Sample-3.pdf"
        ];

        foreach (var file in sampleFiles)
        {
            string path = Path.Combine(SamplesDir, file);
            if (!File.Exists(path))
            {
                _output.WriteLine($"{file}: FILE NOT FOUND");
                continue;
            }

            var result = _detector.Detect(path);
            _output.WriteLine($"{file}: Type={result.Type}, Pages={result.PageCount}, Error={result.ErrorMessage ?? "none"}");
        }
    }
}
