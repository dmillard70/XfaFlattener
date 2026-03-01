using System.CommandLine;
using System.CommandLine.Invocation;
using XfaFlatten.Analysis;
using XfaFlatten.Assembly;
using XfaFlatten.Infrastructure;
using XfaFlatten.Rendering;
using XfaFlatten.Validation;

var inputArgument = new Argument<FileInfo>(
    name: "input-pdf",
    description: "Path to the input XFA-based PDF file.");

var outputArgument = new Argument<FileInfo?>(
    name: "output-pdf",
    getDefaultValue: () => null,
    description: "Path to the output flattened PDF file. Defaults to <input>_flat.pdf.");

var dpiOption = new Option<int>(
    name: "--dpi",
    getDefaultValue: () => 200,
    description: "Rendering resolution in DPI (72-600). Only relevant for the PDFium engine.");

var engineOption = new Option<string>(
    name: "--engine",
    getDefaultValue: () => "auto",
    description: "Rendering engine to use: auto, pdfium, or playwright.");
engineOption.FromAmong("auto", "pdfium", "playwright");

var exportXfaOption = new Option<bool>(
    name: "--export-xfa",
    getDefaultValue: () => false,
    description: "Export XFA XML data before flattening.");

var skipNonXfaOption = new Option<bool>(
    name: "--skip-non-xfa",
    getDefaultValue: () => false,
    description: "Skip non-XFA PDFs instead of copying them.");

var overwriteOption = new Option<bool>(
    name: "--overwrite",
    getDefaultValue: () => false,
    description: "Overwrite the output file if it already exists.");

var verboseOption = new Option<bool>(
    name: "--verbose",
    getDefaultValue: () => false,
    description: "Enable verbose console output.");

var chromiumPathOption = new Option<string?>(
    name: "--chromium-path",
    getDefaultValue: () => null,
    description: "Path to a Chromium installation for the Playwright engine.");

var rootCommand = new RootCommand("XfaFlattener - Flatten XFA-based PDF documents into standard PDFs.")
{
    inputArgument,
    outputArgument,
    dpiOption,
    engineOption,
    exportXfaOption,
    skipNonXfaOption,
    overwriteOption,
    verboseOption,
    chromiumPathOption
};

rootCommand.AddValidator(result =>
{
    var dpi = result.GetValueForOption(dpiOption);
    if (dpi < 72 || dpi > 600)
    {
        result.ErrorMessage = "DPI must be between 72 and 600.";
    }
});

