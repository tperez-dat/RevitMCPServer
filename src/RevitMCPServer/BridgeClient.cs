using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RevitMCP.Contracts;

namespace RevitMCPServer;

/// <summary>
/// Talks to the Revit add-in over the local named pipe.
///
/// Requests are serialised with a semaphore: the pipe carries one request at a time, and Revit's
/// single-threaded API means concurrency would buy nothing anyway. The connection is re-established
/// on demand, so restarting Revit does not require restarting the MCP server.
/// </summary>
public sealed class BridgeClient(ILogger<BridgeClient> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private BridgeSessionFile? _session;

    /// <summary>
    /// Sends a command and returns the response. Transport problems are turned into a failed
    /// <see cref="BridgeResponse"/> rather than thrown, so a tool always has something to report.
    /// </summary>
    public async Task<BridgeResponse> CallAsync(string command, JsonObject? args,
        int timeoutMs, CancellationToken ct)
    {
        var request = new BridgeRequest
        {
            Command = command,
            Args = args,
            TimeoutMs = timeoutMs
        };

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // One retry: the common failure is a pipe left over from a closed Revit session, which
            // only reveals itself on write.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var session = await EnsureSessionAsync().ConfigureAwait(false);
                    request.Token = session.Token;

                    var pipe = await EnsureConnectedAsync(session, ct).ConfigureAwait(false);

                    var payload = JsonSerializer.SerializeToUtf8Bytes(request, Protocol.Json);
                    await Framing.WriteFrameAsync(pipe, payload, ct).ConfigureAwait(false);

                    var frame = await Framing.ReadFrameAsync(pipe, ct).ConfigureAwait(false)
                                ?? throw new IOException("Revit closed the connection without replying.");

                    return JsonSerializer.Deserialize<BridgeResponse>(frame, Protocol.Json)
                           ?? throw new IOException("Revit sent an empty response.");
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                               or InvalidOperationException && attempt == 1)
                {
                    logger.LogWarning(ex, "Bridge call {Command} failed; reconnecting and retrying.", command);
                    Reset();
                }
            }

            throw new IOException("Unreachable: the retry loop always returns or throws.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BridgeUnavailableException ex)
        {
            Reset();
            return BridgeResponse.Fail(request, BridgeErrorCode.Internal, ex.Message);
        }
        catch (Exception ex)
        {
            Reset();
            logger.LogError(ex, "Bridge call {Command} failed.", command);

            return BridgeResponse.Fail(request, BridgeErrorCode.Internal,
                $"Could not reach the Revit bridge: {ex.Message}. Is Revit running with the " +
                "RevitMCPBridge add-in loaded?");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the handshake file Revit writes at startup. Re-read whenever the connection drops, so a
    /// new Revit session's token is picked up automatically.
    /// </summary>
    private Task<BridgeSessionFile> EnsureSessionAsync()
    {
        if (_session is not null) return Task.FromResult(_session);

        var path = BridgeSessionFile.DefaultPath;

        if (!File.Exists(path))
        {
            throw new BridgeUnavailableException(
                $"No Revit bridge session file at {path}. Start Revit with the RevitMCPBridge " +
                "add-in installed; it writes this file at startup.");
        }

        BridgeSessionFile session;
        try
        {
            session = JsonSerializer.Deserialize<BridgeSessionFile>(File.ReadAllText(path), Protocol.Json)
                      ?? throw new BridgeUnavailableException($"The session file at {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new BridgeUnavailableException(
                $"The session file at {path} is not readable JSON: {ex.Message}");
        }

        if (session.Version != Protocol.Version)
        {
            throw new BridgeUnavailableException(
                $"The Revit add-in speaks protocol v{session.Version}; this server speaks " +
                $"v{Protocol.Version}. Update whichever is older.");
        }

        if (string.IsNullOrEmpty(session.Token))
            throw new BridgeUnavailableException($"The session file at {path} carries no token.");

        _session = session;
        logger.LogInformation("Using bridge session for Revit {Version} (pid {Pid}).",
            session.RevitVersion, session.ProcessId);

        return Task.FromResult(session);
    }

    private async Task<NamedPipeClientStream> EnsureConnectedAsync(BridgeSessionFile session, CancellationToken ct)
    {
        if (_pipe is { IsConnected: true }) return _pipe;

        Reset();

        var pipe = new NamedPipeClientStream(".", session.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(5_000, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BridgeUnavailableException(
                $"Timed out connecting to \\\\.\\pipe\\{session.PipeName}. Revit may have closed, " +
                "or another client holds the single allowed connection.");
        }

        _pipe = pipe;
        return pipe;
    }

    private void Reset()
    {
        try { _pipe?.Dispose(); } catch { /* already gone */ }
        _pipe = null;

        // Drop the cached session too: a stale token is the usual cause of a failed reconnect.
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null) await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>The bridge is not reachable for a reason worth explaining to the user.</summary>
public sealed class BridgeUnavailableException(string message) : Exception(message);
