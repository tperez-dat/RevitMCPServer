using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Contracts;

namespace RevitMCPServer.Tools;

/// <summary>Export and outside-Revit integration.</summary>
[McpServerToolType]
public sealed class ExportTools(ToolGateway gateway, PdfGeometryExtractor pdf)
{
    [McpServerTool(Name = Commands.ExportScheduleToCsv)]
    [Description("Exports a schedule to a CSV file and returns the path, size and row count. " +
                 "Identify the schedule by id or exact name — call GET_SCHEDULES to find it. " +
                 "With no path, the file lands in the bridge's exports folder under LOCALAPPDATA. " +
                 "The delimiter is set explicitly, since Revit's own exporter writes tab-separated " +
                 "text by default.")]
    public Task<string> ExportScheduleToCsv(
        CancellationToken ct,
        [Description("The schedule's element id.")] long? scheduleId = null,
        [Description("The schedule's exact name, instead of scheduleId.")] string? name = null,
        [Description("Output file name, or a full path. Must end in .csv.")] string? path = null,
        [Description("Field delimiter. Default ','.")] string delimiter = ",",
        [Description("Text qualifier: 'doubleQuote' (default) or 'none'.")] string textQualifier = "doubleQuote",
        [Description("Include the schedule's title row. Default false.")] bool includeTitle = false,
        [Description("Include column headers. Default true.")] bool includeColumnHeaders = true,
        [Description("Include group headers, footers and blank rows. Default false.")]
        bool includeHeadersFootersBlanks = false) =>
        gateway.CallAsync(Commands.ExportScheduleToCsv, ToolGateway.Args(
            ("scheduleId", scheduleId), ("name", name), ("path", path),
            ("delimiter", delimiter), ("textQualifier", textQualifier),
            ("includeTitle", includeTitle), ("includeColumnHeaders", includeColumnHeaders),
            ("includeHeadersFootersBlanks", includeHeadersFootersBlanks)), ct,
            timeoutMs: 120_000);

    [McpServerTool(Name = Commands.ExtractPdfGeometry)]
    [Description("""
        Extracts vector geometry and positioned text from a PDF: lines, rectangles, quads and bezier
        curves with their coordinates, stroke and fill colours, plus each text span and its bounding
        box. Useful for reading dimensions and linework off a drawing sheet to compare against or
        rebuild in the model.

        This does not touch Revit, so it works whether or not Revit is running, and needs no write mode.

        Coordinates are PDF points, 72 per inch. To convert to Revit feet, divide by 864.

        Requires Python 3 with PyMuPDF installed ('pip install pymupdf'). If it is missing, the tool
        says so. Note that PyMuPDF is AGPL-3.0 or requires a commercial licence from Artifex.
        """)]
    public Task<string> ExtractPdfGeometry(
        [Description("Full path to the PDF file.")] string pdfPath,
        CancellationToken ct,
        [Description("Pages to read, 1-based, e.g. '1', '1-3', or '1,4-5'. Omit for every page.")]
        string? pages = null,
        [Description("Cap on geometric primitives returned. Default 20000.")] int maxItems = 20_000,
        [Description("Include text spans with positions. Default true.")] bool includeText = true) =>
        pdf.ExtractAsync(pdfPath, pages, maxItems, includeText, ct);
}
