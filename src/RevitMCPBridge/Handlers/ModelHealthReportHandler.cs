using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Runs every health check and returns one report.
///
/// Three reasons this exists rather than leaving a caller to make a dozen calls. It is a single
/// snapshot, taken inside one API context, so the numbers are consistent with each other. It cannot
/// be half-performed, which a model asked to "run a health check" otherwise does. And it is the row
/// source for a dashboard: one call per model produces one record.
///
/// It delegates to the individual handlers rather than reimplementing them, so the two can never
/// disagree. Each section is isolated: one that throws is reported and the rest still run, because
/// a report missing one section is far more useful than no report.
///
/// Detail lists are deliberately capped. This answers "how bad is it, and what does it look like" —
/// the per-check tools answer "show me all of them".
/// </summary>
public sealed class GetModelHealthHandler(IReadOnlyDictionary<string, IBridgeCommandHandler> handlers)
    : IBridgeCommandHandler
{
    private const int DefaultDetailLimit = 25;

    /// <summary>Section name to the command that produces it. Section names are the caller's API.</summary>
    private static readonly (string Section, string Command)[] Sections =
    [
        ("project", Commands.GetProjectInfo),
        ("warnings", Commands.GetModelWarnings),
        ("purgeable", Commands.GetPurgeable),
        ("cadImports", Commands.GetCadImports),
        ("images", Commands.ListImages),
        ("groups", Commands.ListGroups),
        ("inPlaceFamilies", Commands.ListInPlaceFamilies),
        ("designOptions", Commands.ListDesignOptions),
        ("unplacedViews", Commands.ListUnplacedViews),
        ("links", Commands.ListLinkedModels),
        ("worksets", Commands.ListWorksets),
        ("revisions", Commands.GetRevisions),
        ("filters", Commands.ListFilters),
        ("viewTemplates", Commands.ListViewTemplates),
        ("sharedParameters", Commands.ListSharedParameters),
        ("spatialElements", Commands.ListRoomAreaSpaceStatus)
    ];

    public string Command => Commands.GetModelHealth;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var detailLimit = args.IntOrNull("detailLimit") ?? DefaultDetailLimit;
        var wanted = RequestedSections(args);

        var results = new JsonObject();
        var failures = new JsonArray();
        var metrics = new JsonObject();
        var missingMetrics = new JsonArray();
        var overall = Stopwatch.StartNew();

        foreach (var (section, command) in Sections)
        {
            if (wanted is not null && !wanted.Contains(section)) continue;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var payload = Run(context, command, detailLimit);
                var entry = new JsonObject
                {
                    ["ok"] = true,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["data"] = payload
                };

                results[section] = entry;
                Summarise(section, payload, metrics, missingMetrics);
            }
            catch (Exception ex)
            {
                // A section that throws must not cost the whole report.
                results[section] = new JsonObject
                {
                    ["ok"] = false,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["error"] = ex.Message
                };

                failures.Add(new JsonObject { ["section"] = section, ["error"] = ex.Message });
            }
        }

        return new JsonObject
        {
            ["generatedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["elapsedMs"] = overall.ElapsedMilliseconds,
            ["model"] = DescribeModel(doc),
            // The headline numbers, flat and named, so they can go straight onto a dashboard.
            ["summary"] = metrics,
            ["sections"] = results,
            ["failures"] = failures,
            // Non-empty means a handler's output changed shape and a summary number is now
            // absent. Surfaced rather than swallowed, so it shows up the first time it happens.
            ["missingMetrics"] = missingMetrics,
            ["detailLimit"] = detailLimit,
            ["note"] = "Detail lists are capped at detailLimit. Counts in 'summary' are totals for " +
                       "the whole model, not of the sample. Call the individual tool for a full list."
        };
    }

    // --- running a section -------------------------------------------------------------------

    /// <summary>
    /// Invokes another handler with its own arguments. Reuse rather than reimplementation: the
    /// report and the individual tool cannot drift apart if they are the same code.
    /// </summary>
    private JsonNode? Run(CommandContext outer, string command, int detailLimit)
    {
        if (!handlers.TryGetValue(command, out var handler))
            throw new BridgeException(BridgeErrorCode.Internal, $"No handler for '{command}'.");

        var args = new JsonObject();

        // Only the paged commands take a limit; the others ignore it harmlessly, but being
        // explicit keeps the intent readable.
        if (command is not (Commands.GetProjectInfo or Commands.ListDesignOptions
                            or Commands.GetRevisions))
            args["limit"] = detailLimit;

        var request = new BridgeRequest
        {
            Id = outer.Request.Id,
            Command = command,
            Args = args,
            TimeoutMs = outer.Request.TimeoutMs
        };

        return handler.Execute(new CommandContext(outer.UiApp, request));
    }

    private static HashSet<string>? RequestedSections(ArgReader args)
    {
        if (!args.Has("sections")) return null;

        var requested = args.Array("sections");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in requested)
        {
            if (node?.GetValue<string>() is { Length: > 0 } name) names.Add(name);
        }

        if (names.Count == 0) return null;

        var known = Sections.Select(s => s.Section).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = names.Where(n => !known.Contains(n)).ToList();

        if (unknown.Count > 0)
        {
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"Unknown section(s): {string.Join(", ", unknown)}. Valid sections: " +
                string.Join(", ", Sections.Select(s => s.Section)));
        }

        return names;
    }

    // --- headline numbers --------------------------------------------------------------------

    /// <summary>
    /// Lifts each section's totals into one flat summary. Keys are read explicitly rather than
    /// guessed, so a rename in a handler surfaces as a missing metric instead of a silent zero.
    /// </summary>
    private static void Summarise(string section, JsonNode? payload, JsonObject metrics,
        JsonArray missing)
    {
        if (payload is not JsonObject o) return;

        // Takes the first source key present. Several sections report a total under one name on
        // the normal path and another in a degenerate case - a non-workshared model's workset
        // payload has a count but no totalMatched - so a fallback is correctness, not laziness.
        // A metric that matches nothing is recorded, because a silently absent number on a
        // dashboard is worse than a loud one.
        void Copy(string to, params string[] from)
        {
            foreach (var key in from)
            {
                if (o[key] is not { } value) continue;
                metrics[to] = value.DeepClone();
                return;
            }

            missing.Add(new JsonObject
            {
                ["metric"] = to,
                ["section"] = section,
                ["lookedFor"] = string.Join(", ", from)
            });
        }

        switch (section)
        {
            case "warnings":
                Copy("warnings", "totalWarnings");
                break;
            case "purgeable":
                Copy("purgeable", "totalPurgeable");
                break;
            case "cadImports":
                Copy("cadImports", "importedCount");
                Copy("cadLinks", "linkedCount");
                break;
            case "images":
                Copy("images", "totalMatched", "count");
                Copy("imagesUnplaced", "unplacedCount");
                break;
            case "groups":
                Copy("groupTypes", "totalMatched", "count");
                Copy("groupTypesUnused", "unusedCount");
                Copy("groupTypesUsedOnce", "usedOnceCount");
                Copy("groupInstances", "totalInstances");
                break;
            case "inPlaceFamilies":
                Copy("inPlaceFamilies", "totalInPlace");
                break;
            case "designOptions":
                Copy("designOptionSets", "setCount");
                Copy("designOptions", "optionCount");
                break;
            case "unplacedViews":
                Copy("unplacedViews", "totalUnplaced");
                break;
            case "links":
                Copy("revitLinks", "count");
                break;
            case "worksets":
                Copy("worksets", "totalMatched", "count");
                break;
            case "revisions":
                Copy("revisions", "count");
                Copy("revisionsWithNoClouds", "withNoCloudsCount");
                break;
            case "filters":
                Copy("viewFilters", "totalMatched", "count");
                Copy("viewFiltersUnused", "unusedCount");
                break;
            case "viewTemplates":
                Copy("viewTemplates", "totalMatched", "count");
                Copy("viewTemplatesUnused", "unusedCount");
                Copy("viewsWithNoTemplate", "viewsWithNoTemplate");
                break;
            case "sharedParameters":
                Copy("boundParameters", "totalMatched", "count");
                Copy("sharedParameters", "sharedCount");
                break;
            case "spatialElements":
                Copy("spatialProblems", "totalProblems");
                break;
        }
    }

    // --- model identity ----------------------------------------------------------------------

    /// <summary>
    /// What identifies this model as a row in a dataset: where it is, how big, when it was last
    /// written, and which Revit produced it.
    /// </summary>
    private static JsonObject DescribeModel(Document doc)
    {
        var application = doc.Application;

        var model = new JsonObject
        {
            ["title"] = doc.Title,
            ["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName,
            ["isWorkshared"] = doc.IsWorkshared,
            ["isFamilyDocument"] = doc.IsFamilyDocument,
            ["isReadOnly"] = doc.IsReadOnly,
            ["revitVersion"] = application.VersionNumber,
            ["revitBuild"] = application.VersionBuild,
            ["elementCount"] = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount()
        };

        // File size and timestamp come from disk, not the API. A cloud model has no local file, so
        // these are reported as null rather than invented.
        try
        {
            if (!string.IsNullOrEmpty(doc.PathName) && File.Exists(doc.PathName))
            {
                var info = new FileInfo(doc.PathName);
                model["fileSizeBytes"] = info.Length;
                model["fileSizeMb"] = Math.Round(info.Length / 1024.0 / 1024.0, 2);
                model["fileLastWrittenUtc"] = info.LastWriteTimeUtc.ToString("O");
            }
        }
        catch (Exception ex)
        {
            model["fileSizeError"] = ex.Message;
        }

        if (doc.IsWorkshared)
        {
            try
            {
                var central = doc.GetWorksharingCentralModelPath();
                if (central is not null)
                {
                    var visible = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                    model["centralPath"] = string.IsNullOrWhiteSpace(visible) ? null : visible;
                }
            }
            catch (Exception)
            {
                // A detached model has no central path.
            }
        }

        return model;
    }
}
