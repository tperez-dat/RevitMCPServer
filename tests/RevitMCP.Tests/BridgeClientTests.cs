using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Exercises the real <see cref="BridgeClient"/> against <see cref="FakeBridge"/>: the transport,
/// the token handshake, version checking, and recovery from a dropped connection.
/// </summary>
[Collection("pipe")]     // these share the session-file environment variable
public class BridgeClientTests
{
    private static RevitMCPServer.BridgeClient NewClient() =>
        new(NullLogger<RevitMCPServer.BridgeClient>.Instance);

    [Fact]
    public async Task CarriesACommandAndItsReplyAcrossThePipe()
    {
        await using var bridge = new FakeBridge();
        await using var client = NewClient();

        var response = await client.CallAsync(Commands.GetProjectInfo, null, 10_000, default);

        Assert.True(response.Ok);
        Assert.Equal(Commands.GetProjectInfo, response.Data!["echo"]!.GetValue<string>());
        Assert.Equal(1, bridge.RequestsSeen);
    }

    [Fact]
    public async Task SendsTheTokenFromTheSessionFile()
    {
        string? seen = null;

        await using var bridge = new FakeBridge(request =>
        {
            seen = request.Token;
            return BridgeResponse.Success(request, new JsonObject());
        });
        await using var client = NewClient();

        var response = await client.CallAsync(Commands.BridgeStatus, null, 10_000, default);

        Assert.True(response.Ok);
        Assert.Equal(bridge.Token, seen);
    }

    [Fact]
    public async Task PassesArgumentsThrough()
    {
        JsonObject? seen = null;

        await using var bridge = new FakeBridge(request =>
        {
            seen = request.Args;
            return BridgeResponse.Success(request, new JsonObject());
        });
        await using var client = NewClient();

        var args = new JsonObject { ["category"] = "Walls", ["limit"] = 25 };
        await client.CallAsync(Commands.ListElements, args, 10_000, default);

        Assert.NotNull(seen);
        Assert.Equal("Walls", seen!["category"]!.GetValue<string>());
        Assert.Equal(25, seen["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReportsABridgeSideFailureWithoutThrowing()
    {
        await using var bridge = new FakeBridge(request =>
            BridgeResponse.Fail(request, BridgeErrorCode.WriteNotPermitted, "Write mode is off."));
        await using var client = NewClient();

        var response = await client.CallAsync(Commands.CreateWall, null, 10_000, default);

        Assert.False(response.Ok);
        Assert.Equal(BridgeErrorCode.WriteNotPermitted, response.Error);
        Assert.Equal("Write mode is off.", response.Message);
    }

    [Fact]
    public async Task ReconnectsAfterTheBridgeDropsTheConnection()
    {
        await using var bridge = new FakeBridge();
        await using var client = NewClient();

        // Establish the connection, then have the bridge hang up mid-request, as closing Revit does.
        Assert.True((await client.CallAsync(Commands.BridgeStatus, null, 10_000, default)).Ok);

        bridge.DropNextConnection = true;
        var afterDrop = await client.CallAsync(Commands.GetProjectInfo, null, 10_000, default);

        Assert.True(afterDrop.Ok);
        Assert.True(bridge.RequestsSeen >= 3);   // first call, the dropped one, and the retry
    }

    [Fact]
    public async Task ExplainsAMissingSessionFileRatherThanThrowing()
    {
        await using var bridge = new FakeBridge(writeSessionFile: false);
        await using var client = NewClient();

        var response = await client.CallAsync(Commands.GetProjectInfo, null, 5_000, default);

        Assert.False(response.Ok);
        Assert.Contains("session file", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesAMismatchedProtocolVersion()
    {
        await using var bridge = new FakeBridge(protocolVersion: Protocol.Version + 1);
        await using var client = NewClient();

        var response = await client.CallAsync(Commands.GetProjectInfo, null, 5_000, default);

        Assert.False(response.Ok);
        Assert.Contains("protocol", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SurvivesManySequentialCalls()
    {
        await using var bridge = new FakeBridge();
        await using var client = NewClient();

        for (var i = 0; i < 25; i++)
            Assert.True((await client.CallAsync(Commands.ListLevels, null, 10_000, default)).Ok);

        Assert.Equal(25, bridge.RequestsSeen);
    }

    [Fact]
    public async Task SerialisesConcurrentCallersOntoTheSinglePipe()
    {
        await using var bridge = new FakeBridge();
        await using var client = NewClient();

        // Revit is single-threaded, so the client must not interleave frames from parallel callers.
        var calls = Enumerable.Range(0, 12)
            .Select(_ => client.CallAsync(Commands.ListLevels, null, 10_000, default));

        var responses = await Task.WhenAll(calls);

        Assert.All(responses, r => Assert.True(r.Ok));
        Assert.Equal(12, bridge.RequestsSeen);
    }
}

/// <summary>Keeps pipe tests off the same environment variable at the same time.</summary>
[CollectionDefinition("pipe", DisableParallelization = true)]
public class PipeCollection;

/// <summary>
/// SET_ELEMENT_PARAMETER decides between display units and raw internal units from the JSON value's
/// kind, so a number must not arrive as a string. These pin that down on the wire.
/// </summary>
[Collection("pipe")]
public class ParameterPayloadTests
{
    private static RevitMCPServer.BridgeClient NewClient() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<RevitMCPServer.BridgeClient>.Instance);

    [Fact]
    public async Task SendsARawValueAsAJsonNumber()
    {
        JsonNode? seen = null;

        await using var bridge = new FakeBridge(request =>
        {
            seen = request.Args!["value"];
            return BridgeResponse.Success(request, new JsonObject());
        });
        await using var client = NewClient();

        var args = new JsonObject
        {
            ["elementIds"] = new JsonArray { 1L },
            ["parameterName"] = "Unconnected Height",
            ["value"] = JsonValue.Create(8.5)
        };

        await client.CallAsync(Commands.SetElementParameter, args, 10_000, default);

        Assert.NotNull(seen);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, seen!.GetValueKind());
        Assert.Equal(8.5, seen.GetValue<double>());
    }

    [Fact]
    public async Task SendsATextValueAsAJsonString()
    {
        JsonNode? seen = null;

        await using var bridge = new FakeBridge(request =>
        {
            seen = request.Args!["value"];
            return BridgeResponse.Success(request, new JsonObject());
        });
        await using var client = NewClient();

        var args = new JsonObject
        {
            ["elementIds"] = new JsonArray { 1L },
            ["parameterName"] = "Unconnected Height",
            ["value"] = JsonValue.Create("8' 6\"")
        };

        await client.CallAsync(Commands.SetElementParameter, args, 10_000, default);

        Assert.NotNull(seen);
        Assert.Equal(System.Text.Json.JsonValueKind.String, seen!.GetValueKind());
        Assert.Equal("8' 6\"", seen.GetValue<string>());
    }

    [Fact]
    public async Task PreservesElementIdsAsNumbersNotStrings()
    {
        JsonArray? seen = null;

        await using var bridge = new FakeBridge(request =>
        {
            seen = request.Args!["elementIds"] as JsonArray;
            return BridgeResponse.Success(request, new JsonObject());
        });
        await using var client = NewClient();

        var args = new JsonObject { ["elementIds"] = new JsonArray { 12345L, 67890L } };
        await client.CallAsync(Commands.DeleteElement, args, 10_000, default);

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Count);
        Assert.Equal(12345L, seen[0]!.GetValue<long>());
        Assert.Equal(67890L, seen[1]!.GetValue<long>());
    }
}
