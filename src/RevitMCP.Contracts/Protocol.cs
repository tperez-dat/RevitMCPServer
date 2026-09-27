using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RevitMCP.Contracts;

public static class Protocol
{
    /// <summary>Wire protocol version. Bridge refuses a client whose major version differs.</summary>
    public const int Version = 1;

    /// <summary>Single-instance pipe name (session is single-instance by design).</summary>
    public const string PipeName = "RevitMCPBridge";

    /// <summary>Max bytes for one framed message, both directions. Guards against a runaway payload.</summary>
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
}

/// <summary>A command sent from the MCP server to the Revit add-in.</summary>
public sealed class BridgeRequest
{
    public int Version { get; set; } = Protocol.Version;

    /// <summary>Correlation id, echoed back on the response.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>One of <see cref="Commands"/>.</summary>
    public string Command { get; set; } = "";

    /// <summary>Command-specific arguments.</summary>
    public JsonObject? Args { get; set; }

    /// <summary>Per-session shared secret from the handshake file. Required on every request.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Client-side deadline in milliseconds. The bridge abandons the wait (but cannot cancel a
    /// Revit call already in flight) and answers <see cref="BridgeErrorCode.Timeout"/>.
    /// </summary>
    public int TimeoutMs { get; set; } = 30_000;
}

public enum BridgeErrorCode
{
    None = 0,
    /// <summary>Command name not recognised.</summary>
    UnknownCommand,
    /// <summary>Arguments missing, malformed, or out of range.</summary>
    BadRequest,
    /// <summary>Token missing or wrong.</summary>
    Unauthorized,
    /// <summary>A write was attempted while write mode is off.</summary>
    WriteNotPermitted,
    /// <summary>No document open, or the document is not of a usable kind.</summary>
    NoDocument,
    /// <summary>Referenced element / view / type does not exist.</summary>
    NotFound,
    /// <summary>The request is legal but not valid for the current model or view state.</summary>
    InvalidState,
    /// <summary>Revit did not reach API context before the deadline (modal dialog, long command).</summary>
    Timeout,
    /// <summary>Revit threw. <see cref="BridgeResponse.Detail"/> carries the exception text.</summary>
    RevitException,
    /// <summary>Anything the bridge did not anticipate.</summary>
    Internal,
    /// <summary>Protocol version mismatch between client and bridge.</summary>
    VersionMismatch
}

/// <summary>The add-in's answer to a <see cref="BridgeRequest"/>.</summary>
public sealed class BridgeResponse
{
    public int Version { get; set; } = Protocol.Version;
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Ok { get; set; }

    /// <summary>Payload on success.</summary>
    public JsonNode? Data { get; set; }

    public BridgeErrorCode Error { get; set; } = BridgeErrorCode.None;

    /// <summary>Human-readable message, safe to surface to the model.</summary>
    public string? Message { get; set; }

    /// <summary>Exception detail / stack, for diagnostics.</summary>
    public string? Detail { get; set; }

    /// <summary>Server-side duration, useful for spotting slow model queries.</summary>
    public long ElapsedMs { get; set; }

    public static BridgeResponse Success(BridgeRequest req, JsonNode? data) => new()
    {
        Id = req.Id, Command = req.Command, Ok = true, Data = data
    };

    public static BridgeResponse Fail(BridgeRequest req, BridgeErrorCode code, string message, string? detail = null) => new()
    {
        Id = req.Id, Command = req.Command, Ok = false, Error = code, Message = message, Detail = detail
    };
}

/// <summary>
/// Handshake file written by the add-in and read by the MCP server. Lives under
/// %LOCALAPPDATA%\RevitMCPBridge\session.json with owner-only ACLs.
/// </summary>
public sealed class BridgeSessionFile
{
    public int Version { get; set; } = Protocol.Version;
    public string PipeName { get; set; } = Protocol.PipeName;
    public string Token { get; set; } = "";
    public int ProcessId { get; set; }
    public string RevitVersion { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>
    /// Where the handshake file lives. REVIT_MCP_SESSION_FILE overrides it, which allows a
    /// non-standard install and keeps tests from touching a developer's live session file.
    /// </summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("REVIT_MCP_SESSION_FILE") is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMCPBridge", "session.json");
}
