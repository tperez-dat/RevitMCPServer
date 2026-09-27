using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// Add-in entry point: builds the ribbon, starts the pipe listener, and owns the dispatcher.
/// </summary>
public sealed class RevitMCPBridgeApp : IExternalApplication
{
    private const string TabName = "MCP Bridge";
    private const string PanelName = "Revit MCP";

    internal static RevitCommandDispatcher? Dispatcher { get; private set; }
    internal static PipeServer? Server { get; private set; }
    internal static DateTimeOffset StartedUtc { get; private set; }
    internal static string RevitVersion { get; private set; } = "unknown";
    internal static PushButton? WriteToggle { get; private set; }

    /// <summary>False when the ribbon failed to build, so the status dialog can say so.</summary>
    internal static bool RibbonAvailable { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            RevitVersion = application.ControlledApplication.VersionNumber;
            StartedUtc = DateTimeOffset.UtcNow;

            BridgeLog.Info($"Starting Revit MCP Bridge on Revit {RevitVersion} " +
                           $"(pid {Environment.ProcessId}, protocol v{Protocol.Version}).");

            // ExternalEvent.Create is only legal from a valid API context, which OnStartup is.
            Dispatcher = new RevitCommandDispatcher(HandlerRegistry.CreateAll());
            Dispatcher.Initialize();

            var token = SessionHandshake.NewToken();
            SessionHandshake.Publish(new BridgeSessionFile
            {
                Token = token,
                ProcessId = Environment.ProcessId,
                RevitVersion = RevitVersion,
                StartedUtc = StartedUtc
            });

            Server = new PipeServer(Dispatcher, token);
            Server.Start();

            // The ribbon is a convenience, not the bridge. If it cannot be built, reads must still
            // work rather than the whole add-in being lost over a button.
            RibbonAvailable = TryBuildRibbon(application);

            BridgeLog.Info($"Listening on \\\\.\\pipe\\{Protocol.PipeName} with " +
                           $"{Dispatcher.KnownCommands.Count} commands registered. Writes are OFF.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            // Server is non-null only once Start() has been reached, so this reports what is true.
            var listening = Server?.IsRunning == true;

            BridgeLog.Error(listening
                ? "OnStartup did not complete, but the pipe server is listening."
                : "OnStartup failed; the bridge is not running.", ex);

            // A failed add-in must not block Revit from opening.
            TaskDialog.Show("Revit MCP Bridge",
                $"The MCP bridge {(listening ? "started but did not finish setting up" : "could not start")}:" +
                $"\n\n{ex.Message}\n\nSee {BridgeLog.FilePath}");
            return Result.Succeeded;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            BridgeLog.Info("Shutting down the bridge.");
            if (Server is not null) Server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SessionHandshake.Remove();
        }
        catch (Exception ex)
        {
            BridgeLog.Warn("Shutdown was not clean.", ex);
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// Builds the ribbon, reporting rather than throwing on failure. Revit's ribbon API rejects
    /// items for reasons that vary between releases, and losing read-only access to the model over
    /// a button would be a poor trade.
    /// </summary>
    private static bool TryBuildRibbon(UIControlledApplication application)
    {
        try
        {
            BuildRibbon(application);
            return true;
        }
        catch (Exception ex)
        {
            BridgeLog.Error(
                "The ribbon could not be built, so the MCP Bridge tab is missing. The bridge is " +
                "still listening and read-only tools work. To enable writes without the ribbon, " +
                "use Add-Ins > External Tools > Toggle MCP Writes.", ex);
            return false;
        }
    }

    /// <summary>
    /// Builds the ribbon. A ToggleButton is deliberately not used: RibbonPanel.AddItem rejects
    /// ToggleButtonData as a top-level item (it is only valid inside a RadioButtonGroup) and throws
    /// "This type of data is not supported as a large size item". A PushButton whose caption tracks
    /// the flag gives the same affordance and is a supported item.
    /// </summary>
    private static void BuildRibbon(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

        var panel = application.GetRibbonPanels(TabName)
                        .FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);

        var assemblyPath = typeof(RevitMCPBridgeApp).Assembly.Location;

        var toggle = panel.AddItem(new PushButtonData(
            "RevitMcpWriteToggle", WriteButtonCaption(false),
            assemblyPath, typeof(ToggleWritesCommand).FullName)) as PushButton;

        if (toggle is not null)
        {
            toggle.ToolTip = "Allow the MCP bridge to change this model.";
            toggle.LongDescription =
                "Off (default): the bridge answers read-only queries only.\n\n" +
                "On: the bridge may also change the selection, the active view, and the model. " +
                "Every change runs in its own named transaction, so Ctrl+Z reverses it.\n\n" +
                "This resets to off each time Revit starts.";
            WriteToggle = toggle;
        }

        panel.AddSeparator();

        var status = panel.AddItem(new PushButtonData(
            "RevitMcpStatus", "Bridge\nStatus",
            assemblyPath, typeof(ShowStatusCommand).FullName)) as PushButton;

        if (status is not null)
            status.ToolTip = "Show the bridge's pipe name, request count, and log location.";
    }

    /// <summary>The write button's caption, which is how the user sees the current state.</summary>
    internal static string WriteButtonCaption(bool enabled) =>
        enabled ? "MCP Writes\nON" : "Allow MCP\nWrites";
}

/// <summary>Ribbon toggle for <see cref="WriteConsent"/>. The only way to enable writes.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ToggleWritesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var enabled = WriteConsent.Toggle();
        BridgeLog.Info($"Write mode turned {(enabled ? "ON" : "OFF")} by the user.");

        // Keep the ribbon check state in step with the flag, including when Revit toggles it for us.
        if (RevitMCPBridgeApp.WriteToggle is { } toggle)
        {
            try { toggle.ItemText = RevitMCPBridgeApp.WriteButtonCaption(enabled); }
            catch (Exception ex) { BridgeLog.Warn("Could not update the toggle caption.", ex); }
        }

        return Result.Succeeded;
    }
}

/// <summary>Shows bridge health without going through the pipe.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class ShowStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var server = RevitMCPBridgeApp.Server;
        var uptime = DateTimeOffset.UtcNow - RevitMCPBridgeApp.StartedUtc;

        var dialog = new TaskDialog("Revit MCP Bridge")
        {
            MainInstruction = server?.IsRunning == true ? "Bridge is listening." : "Bridge is NOT listening.",
            MainContent =
                $"Pipe: \\\\.\\pipe\\{Protocol.PipeName}\n" +
                $"Protocol: v{Protocol.Version}\n" +
                $"Revit: {RevitMCPBridgeApp.RevitVersion}  (pid {Environment.ProcessId})\n" +
                $"Uptime: {uptime:hh\\:mm\\:ss}\n" +
                $"Connections: {server?.ConnectionsAccepted ?? 0}\n" +
                $"Requests: {server?.RequestsHandled ?? 0}\n" +
                $"Writes: {(WriteConsent.Enabled ? "ALLOWED" : "blocked (read-only)")}\n" +
                $"Ribbon: {(RevitMCPBridgeApp.RibbonAvailable ? "built" : "FAILED - use Add-Ins > External Tools")}\n" +
                $"Last error: {server?.LastError ?? "none"}\n\n" +
                $"Log: {BridgeLog.FilePath}"
        };

        dialog.Show();
        return Result.Succeeded;
    }
}
