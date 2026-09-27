using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Listens on the local named pipe and hands each request to <see cref="RevitCommandDispatcher"/>.
/// Runs entirely off Revit's main thread and never touches the Revit API itself.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly RevitCommandDispatcher _dispatcher;
    private readonly string _token;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public PipeServer(RevitCommandDispatcher dispatcher, string token)
    {
        _dispatcher = dispatcher;
        _token = token;
    }

    public bool IsRunning => _loop is { IsCompleted: false };
    public int ConnectionsAccepted { get; private set; }
    public int RequestsHandled { get; private set; }
    public string? LastError { get; private set; }

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));

    /// <summary>
    /// Accepts one client at a time. Single-instance by design: Revit itself is single-threaded, so
    /// concurrent clients would serialise on the external event regardless.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                ConnectionsAccepted++;
                await ServeClientAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A broken client must never take the listener down; log and accept the next one.
                LastError = $"{DateTimeOffset.Now:HH:mm:ss} {ex.GetType().Name}: {ex.Message}";
                BridgeLog.Warn("Pipe connection failed", ex);
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* already gone */ }
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Creates the pipe restricted to the current user. The token in each request is the real
    /// authorisation check; this ACL just keeps other accounts on the machine from connecting at all.
    /// </summary>
    private static NamedPipeServerStream CreatePipe()
    {
        const int bufferSize = 64 * 1024;

        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                identity.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                Protocol.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: bufferSize,
                outBufferSize: bufferSize,
                pipeSecurity: security);
        }
        catch (PlatformNotSupportedException)
        {
            // Should not happen on Windows, but a default-ACL pipe (creator + SYSTEM + admins)
            // plus the token check is still an acceptable posture.
            BridgeLog.Warn("Pipe ACLs unavailable; falling back to the default pipe ACL.", null);
            return new NamedPipeServerStream(
                Protocol.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, bufferSize, bufferSize);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            var frame = await Framing.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
            if (frame is null) return;                 // client closed cleanly

            var response = await HandleFrameAsync(frame, ct).ConfigureAwait(false);
            RequestsHandled++;

            var payload = JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json);
            await Framing.WriteFrameAsync(pipe, payload, ct).ConfigureAwait(false);
        }
    }

    private async Task<BridgeResponse> HandleFrameAsync(byte[] frame, CancellationToken ct)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(frame, Protocol.Json);
            if (request is null || string.IsNullOrWhiteSpace(request.Command))
            {
                return new BridgeResponse
                {
                    Ok = false,
                    Error = BridgeErrorCode.BadRequest,
                    Message = "Request was empty or had no 'command'."
                };
            }

            if (request.Version != Protocol.Version)
            {
                return BridgeResponse.Fail(request, BridgeErrorCode.VersionMismatch,
                    $"Client speaks protocol v{request.Version}; this bridge speaks v{Protocol.Version}. " +
                    "Update whichever side is older.");
            }

            // Constant-time compare so a wrong token cannot be discovered by timing.
            if (!TokenMatches(request.Token))
            {
                BridgeLog.Warn($"Rejected '{request.Command}': bad token.", null);
                return BridgeResponse.Fail(request, BridgeErrorCode.Unauthorized,
                    "Missing or invalid session token. The MCP server reads it from the bridge " +
                    "session file; restart the MCP server so it picks up the current Revit session.");
            }

            return await _dispatcher.SubmitAsync(request, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return new BridgeResponse
            {
                Ok = false,
                Error = BridgeErrorCode.BadRequest,
                Message = "Request was not valid JSON.",
                Detail = ex.Message
            };
        }
        catch (Exception ex) when (request is not null)
        {
            return BridgeResponse.Fail(request, BridgeErrorCode.Internal, ex.Message, ex.ToString());
        }
    }

    private bool TokenMatches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex) { BridgeLog.Warn("Pipe listener ended with an error", ex); }
        }

        _shutdown.Dispose();
    }
}
