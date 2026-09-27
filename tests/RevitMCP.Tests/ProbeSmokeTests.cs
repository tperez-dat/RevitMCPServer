using System.Text.Json.Nodes;
using RevitMCP.Contracts;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Drives the probe's chaining against a stub bridge that answers with realistic payload shapes.
/// The probe's whole value is that it follows ids from one call into the next, so that wiring is
/// what needs testing — a broken chain would silently probe nothing.
/// </summary>
[Collection("pipe")]
public class ProbeSmokeTests
{
    /// <summary>Answers each read command with the shape the real handlers produce.</summary>
    private static BridgeResponse StubHandler(BridgeRequest request) => request.Command switch
    {
        Commands.BridgeStatus => Ok(request, new JsonObject
        {
            ["listening"] = true,
            ["protocolVersion"] = Protocol.Version,
            ["revitVersion"] = "2026",
            ["processId"] = 4242,
            ["commandCount"] = CommandCatalog.All.Count,
            ["writesAllowed"] = false,
            ["requestsHandled"] = 1,
            ["lastError"] = null,
            ["logPath"] = @"C:\log\bridge.log"
        }),

        Commands.ListCategories => Ok(request, new JsonObject
        {
            ["count"] = 3,
            ["totalMatched"] = 3,
            ["categories"] = new JsonArray
            {
                // Areas sorts first and is typeless, exactly as in a real model. The probe should
                // look past it to a category whose elements have types and geometry.
                new JsonObject { ["id"] = -2000080, ["name"] = "Areas", ["elementCount"] = 12 },
                new JsonObject { ["id"] = -2000011, ["name"] = "Walls", ["elementCount"] = 7 },
                new JsonObject { ["id"] = -2000023, ["name"] = "Floors", ["elementCount"] = 3 }
            }
        }),

        Commands.ListElements => Ok(request, new JsonObject
        {
            ["count"] = 1,
            ["totalMatched"] = 7,
            ["elements"] = new JsonArray
            {
                new JsonObject { ["id"] = 9001, ["name"] = "Generic - 8\"", ["category"] = "Walls" }
            }
        }),

        Commands.ListLevels => Ok(request, new JsonObject
        {
            ["count"] = 1,
            ["levels"] = new JsonArray
            {
                new JsonObject { ["id"] = 311, ["name"] = "Level 1", ["elevation"] = 0.0 }
            }
        }),

        Commands.ListSheets => Ok(request, new JsonObject
        {
            ["count"] = 1,
            ["sheets"] = new JsonArray
            {
                new JsonObject { ["id"] = 555, ["sheetNumber"] = "A-101", ["name"] = "Plans" }
            }
        }),

        Commands.ListMaterials => Ok(request, new JsonObject
        {
            ["count"] = 1,
            ["materials"] = new JsonArray
            {
                new JsonObject { ["id"] = 777, ["name"] = "Concrete" }
            }
        }),

        Commands.GetSchedules => Ok(request, new JsonObject
        {
            ["count"] = 1,
            ["schedules"] = new JsonArray { new JsonObject { ["id"] = 888, ["name"] = "Door Schedule" } }
        }),

        // A level has no type, which is the benign failure the probe expects to see reported as a skip.
        Commands.GetElementTypeProperties when request.Args?["elementId"]?.GetValue<long>() == 311 =>
            BridgeResponse.Fail(request, BridgeErrorCode.NotFound, "Element 311 has no element type."),

        // Not workshared: also benign.
        Commands.ListWorksets => Ok(request, new JsonObject
        {
            ["isWorkshared"] = false, ["count"] = 0, ["worksets"] = new JsonArray()
        }),

        _ => Ok(request, new JsonObject { ["count"] = 0, ["name"] = "stub" })
    };

    private static BridgeResponse Ok(BridgeRequest request, JsonNode data) =>
        BridgeResponse.Success(request, data);

    [Fact]
    public async Task ProbeFollowsIdsThroughTheWholeReadSequence()
    {
        var seen = new List<string>();

        await using var bridge = new FakeBridge(request =>
        {
            seen.Add(request.Command);
            return StubHandler(request);
        });

        await using var client = new RevitMCPServer.BridgeClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RevitMCPServer.BridgeClient>.Instance);

        var probe = new RevitMCPProbe.Probe(client, verbose: false);
        var passed = await probe.RunReadChecksAsync(default);

        Assert.True(passed, "No check should fail against a stub that answers everything.");

        // The chained commands only run if the probe successfully read an id out of the previous
        // response, so their presence proves the chaining works.
        Assert.Contains(Commands.GetElementProperties, seen);
        Assert.Contains(Commands.GetElementLocation, seen);
        Assert.Contains(Commands.GetSheetContents, seen);
        Assert.Contains(Commands.GetMaterialProperties, seen);
        Assert.Contains(Commands.CountElements, seen);
        Assert.Contains(Commands.FindByParam, seen);

        // Every read command in the catalogue except the two with side effects should be covered.
        var expected = CommandCatalog.All
            .Where(c => c.Kind == CommandKind.Read)
            .Select(c => c.Name)
            .Except([Commands.ExportScheduleToCsv])   // writes a file, so the probe leaves it alone
            .ToList();

        var missed = expected.Except(seen).ToList();
        Assert.True(missed.Count == 0, $"Probe never exercised: {string.Join(", ", missed)}");
    }

    [Fact]
    public async Task ProbePrefersACategoryWhoseElementsHaveTypes()
    {
        string? requestedCategory = null;

        await using var bridge = new FakeBridge(request =>
        {
            if (request.Command == Commands.ListElements)
                requestedCategory ??= request.Args?["category"]?.GetValue<string>();
            return StubHandler(request);
        });

        await using var client = new RevitMCPServer.BridgeClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RevitMCPServer.BridgeClient>.Instance);

        await new RevitMCPProbe.Probe(client, verbose: false).RunReadChecksAsync(default);

        // Areas sorts first but is typeless, so following it would prove little about the
        // type and location handlers.
        Assert.Equal("Walls", requestedCategory);
    }

    [Fact]
    public async Task ProbeReportsBenignFailuresAsSkipsNotFailures()
    {
        await using var bridge = new FakeBridge(StubHandler);
        await using var client = new RevitMCPServer.BridgeClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RevitMCPServer.BridgeClient>.Instance);

        var probe = new RevitMCPProbe.Probe(client, verbose: false);
        await probe.RunReadChecksAsync(default);

        // The typeless level is a NotFound, which must not be counted as a bridge fault.
        Assert.Contains(probe.Results, r => r.Outcome == RevitMCPProbe.Outcome.Skip);
        Assert.DoesNotContain(probe.Results, r => r.Outcome == RevitMCPProbe.Outcome.Fail);
    }

    [Fact]
    public async Task ProbeFailsLoudlyWhenTheBridgeIsAbsent()
    {
        await using var bridge = new FakeBridge(writeSessionFile: false);
        await using var client = new RevitMCPServer.BridgeClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RevitMCPServer.BridgeClient>.Instance);

        var probe = new RevitMCPProbe.Probe(client, verbose: false);

        Assert.False(await probe.RunReadChecksAsync(default));
    }
}
