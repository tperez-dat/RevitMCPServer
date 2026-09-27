namespace RevitMCPBridge;

/// <summary>
/// Write commands are refused unless the user has explicitly opted in from the Revit ribbon.
/// A local named pipe is reachable by any process running as this user, so the model must not be
/// mutable purely because the bridge is listening.
/// </summary>
public static class WriteConsent
{
    private static int _enabled;      // 0 = off, 1 = on. Int + Interlocked: read from the pipe thread.

    /// <summary>Off at every Revit start; never persisted, so consent does not outlive the session.</summary>
    public static bool Enabled => Volatile.Read(ref _enabled) == 1;

    public static void Set(bool on) => Interlocked.Exchange(ref _enabled, on ? 1 : 0);

    public static bool Toggle()
    {
        var next = !Enabled;
        Set(next);
        return next;
    }
}
