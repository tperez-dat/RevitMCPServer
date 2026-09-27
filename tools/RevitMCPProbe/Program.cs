using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RevitMCP.Contracts;
using RevitMCPProbe;

// Verifies a live bridge end to end. Run it after installing the add-in, before wiring up an MCP
// client: it isolates "is the bridge working" from "is my MCP config right".

var wantsWrites = args.Contains("--writes");
var verbose = args.Contains("--verbose") || args.Contains("-v");

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        RevitMCPProbe - checks a running Revit MCP bridge.

          RevitMCPProbe                 run every read-only check
          RevitMCPProbe --writes        also run a self-cleaning write check
          RevitMCPProbe --verbose       show which ids the checks followed
          RevitMCPProbe --help          this text

        Read checks never modify the model. The write check creates a level, renames it, and
        deletes it again; it needs 'Allow MCP Writes' enabled on the Revit ribbon.

        Exit code is 0 when nothing failed, 1 otherwise.
        """);
    return 0;
}

Console.WriteLine($"Revit MCP bridge probe - protocol v{Protocol.Version}");
Console.WriteLine($"session file: {BridgeSessionFile.DefaultPath}");

await using var client = new RevitMCPServer.BridgeClient(
    NullLogger<RevitMCPServer.BridgeClient>.Instance);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var probe = new Probe(client, verbose);
var readsPassed = await probe.RunReadChecksAsync(cancellation.Token);

var writesPassed = true;
if (readsPassed && wantsWrites)
{
    Console.WriteLine();
    Console.WriteLine("--- write check (creates a level, renames it, deletes it) ---");
    writesPassed = await RunWriteCheckAsync(client, cancellation.Token);
}
else if (wantsWrites)
{
    Console.WriteLine();
    Console.WriteLine("Skipping the write check because read checks did not all pass.");
}

// --- summary -------------------------------------------------------------------------------

var results = probe.Results;
var passed = results.Count(r => r.Outcome == Outcome.Pass);
var skipped = results.Count(r => r.Outcome == Outcome.Skip);
var failed = results.Count(r => r.Outcome == Outcome.Fail);

Console.WriteLine();
Console.WriteLine($"{passed} passed, {skipped} skipped, {failed} failed");

if (skipped > 0)
{
    Console.WriteLine();
    Console.WriteLine("Skipped (not necessarily a problem - the model may simply not have these):");
    foreach (var result in results.Where(r => r.Outcome == Outcome.Skip))
        Console.WriteLine($"  {result.Command}: {result.Detail}");
}

if (failed > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failed:");
    foreach (var result in results.Where(r => r.Outcome == Outcome.Fail))
        Console.WriteLine($"  {result.Command}: {result.Detail}");

    Console.WriteLine();
    Console.WriteLine("Send these lines back along with %LOCALAPPDATA%\\RevitMCPBridge\\bridge.log.");
}

return failed == 0 && writesPassed ? 0 : 1;

// --- write check ---------------------------------------------------------------------------

/// <summary>
/// Exercises the write path without leaving anything behind: create a level at a deliberately odd
/// elevation, rename it through SET_ELEMENT_PARAMETER, then delete it. If any step fails the
/// created level is still cleaned up, so a failed probe does not litter the model.
/// </summary>
static async Task<bool> RunWriteCheckAsync(RevitMCPServer.BridgeClient client, CancellationToken ct)
{
    const double probeElevation = 1234.5;   // far from any real level
    long? levelId = null;

    try
    {
        var created = await client.CallAsync(Commands.CreateLevel, new JsonObject
        {
            ["elevation"] = probeElevation,
            ["name"] = $"MCP probe {DateTime.Now:HHmmss}"
        }, 60_000, ct);

        if (!created.Ok)
        {
            Console.WriteLine($"[ FAIL ] CREATE_LEVEL  {created.Error}: {created.Message}");

            if (created.Error == BridgeErrorCode.WriteNotPermitted)
                Console.WriteLine("         Enable 'Allow MCP Writes' on the MCP Bridge ribbon panel first.");

            return false;
        }

        levelId = created.Data?["id"]?.GetValue<long>();
        Console.WriteLine($"[  ok  ] CREATE_LEVEL             created level {levelId} " +
                          $"at {created.Data?["elevation"]} ft");

        if (levelId is null)
        {
            Console.WriteLine("[ FAIL ] CREATE_LEVEL returned no element id.");
            return false;
        }

        var renamed = await client.CallAsync(Commands.SetElementParameter, new JsonObject
        {
            ["elementIds"] = new JsonArray { levelId.Value },
            ["parameterName"] = "Name",
            ["value"] = $"MCP probe renamed {DateTime.Now:HHmmss}"
        }, 60_000, ct);

        if (renamed.Ok)
        {
            var first = (renamed.Data?["updated"] as JsonArray)?.FirstOrDefault();
            Console.WriteLine($"[  ok  ] SET_ELEMENT_PARAMETER   '{first?["before"]}' -> '{first?["after"]}'");
        }
        else
        {
            Console.WriteLine($"[ FAIL ] SET_ELEMENT_PARAMETER  {renamed.Error}: {renamed.Message}");
            return false;
        }

        // Dry run first: it is the tool's own safety feature, so it should be exercised too.
        var dryRun = await client.CallAsync(Commands.DeleteElement, new JsonObject
        {
            ["elementIds"] = new JsonArray { levelId.Value },
            ["dryRun"] = true
        }, 60_000, ct);

        Console.WriteLine(dryRun.Ok
            ? $"[  ok  ] DELETE_ELEMENT (dry run) would delete {dryRun.Data?["requestedCount"]}"
            : $"[ FAIL ] DELETE_ELEMENT (dry run)  {dryRun.Error}: {dryRun.Message}");

        return dryRun.Ok;
    }
    finally
    {
        if (levelId is not null)
        {
            var deleted = await client.CallAsync(Commands.DeleteElement, new JsonObject
            {
                ["elementIds"] = new JsonArray { levelId.Value }
            }, 60_000, CancellationToken.None);

            Console.WriteLine(deleted.Ok
                ? $"[  ok  ] DELETE_ELEMENT          removed the probe level " +
                  $"({deleted.Data?["deletedCount"]} element(s))"
                : $"[ WARN ] could not delete the probe level {levelId}: {deleted.Message}. " +
                  "Delete it by hand, or press Ctrl+Z in Revit.");
        }
    }
}
