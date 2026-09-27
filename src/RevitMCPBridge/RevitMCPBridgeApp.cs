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
    internal static ToggleButton? WriteToggle { get; private set; }

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

            BuildRibbon(application);

            BridgeLog.Info($"Listening on \\\\.\\pipe\\{Protocol.PipeName} with " +
                           $"{Dispatcher.KnownCommands.Count} commands registered. Writes are OFF.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            BridgeLog.Error("OnStartup failed; the bridge is not running.", ex);

            // A failed add-in must not block Revit from opening.
            TaskDialog.Show("Revit MCP Bridge",
                $"The MCP bridge could not start:\n\n{ex.Message}\n\nSee {BridgeLog.FilePath}");
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

    private static void BuildRibbon(UIControlledApplication application)
    {
        try { application.CreateRibbonTab(TabName); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

        var panel = application.GetRibbonPanels(TabName)
                        .FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);

        var assemblyPath = typeof(RevitMCPBridgeApp).Assembly.Location;

        var toggle = panel.AddItem(new ToggleButtonData(
            "RevitMcpWriteToggle", "Allow MCP\nWrites",
            assemblyPath, typeof(ToggleWritesCommand).FullName)) as ToggleButton;

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
            try { toggle.ItemText = enabled ? "MCP Writes\nON" : "Allow MCP\nWrites"; }
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
                $"Last error: {server?.LastError ?? "none"}\n\n" +
                $"Log: {BridgeLog.FilePath}"
        };

        dialog.Show();
        return Result.Succeeded;
    }
}
