namespace XfaFlatten.Infrastructure;

/// <summary>
/// Simple console logger with colored output and verbose filtering.
/// </summary>
public sealed class ConsoleLogger
{
    /// <summary>
    /// Gets or sets whether verbose messages are written to the console.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Writes an informational message to the console.
    /// </summary>
    public void Info(string message)
    {
        Console.WriteLine(message);
    }

    /// <summary>
    /// Writes a message to the console only when <see cref="Verbose"/> is enabled.
    /// </summary>
    public void VerboseLog(string message)
    {
        if (Verbose)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>
    /// Writes a warning message in yellow to the console.
    /// </summary>
    public void Warning(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Writes an error message in red to the console.
    /// </summary>
    public void Error(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message);
        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Writes a success message in green to the console.
    /// </summary>
    public void Success(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
