using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using RevitMCP.Contracts;

namespace RevitMCP.Tests;

/// <summary>
/// Stands in for the Revit add-in: a named pipe that speaks the real protocol. .NET implements named
/// pipes over Unix domain sockets on Linux, so the transport, the framing, and the token handshake
/// can all be exercised without Revit.
/// </summary>
public sealed class FakeBridge : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _sessionPath;
    private readonly Task _loop;

    public FakeBridge(
        Func<BridgeRequest, BridgeResponse>? handler = null,
        int protocolVersion = Protocol.Version,
        bool writeSessionFile = true)
    {
        // A unique pipe name per test keeps parallel test classes from colliding.
        PipeName = "RevitMCPBridgeTest-" + Guid.NewGuid().ToString("n")[..12];
        Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        Handler = handler ?? DefaultHandler;

        _sessionPath = Path.Combine(Path.GetTempPath(), $"revitmcp-session-{Guid.NewGuid():n}.json");

        if (writeSessionFile)
        {
            File.WriteAllText(_sessionPath, JsonSerializer.Serialize(new BridgeSessionFile
            {
                Version = protocolVersion,
                PipeName = PipeName,
                Token = Token,
                ProcessId = Environment.ProcessId,
                RevitVersion = "2026",
                StartedUtc = DateTimeOffset.UtcNow
            }, Protocol.Json));
        }

        // BridgeClient reads this path, so pointing it at a temp file keeps the test off any real
        // session file on the machine.
        Environment.SetEnvironmentVariable("REVIT_MCP_SESSION_FILE", _sessionPath);

        _loop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public string PipeName { get; }
    public string Token { get; }
    public Func<BridgeRequest, BridgeResponse> Handler { get; set; }
    public int RequestsSeen { get; private set; }
    public List<string> CommandsSeen { get; } = [];

    /// <summary>Drops the current connection, as closing Revit would.</summary>
    public bool DropNextConnection { get; set; }

    private static BridgeResponse DefaultHandler(BridgeRequest request) =>
        BridgeResponse.Success(request, new JsonObject { ["echo"] = request.Command });

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var frame = await Framing.ReadFrameAsync(pipe, ct);
                    if (frame is null) break;

                    var request = JsonSerializer.Deserialize<BridgeRequest>(frame, Protocol.Json)!;
                    RequestsSeen++;
                    CommandsSeen.Add(request.Command);

                    if (DropNextConnection)
                    {
                        DropNextConnection = false;
                        break;                                   // close without replying
                    }

                    var response = request.Token != Token
                        ? BridgeResponse.Fail(request, BridgeErrorCode.Unauthorized, "Bad token.")
                        : Handler(request);

                    await Framing.WriteFrameAsync(pipe,
                        JsonSerializer.SerializeToUtf8Bytes(response, Protocol.Json), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client vanished; accept the next one */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try { await _loop; } catch (OperationCanceledException) { }
        _shutdown.Dispose();

        Environment.SetEnvironmentVariable("REVIT_MCP_SESSION_FILE", null);
        if (File.Exists(_sessionPath)) File.Delete(_sessionPath);
    }
}
