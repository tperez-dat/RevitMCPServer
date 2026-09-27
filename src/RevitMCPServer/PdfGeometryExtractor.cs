using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RevitMCPServer;

/// <summary>
/// Runs the bundled PyMuPDF script in a child process.
///
/// This deliberately runs in the MCP server rather than in Revit: nothing about it needs the Revit
/// API, and spawning a Python interpreter from inside Revit's process is a liability with no
/// upside. The subprocess boundary also keeps PyMuPDF's AGPL obligation off the rest of the code.
/// </summary>
public sealed class PdfGeometryExtractor(ILogger<PdfGeometryExtractor> logger)
{
    private const int TimeoutMs = 120_000;

    public async Task<string> ExtractAsync(string pdfPath, string? pages, int maxItems,
        bool includeText, CancellationToken ct)
    {
        if (!File.Exists(pdfPath))
            return Error($"No file exists at '{pdfPath}'.");

        if (!pdfPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Error($"'{pdfPath}' is not a .pdf file.");

        var script = LocateScript();
        if (script is null)
            return Error("extract_pdf_geometry.py was not found next to the server executable.");

        var python = LocatePython();
        if (python is null)
        {
            return Error("No Python interpreter was found on PATH. Install Python 3.9 or later and " +
                         "'pip install pymupdf', or set the REVIT_MCP_PYTHON environment variable to " +
                         "the interpreter's full path.");
        }

        var arguments = new List<string> { script, "--pdf", pdfPath, "--max-items", maxItems.ToString() };
        if (!string.IsNullOrWhiteSpace(pages)) { arguments.Add("--pages"); arguments.Add(pages); }
        if (!includeText) arguments.Add("--no-text");

        var startInfo = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeoutMs);

            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return Error($"PDF extraction did not finish within {TimeoutMs / 1000} s. " +
                             "Try fewer pages or a lower maxItems.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run the PDF extraction script.");
            return Error($"Could not run '{python}': {ex.Message}");
        }

        var output = stdout.ToString().Trim();
        var errorText = stderr.ToString().Trim();

        // The script reports its own failures as JSON on stdout, so prefer that over the exit code.
        if (output.Length > 0) return output;

        return Error(process.ExitCode == 0
            ? "The extraction script produced no output."
            : $"The extraction script exited with code {process.ExitCode}. {Summarise(errorText)}");
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { /* it exited on its own */ }
    }

    /// <summary>PyMuPDF prints deprecation notices to stderr; keep only the tail that matters.</summary>
    private static string Summarise(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "It produced no error output.";

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(5);

        return string.Join(" | ", lines);
    }

    private static string? LocateScript()
    {
        var directory = AppContext.BaseDirectory;

        // Beside the executable after a build; under scripts/ when run from the source tree.
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, "extract_pdf_geometry.py"),
                     Path.Combine(directory, "scripts", "extract_pdf_geometry.py")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// An explicit override wins, then the usual Windows and POSIX interpreter names. 'py' is
    /// checked because the Windows launcher is often the only thing on PATH.
    /// </summary>
    private static string? LocatePython()
    {
        if (Environment.GetEnvironmentVariable("REVIT_MCP_PYTHON") is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : null;

        foreach (var name in new[] { "python", "python3", "py" })
        {
            if (OnPath(name)) return name;
        }

        return null;
    }

    private static bool OnPath(string executable)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return false;

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat" }
            : new[] { "" };

        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, executable + extension))) return true;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing over.
                }
            }
        }

        return false;
    }

    private static string Error(string message) =>
        $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(message)}}}";
}
