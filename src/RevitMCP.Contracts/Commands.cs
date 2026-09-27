namespace RevitMCP.Contracts;

/// <summary>
/// Canonical command names on the bridge wire. The MCP server sends these verbatim;
/// the add-in dispatches on them. Keep in sync with <see cref="CommandCatalog"/>.
/// </summary>
public static class Commands
{
    // --- query / read ---
    public const string GetProjectInfo = "GET_PROJECT_INFO";
    public const string ListElements = "LIST_ELEMENTS";
    public const string GetElementProperties = "GET_ELEMENT_PROPERTIES";
    public const string GetElementTypeProperties = "GET_ELEMENT_TYPE_PROPERTIES";
    public const string GetModelWarnings = "GET_MODEL_WARNINGS";
    public const string GetSchedules = "GET_SCHEDULES";
    public const string ListCategories = "LIST_CATEGORIES";
    public const string ListLevels = "LIST_LEVELS";
    public const string ListPhases = "LIST_PHASES";
    public const string ListWorksets = "LIST_WORKSETS";
    public const string ListLinkedModels = "LIST_LINKED_MODELS";
    public const string GetActiveView = "GET_ACTIVE_VIEW";
    public const string ListViews = "LIST_VIEWS";
    public const string ListSheets = "LIST_SHEETS";
    public const string GetSheetContents = "GET_SHEET_CONTENTS";
    public const string ListFamilies = "LIST_FAMILIES";
    public const string ListFamilyTypes = "LIST_FAMILY_TYPES";
    public const string ListMaterials = "LIST_MATERIALS";
    public const string GetMaterialProperties = "GET_MATERIAL_PROPERTIES";
    public const string CountElements = "COUNT_ELEMENTS";
    public const string FindByParam = "FIND_BY_PARAM";
    public const string GetElementLocation = "GET_ELEMENT_LOCATION";
    public const string GetSelection = "GET_SELECTION";
    public const string BridgeStatus = "BRIDGE_STATUS";

    // --- selection / view interaction ---
    public const string SetSelection = "SET_SELECTION";
    public const string OpenView = "OPEN_VIEW";
    public const string IsolateInView = "ISOLATE_IN_VIEW";

    // --- modeling / creation ---
    public const string CreateWall = "CREATE_WALL";
    public const string CreateGrid = "CREATE_GRID";
    public const string CreateStructuralColumn = "CREATE_STRUCTURAL_COLUMN";
    public const string CreateStructuralFraming = "CREATE_STRUCTURAL_FRAMING";
    public const string CreateFloor = "CREATE_FLOOR";
    public const string CreateDraftDetail = "CREATE_DRAFT_DETAIL";

    // --- export / integration ---
    public const string ExportScheduleToCsv = "EXPORT_SCHEDULE_TO_CSV";
    public const string ExtractPdfGeometry = "EXTRACT_PDF_GEOMETRY";
}

/// <summary>What a command needs from Revit, which decides how it is marshalled and gated.</summary>
public enum CommandKind
{
    /// <summary>Answerable without a Document (never blocked, never needs a transaction).</summary>
    BridgeLocal,
    /// <summary>Reads the model. Runs in API context, no transaction.</summary>
    Read,
    /// <summary>Changes UI state (selection, active view, temporary isolate). Requires write opt-in.</summary>
    UiWrite,
    /// <summary>Changes the model inside a transaction. Requires write opt-in.</summary>
    ModelWrite,
    /// <summary>
    /// Handled entirely in the MCP server process and never sent over the pipe. Used for work that
    /// needs nothing from Revit, so it stays out of Revit's process.
    /// </summary>
    ServerLocal
}

public sealed record CommandSpec(string Name, CommandKind Kind, string Description);

