using System.Text;

namespace RevitMCPBridge;

/// <summary>
/// Minimal rolling log under %LOCALAPPDATA%\RevitMCPBridge\bridge.log. Revit swallows most add-in
/// diagnostics, and without a log a misbehaving bridge is very hard to diagnose on a locked-down
/// workstation. Never throws: logging must not be able to break the bridge.
/// </summary>
public static class BridgeLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCPBridge");

    public static string FilePath => Path.Combine(Directory, "bridge.log");

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                Roll();

                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(' ').Append(level).Append(' ').Append(message);

                if (ex is not null) line.AppendLine().Append("    ").Append(ex);

                File.AppendAllText(FilePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics are best-effort by design.
        }
    }

    /// <summary>Keeps one previous file so a log cannot fill a user's profile disk.</summary>
    private static void Roll()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes) return;

        var previous = FilePath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(FilePath, previous);
    }
}
