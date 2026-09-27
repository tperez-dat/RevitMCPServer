using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Publishes the per-session token the MCP server needs, in a file only this user can read.
/// The token is generated fresh at each Revit start and deleted at shutdown, so a stale token from
/// a previous session is never accepted.
/// </summary>
public static class SessionHandshake
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static void Publish(BridgeSessionFile session)
    {
        var path = BridgeSessionFile.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var json = JsonSerializer.Serialize(session, Protocol.Json);
        File.WriteAllText(path, json);
        RestrictToCurrentUser(path);

        BridgeLog.Info($"Session file published at {path} (pid {session.ProcessId}, Revit {session.RevitVersion}).");
    }

    public static void Remove()
    {
        try
        {
            var path = BridgeSessionFile.DefaultPath;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("Could not delete the session file.", ex);
        }
    }

    /// <summary>Strips inherited ACEs so only the current user can read the token.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var info = new FileInfo(path);
            var security = info.GetAccessControl();

            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            foreach (FileSystemAccessRule existing in security.GetAccessRules(
                         true, false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRule(existing);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));

            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            // %LOCALAPPDATA% is already per-user; tightening it further is defence in depth.
            BridgeLog.Warn("Could not tighten ACLs on the session file.", ex);
        }
    }
}
