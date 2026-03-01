namespace XfaFlatten.Infrastructure;

/// <summary>
/// Defines process exit codes for the XfaFlattener application.
/// </summary>
public static class ExitCodes
{
    /// <summary>Successful execution.</summary>
    public const int Success = 0;

    /// <summary>An unspecified error occurred.</summary>
    public const int GeneralError = 1;

    /// <summary>The specified input PDF file was not found.</summary>
    public const int InputFileNotFound = 2;

    /// <summary>The input file is not a valid PDF.</summary>
    public const int InvalidPdf = 3;

    /// <summary>XFA rendering failed across all engines.</summary>
    public const int XfaRenderingFailed = 4;

    /// <summary>The output file could not be written.</summary>
    public const int OutputWriteError = 5;

    /// <summary>No XFA content detected (with --skip-non-xfa).</summary>
    public const int NoXfaDetected = 10;
}
