using System.Text.Json.Nodes;
using RevitMCP.Contracts;

namespace RevitMCPProbe;

/// <summary>Safe navigation into the JSON payloads the bridge returns.</summary>
internal static class JsonNav
{
    /// <summary>The named array's items, or empty when the key is absent or not an array.</summary>
    public static IEnumerable<JsonNode?> Arr(this JsonNode? node, string key) =>
        node?[key] as JsonArray ?? Enumerable.Empty<JsonNode?>();
}

public enum Outcome { Pass, Skip, Fail }

public sealed record CheckResult(string Command, Outcome Outcome, long ElapsedMs, string Detail);

/// <summary>
/// Runs every read-only command against the live bridge and reports what worked.
///
/// The point is the chaining: ids taken from one call feed the next, so the handlers are exercised
/// against elements that actually exist in the open model. That is where handler bugs hide — a
/// hand-written id proves far less.
/// </summary>
public sealed class Probe(RevitMCPServer.BridgeClient client, bool verbose)
{
    private readonly List<CheckResult> _results = [];

    public IReadOnlyList<CheckResult> Results => _results;

    /// <summary>A command that legitimately has nothing to report, rather than a fault.</summary>
    private static readonly BridgeErrorCode[] BenignCodes =
        [BridgeErrorCode.NotFound, BridgeErrorCode.InvalidState];

    public async Task<bool> RunReadChecksAsync(CancellationToken ct)
    {
        // Status first: if the bridge is not answering, nothing else is worth trying.
        var status = await CheckAsync(Commands.BridgeStatus, null, ct);
        if (status is null)
        {
            Console.WriteLine();
            Console.WriteLine("The bridge did not answer BRIDGE_STATUS, so the remaining checks were skipped.");
            Console.WriteLine("Start Revit with the MCP Bridge add-in loaded, then run this again.");
            return false;
        }

        ReportStatus(status);

        await CheckAsync(Commands.GetProjectInfo, null, ct);
        await CheckAsync(Commands.ListPhases, null, ct);
        await CheckAsync(Commands.ListWorksets, null, ct);
        await CheckAsync(Commands.ListLinkedModels, null, ct);
        await CheckAsync(Commands.GetActiveView, null, ct);
        await CheckAsync(Commands.GetSelection, null, ct);
        await CheckAsync(Commands.GetModelWarnings, Args(("limit", 5)), ct);

        // Each of these hands ids to the checks that follow.
        var levels = await CheckAsync(Commands.ListLevels, Args(("limit", 5)), ct);
        await ProbeElementsOfFirstPopulatedCategory(ct);
        await ProbeViewsAndSheets(ct);
        await ProbeFamiliesAndMaterials(ct);
        await ProbeSchedules(ct);

        if (levels is not null) await ProbeLevelParameters(levels, ct);

        return _results.All(r => r.Outcome != Outcome.Fail);
    }

    private void ReportStatus(JsonNode status)
    {
        Console.WriteLine();
        Console.WriteLine($"  Revit           : {status["revitVersion"]} (pid {status["processId"]})");
        Console.WriteLine($"  protocol        : v{status["protocolVersion"]}");
        Console.WriteLine($"  commands        : {status["commandCount"]}");
        Console.WriteLine($"  writes allowed  : {status["writesAllowed"]}");
        Console.WriteLine($"  requests served : {status["requestsHandled"]}");
        Console.WriteLine($"  log             : {status["logPath"]}");

        if (status["lastError"]?.ToString() is { Length: > 0 } lastError && lastError != "null")
            Console.WriteLine($"  last error      : {lastError}");

        Console.WriteLine();
    }

    /// <summary>
    /// Finds a category with elements in it and walks the per-element commands, rather than assuming
    /// the model has walls. A structural-only or MEP model would otherwise report false failures.
    /// </summary>
    private async Task ProbeElementsOfFirstPopulatedCategory(CancellationToken ct)
    {
        var categories = await CheckAsync(Commands.ListCategories,
            Args(("limit", 60), ("nonEmptyOnly", true), ("includeCounts", true)), ct);

        var categoryName = categories.Arr("categories")
            .FirstOrDefault(c => c?["elementCount"]?.GetValue<int>() > 0)
            ?["name"]?.GetValue<string>();

        if (categoryName is null)
        {
            Record(Commands.ListElements, Outcome.Skip, 0, "No populated category found; is a model open?");
            return;
        }

        await CheckAsync(Commands.CountElements, Args(("category", categoryName)), ct);
        var elements = await CheckAsync(Commands.ListElements,
            Args(("category", categoryName), ("limit", 5)), ct);

        var elementId = elements.Arr("elements").FirstOrDefault()?["id"]?.GetValue<long>();
        if (elementId is null)
        {
            Record(Commands.GetElementProperties, Outcome.Skip, 0, "No element id to follow up with.");
            return;
        }

        if (verbose) Console.WriteLine($"    (following element {elementId} in '{categoryName}')");

        await CheckAsync(Commands.GetElementProperties, Args(("elementId", elementId)), ct);
        await CheckAsync(Commands.GetElementTypeProperties, Args(("elementId", elementId)), ct);
        await CheckAsync(Commands.GetElementLocation, Args(("elementId", elementId)), ct);

        // A parameter every element has, so this exercises the search rather than the model's content.
        await CheckAsync(Commands.FindByParam,
            Args(("parameterName", "Comments"), ("category", categoryName),
                 ("match", "exists"), ("limit", 5)), ct);
    }