rootCommand.SetHandler(async (InvocationContext context) =>
{
    var inputFile = context.ParseResult.GetValueForArgument(inputArgument);
    var outputFile = context.ParseResult.GetValueForArgument(outputArgument);
    var dpi = context.ParseResult.GetValueForOption(dpiOption);
    var engine = context.ParseResult.GetValueForOption(engineOption)!;
    var exportXfa = context.ParseResult.GetValueForOption(exportXfaOption);
    var skipNonXfa = context.ParseResult.GetValueForOption(skipNonXfaOption);
    var overwrite = context.ParseResult.GetValueForOption(overwriteOption);
    var verbose = context.ParseResult.GetValueForOption(verboseOption);
    var chromiumPath = context.ParseResult.GetValueForOption(chromiumPathOption);

    var logger = new ConsoleLogger { Verbose = verbose };

    logger.Info("XfaFlattener v1.0.0");

    // --- Validate input file ---
    if (!inputFile.Exists)
    {
        logger.Error($"Input file not found: {inputFile.FullName}");
        context.ExitCode = ExitCodes.InputFileNotFound;
        return;
    }

    // --- Compute output path ---
    if (outputFile is null)
    {
        var dir = Path.GetDirectoryName(inputFile.FullName) ?? ".";
        var name = Path.GetFileNameWithoutExtension(inputFile.FullName);
        var outputPath = Path.Combine(dir, $"{name}_flat.pdf");
        outputFile = new FileInfo(outputPath);
    }

    // --- Check overwrite ---
    if (outputFile.Exists && !overwrite)
    {
        logger.Error($"Output file already exists: {outputFile.FullName}");
        logger.Error("Use --overwrite to replace it.");
        context.ExitCode = ExitCodes.OutputWriteError;
        return;
    }

    // --- Log configuration ---
    logger.VerboseLog($"Input:  {inputFile.FullName}");
    logger.VerboseLog($"Output: {outputFile.FullName}");
    logger.VerboseLog($"Engine: {engine}");
    logger.VerboseLog($"DPI:    {dpi}");
    if (exportXfa) logger.VerboseLog("XFA export enabled.");
    if (skipNonXfa) logger.VerboseLog("Skip non-XFA mode enabled.");
    if (chromiumPath is not null) logger.VerboseLog($"Chromium path: {chromiumPath}");

    // ========================================
    // Phase 1: XFA Detection
    // ========================================
    logger.Info("Detecting XFA content...");
    var detector = new XfaDetector();
    var detection = detector.Detect(inputFile.FullName);

    if (detection.ErrorMessage is not null)
    {
        logger.Error($"PDF analysis failed: {detection.ErrorMessage}");
        context.ExitCode = ExitCodes.InvalidPdf;
        return;
    }

    logger.Info($"XFA type: {detection.Type}, Pages: {detection.PageCount}");

    if (detection.Type == XfaType.None)
    {
        if (skipNonXfa)
        {
            logger.Info("No XFA content detected. Skipping (--skip-non-xfa).");
            context.ExitCode = ExitCodes.NoXfaDetected;
            return;
        }

        // Copy the file unchanged.
        logger.Info("No XFA content detected. Copying file unchanged.");
        File.Copy(inputFile.FullName, outputFile.FullName, overwrite);
        logger.Success($"Output: {outputFile.FullName}");
        context.ExitCode = ExitCodes.Success;
        return;
    }

    // ========================================
    // Phase 1b: XFA Export (optional)
    // ========================================
    if (exportXfa)
    {
        logger.Info("Exporting XFA data...");
        try
        {
            var outputDir = Path.GetDirectoryName(outputFile.FullName) ?? ".";
            var extractor = new XfaDataExtractor();
            extractor.ExportAll(inputFile.FullName, outputDir);
            logger.Success("XFA data exported.");
        }
        catch (Exception ex)
        {
            logger.Warning($"XFA export failed: {ex.Message}");
            // Non-fatal — continue with rendering.
        }
    }

    // ========================================
    // Phase 2: Rendering
    // ========================================
    logger.Info("Rendering PDF...");
    var selector = new EngineSelector(engine, chromiumPath, logger);

    RenderResult renderResult;
    try
    {
        renderResult = await selector.RenderAsync(inputFile.FullName, dpi, detection.PageCount);
    }
    catch (Exception ex)
    {
        logger.Error($"Rendering failed: {ex.Message}");
        context.ExitCode = ExitCodes.XfaRenderingFailed;
        return;
    }

    if (!renderResult.Success)
    {
        logger.Error($"Rendering failed: {renderResult.ErrorMessage}");
        context.ExitCode = ExitCodes.XfaRenderingFailed;
        return;
    }

    // ========================================
    // Phase 3: Validation
    // ========================================
    var validation = RenderValidator.Validate(renderResult, detection.PageCount);
    if (!validation.IsValid)
    {
        logger.Error($"Validation failed: {validation.Message}");
        context.ExitCode = ExitCodes.XfaRenderingFailed;
        return;
    }

    if (validation.BlankPageIndices.Length > 0)
        logger.Warning(validation.Message);
    else
        logger.VerboseLog($"Validation: {validation.Message}");

    // ========================================
    // Phase 4: PDF Assembly
    // ========================================
    logger.Info("Assembling output PDF...");
    try
    {
        PdfAssembler.Assemble(renderResult, outputFile.FullName);
    }
    catch (Exception ex)
    {
        logger.Error($"PDF assembly failed: {ex.Message}");
        context.ExitCode = ExitCodes.OutputWriteError;
        return;
    }

    // Copy metadata from original PDF.
    try
    {
        MetadataCopier.CopyMetadata(inputFile.FullName, outputFile.FullName);
        logger.VerboseLog("Metadata copied from original PDF.");
    }
    catch (Exception ex)
    {
        logger.Warning($"Metadata copy failed (non-fatal): {ex.Message}");
    }

    logger.Success($"Output: {outputFile.FullName}");
    context.ExitCode = ExitCodes.Success;
});

return await rootCommand.InvokeAsync(args);