/// <summary>
/// Single source of truth for which commands exist and how each is gated.
/// </summary>
public static class CommandCatalog
{
    public static readonly IReadOnlyList<CommandSpec> All = new CommandSpec[]
    {
        new(Commands.GetProjectInfo, CommandKind.Read, "Project metadata."),
        new(Commands.ListElements, CommandKind.Read, "List elements by category."),
        new(Commands.GetElementProperties, CommandKind.Read, "All instance parameters for an element."),
        new(Commands.GetElementTypeProperties, CommandKind.Read, "Type-level parameters for an element's type."),
        new(Commands.GetModelWarnings, CommandKind.Read, "Model warnings."),
        new(Commands.GetSchedules, CommandKind.Read, "Schedules, excluding titleblock revision schedules."),
        new(Commands.ListCategories, CommandKind.Read, "Model categories."),
        new(Commands.ListLevels, CommandKind.Read, "Levels with elevation."),
        new(Commands.ListPhases, CommandKind.Read, "Project phases."),
        new(Commands.ListWorksets, CommandKind.Read, "Worksets (workshared models only)."),
        new(Commands.ListLinkedModels, CommandKind.Read, "Linked Revit models."),
        new(Commands.GetActiveView, CommandKind.Read, "The active view."),
        new(Commands.ListViews, CommandKind.Read, "Views, optionally filtered by view type."),
        new(Commands.ListSheets, CommandKind.Read, "Sheets."),
        new(Commands.GetSheetContents, CommandKind.Read, "Views placed on a sheet."),
        new(Commands.ListFamilies, CommandKind.Read, "Families, optionally filtered by category."),
        new(Commands.ListFamilyTypes, CommandKind.Read, "Family types/symbols."),
        new(Commands.ListMaterials, CommandKind.Read, "Materials."),
        new(Commands.GetMaterialProperties, CommandKind.Read, "Material properties by id or name."),
        new(Commands.CountElements, CommandKind.Read, "Count elements in a category."),
        new(Commands.FindByParam, CommandKind.Read, "Find elements by parameter name/value."),
        new(Commands.GetElementLocation, CommandKind.Read, "Point or curve location of an element."),
        new(Commands.GetSelection, CommandKind.Read, "Currently selected element ids."),
        new(Commands.BridgeStatus, CommandKind.BridgeLocal, "Bridge health/status."),

        new(Commands.SetSelection, CommandKind.UiWrite, "Set the active selection."),
        new(Commands.OpenView, CommandKind.UiWrite, "Activate a view by id or name."),
        new(Commands.IsolateInView, CommandKind.UiWrite, "Temporarily isolate elements in the active view."),

        new(Commands.CreateWall, CommandKind.ModelWrite, "Create a wall."),
        new(Commands.CreateGrid, CommandKind.ModelWrite, "Create a grid line."),
        new(Commands.CreateStructuralColumn, CommandKind.ModelWrite, "Place a structural column."),
        new(Commands.CreateStructuralFraming, CommandKind.ModelWrite, "Create a beam/framing element."),
        new(Commands.CreateFloor, CommandKind.ModelWrite, "Create a floor from a boundary polygon."),
        new(Commands.CreateDraftDetail, CommandKind.ModelWrite, "Create detail elements in a view."),

        new(Commands.ExportScheduleToCsv, CommandKind.Read, "Export a named schedule to CSV."),
        new(Commands.ExtractPdfGeometry, CommandKind.ServerLocal, "Extract geometry from a PDF (runs in the MCP server; no Revit involvement)."),
    };

    private static readonly Dictionary<string, CommandSpec> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string name, out CommandSpec spec) => ByName.TryGetValue(name, out spec!);

    public static bool RequiresWriteConsent(string name) =>
        TryGet(name, out var s) && s.Kind is CommandKind.UiWrite or CommandKind.ModelWrite;

    /// <summary>Commands the Revit add-in is expected to implement.</summary>
    public static IEnumerable<CommandSpec> BridgeCommands =>
        All.Where(c => c.Kind != CommandKind.ServerLocal);

    /// <summary>Commands the MCP server answers by itself, without reaching Revit.</summary>
    public static IEnumerable<CommandSpec> ServerLocalCommands =>
        All.Where(c => c.Kind == CommandKind.ServerLocal);
}