    private async Task ProbeViewsAndSheets(CancellationToken ct)
    {
        await CheckAsync(Commands.ListViews, Args(("limit", 10)), ct);

        var sheets = await CheckAsync(Commands.ListSheets, Args(("limit", 5)), ct);
        var sheetId = sheets.Arr("sheets").FirstOrDefault()?["id"]?.GetValue<long>();

        if (sheetId is null)
            Record(Commands.GetSheetContents, Outcome.Skip, 0, "This model has no sheets.");
        else
            await CheckAsync(Commands.GetSheetContents, Args(("sheetId", sheetId)), ct);
    }

    private async Task ProbeFamiliesAndMaterials(CancellationToken ct)
    {
        await CheckAsync(Commands.ListFamilies, Args(("limit", 10)), ct);
        await CheckAsync(Commands.ListFamilyTypes, Args(("limit", 10)), ct);

        var materials = await CheckAsync(Commands.ListMaterials, Args(("limit", 5)), ct);
        var materialId = materials.Arr("materials").FirstOrDefault()?["id"]?.GetValue<long>();

        if (materialId is null)
            Record(Commands.GetMaterialProperties, Outcome.Skip, 0, "This model has no materials.");
        else
            await CheckAsync(Commands.GetMaterialProperties, Args(("materialId", materialId)), ct);
    }

    private async Task ProbeSchedules(CancellationToken ct)
    {
        var schedules = await CheckAsync(Commands.GetSchedules, Args(("limit", 5)), ct);
        var count = schedules.Arr("schedules").Count();

        if (verbose && count > 0)
            Console.WriteLine($"    ({count} schedule(s) found; EXPORT_SCHEDULE_TO_CSV writes a file, so it is not run here)");
    }

    private async Task ProbeLevelParameters(JsonNode levels, CancellationToken ct)
    {
        var levelId = levels.Arr("levels").FirstOrDefault()?["id"]?.GetValue<long>();
        if (levelId is null) return;

        // A level is typeless, so this confirms GET_ELEMENT_TYPE_PROPERTIES reports that cleanly
        // instead of throwing — a case a wall would never exercise.
        await CheckAsync(Commands.GetElementTypeProperties, Args(("elementId", levelId)), ct,
            expectBenignFailure: true);
    }

    // --- plumbing ----------------------------------------------------------------------------

    private async Task<JsonNode?> CheckAsync(string command, JsonObject? args, CancellationToken ct,
        bool expectBenignFailure = false)
    {
        var response = await client.CallAsync(command, args, 60_000, ct);

        if (response.Ok)
        {
            Record(command, Outcome.Pass, response.ElapsedMs, Summarise(response.Data));
            return response.Data;
        }

        // A model without worksets or an open view is not a bug in the bridge.
        var benign = expectBenignFailure || BenignCodes.Contains(response.Error);
        Record(command, benign ? Outcome.Skip : Outcome.Fail, response.ElapsedMs,
            $"{response.Error}: {Trim(response.Message)}");

        return null;
    }

    private void Record(string command, Outcome outcome, long ms, string detail)
    {
        _results.Add(new CheckResult(command, outcome, ms, detail));

        var mark = outcome switch
        {
            Outcome.Pass => "  ok  ",
            Outcome.Skip => " skip ",
            _ => " FAIL "
        };

        Console.WriteLine($"[{mark}] {command,-30} {ms,6} ms  {detail}");
    }

    /// <summary>A one-line gist of a payload, enough to see the call returned real content.</summary>
    private static string Summarise(JsonNode? data)
    {
        if (data is not JsonObject o) return "";

        if (o["count"]?.GetValue<int>() is { } count)
        {
            var total = o["totalMatched"]?.GetValue<int>();
            return total is not null && total != count ? $"{count} of {total}" : $"{count}";
        }

        foreach (var key in new[] { "name", "title", "sheetNumber", "viewName" })
        {
            if (o[key]?.ToString() is { Length: > 0 } value && value != "null")
                return Trim(value);
        }

        return o["listening"] is not null ? "listening" : "";
    }

    private static string Trim(string? text) =>
        text is null ? "" : text.Length <= 90 ? text : text[..87] + "...";

    private static JsonObject Args(params (string Name, object Value)[] entries)
    {
        var o = new JsonObject();
        foreach (var (name, value) in entries)
        {
            o[name] = value switch
            {
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                _ => JsonValue.Create(value.ToString())
            };
        }
        return o;
    }
}
