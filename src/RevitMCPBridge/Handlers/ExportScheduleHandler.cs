using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitMCP.Contracts;

namespace RevitMCPBridge.Handlers;

/// <summary>
/// Exports a schedule to CSV.
///
/// Revit's own <see cref="ViewSchedule.Export"/> writes tab-delimited text by default, so the
/// delimiter is set explicitly — a file named .csv that is not comma-separated is a trap.
/// </summary>
public sealed class ExportScheduleToCsvHandler : IBridgeCommandHandler
{
    /// <summary>Default export location, so a caller need not know anything about the filesystem.</summary>
    private static string DefaultDirectory => Path.Combine(BridgeLog.Directory, "exports");

    public string Command => Commands.ExportScheduleToCsv;

    public JsonNode? Execute(CommandContext context)
    {
        var doc = context.Doc;
        var args = context.Args;

        var schedule = Resolve(context, doc);
        var (directory, fileName) = ResolveOutputPath(args, schedule);

        Directory.CreateDirectory(directory);

        var options = new ViewScheduleExportOptions
        {
            FieldDelimiter = args.StringOr("delimiter", ","),
            TextQualifier = ParseQualifier(args.StringOr("textQualifier", "doubleQuote")),
            Title = args.Bool("includeTitle"),
            ColumnHeaders = args.Bool("includeColumnHeaders", true)
                ? ExportColumnHeaders.OneRow
                : ExportColumnHeaders.None,
            HeadersFootersBlanks = args.Bool("includeHeadersFootersBlanks")
        };

        var fullPath = Path.Combine(directory, fileName);

        try
        {
            schedule.Export(directory, fileName, options);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            throw new BridgeException(BridgeErrorCode.RevitException,
                $"Revit could not export '{Json.SafeName(schedule)}'. The folder may be read-only, " +
                "or the file may be open in another program.", ex.Message);
        }

        if (!File.Exists(fullPath))
        {
            throw new BridgeException(BridgeErrorCode.Internal,
                $"Revit reported no error but no file appeared at {fullPath}.");
        }

        var info = new FileInfo(fullPath);

        return new JsonObject
        {
            ["exported"] = true,
            ["scheduleId"] = schedule.Id.Value,
            ["scheduleName"] = Json.SafeName(schedule),
            ["path"] = fullPath,
            ["sizeBytes"] = info.Length,
            ["rowsEstimated"] = CountLines(fullPath),
            ["delimiter"] = options.FieldDelimiter
        };
    }

    private static int? CountLines(string path)
    {
        // A row count is the quickest sanity check that the export holds what was expected.
        try
        {
            var lines = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is not null) lines++;
            return lines;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ExportTextQualifier ParseQualifier(string raw) => raw.ToLowerInvariant() switch
    {
        "none" => ExportTextQualifier.None,
        "quote" or "doublequote" => ExportTextQualifier.DoubleQuote,
        _ => throw new BridgeException(BridgeErrorCode.BadRequest,
            $"'textQualifier' must be 'none' or 'doubleQuote' (got '{raw}').")
    };

    /// <summary>
    /// Works out where to write. A bare file name lands in the bridge's own exports folder; an
    /// absolute path is honoured. Traversal and non-CSV extensions are refused, because this command
    /// is otherwise a read-only tool that can write a file anywhere the Revit process can reach.
    /// </summary>
    private static (string Directory, string FileName) ResolveOutputPath(ArgReader args, ViewSchedule schedule)
    {
        var requested = args.StringOrNull("path");

        if (requested is null)
        {
            return (DefaultDirectory, SafeFileName(Json.SafeName(schedule) ?? "schedule") + ".csv");
        }

        if (requested.Contains("..", StringComparison.Ordinal))
            throw new BridgeException(BridgeErrorCode.BadRequest,
                "'path' must not contain '..'. Give a file name or a full path.");

        string directory, fileName;

        if (Path.IsPathRooted(requested))
        {
            directory = Path.GetDirectoryName(requested)
                        ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                            $"'{requested}' has no directory part.");
            fileName = Path.GetFileName(requested);
        }
        else
        {
            directory = DefaultDirectory;
            fileName = requested;
        }

        if (string.IsNullOrWhiteSpace(fileName))
            throw new BridgeException(BridgeErrorCode.BadRequest, "'path' has no file name.");

        if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'path' must end in .csv (got '{fileName}').");

        if (fileName.Intersect(Path.GetInvalidFileNameChars()).Any())
            throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{fileName}' contains characters that are not valid in a file name.");

        return (directory, fileName);
    }

    private static string SafeFileName(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "schedule" : cleaned;
    }

    /// <summary>Accepts a schedule id or an exact schedule name.</summary>
    private static ViewSchedule Resolve(CommandContext context, Document doc)
    {
        if (context.Args.LongOrNull("scheduleId") is { } id)
        {
            return doc.GetElement(new ElementId(id)) as ViewSchedule
                   ?? throw new BridgeException(BridgeErrorCode.NotFound,
                       $"Element {id} is not a schedule.");
        }

        var name = context.Args.StringOrNull("name")
                   ?? throw new BridgeException(BridgeErrorCode.BadRequest,
                       "Pass 'scheduleId' or 'name' to identify the schedule.");

        var matches = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(s => !s.IsTemplate)
            .Where(s => string.Equals(Json.SafeName(s), name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BridgeException(BridgeErrorCode.NotFound,
                $"No schedule is named '{name}'. Call GET_SCHEDULES to see what exists."),
            _ => throw new BridgeException(BridgeErrorCode.BadRequest,
                $"'{name}' matches {matches.Count} schedules. Pass 'scheduleId' instead.")
        };
    }
}
