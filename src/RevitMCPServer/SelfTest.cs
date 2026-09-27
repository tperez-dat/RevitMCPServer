using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer;

/// <summary>
/// Checks this executable end to end without an MCP client: which tools it exposes, and whether it
/// can reach the Revit bridge. Splits "the server is broken" from "the client is misconfigured",
/// which are otherwise indistinguishable when a client simply shows no tools.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine($"Revit MCP server self-test  (protocol v{Protocol.Version})");
        Console.WriteLine($"executable   : {Environment.ProcessPath}");
        Console.WriteLine($"session file : {BridgeSessionFile.DefaultPath}");
        Console.WriteLine();

        var toolsOk = ReportTools();
        var bridgeOk = await ReportBridgeAsync();

        Console.WriteLine();
        if (toolsOk && bridgeOk)
        {
            Console.WriteLine("PASS - the server works and Revit is reachable.");
            Console.WriteLine("Point your MCP client at the executable path above.");
            return 0;
        }

        if (toolsOk && !bridgeOk)
        {
            Console.WriteLine("PARTIAL - the server itself is fine, but it could not reach Revit.");
            Console.WriteLine("Start Revit with the MCP Bridge add-in loaded, then run this again.");
            Console.WriteLine("The MCP client will still connect; its tools just fail until Revit is up.");
            return 1;
        }

        Console.WriteLine("FAIL - the server did not register its tools. This build is broken.");
        return 2;
    }

    /// <summary>
    /// Counts the tools by reflection, the same way the SDK discovers them, so a registration
    /// problem shows up here rather than as an empty tool list in the client.
    /// </summary>
    private static bool ReportTools()
    {
        var tools = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"tools registered : {tools.Count}");

        var expected = CommandCatalog.All.Count;
        if (tools.Count != expected)
        {
            Console.WriteLine($"  MISMATCH: the catalogue declares {expected} commands.");
            var missing = CommandCatalog.All.Select(c => c.Name).Except(tools!).ToList();
            if (missing.Count > 0) Console.WriteLine($"  missing: {string.Join(", ", missing)}");
            return false;
        }

        // Print them in columns: seeing the list is what makes this useful when a client shows none.
        for (var i = 0; i < tools.Count; i += 3)
        {
            var row = tools.Skip(i).Take(3).Select(t => t!.PadRight(28));
            Console.WriteLine("  " + string.Concat(row).TrimEnd());
        }

        return true;
    }

    private static async Task<bool> ReportBridgeAsync()
    {
        Console.WriteLine();
        Console.Write("revit bridge     : ");

        await using var client = new BridgeClient(NullLogger<BridgeClient>.Instance);
        var response = await client.CallAsync(Commands.BridgeStatus, null, 10_000, CancellationToken.None);

        if (!response.Ok)
        {
            Console.WriteLine("NOT REACHABLE");
            Console.WriteLine($"  {response.Message}");
            return false;
        }

        var data = response.Data as JsonObject;
        Console.WriteLine("reachable");
        Console.WriteLine($"  Revit          : {data?["revitVersion"]} (pid {data?["processId"]})");
        Console.WriteLine($"  writes allowed : {data?["writesAllowed"]}");
        Console.WriteLine($"  round trip     : {response.ElapsedMs} ms");
        return true;
    }
}
