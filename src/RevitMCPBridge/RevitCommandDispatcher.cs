using System.Collections.Concurrent;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Contracts;

namespace RevitMCPBridge;

/// <summary>
/// The only place Revit API calls happen.
///
/// The pipe server runs on a worker thread, where touching a <see cref="Document"/> is illegal and
/// crashes Revit rather than throwing. So work is queued and executed from
/// <see cref="Execute"/>, which Revit invokes on its main thread in a valid API context. The pipe
/// thread submits and then awaits the result.
///
/// Note that <see cref="ExternalEvent.Raise"/> coalesces: several raises can produce a single
/// <see cref="Execute"/> callback, which is why this drains the whole queue each time rather than
/// handling one item.
/// </summary>
public sealed class RevitCommandDispatcher : IExternalEventHandler
{
    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly Dictionary<string, IBridgeCommandHandler> _handlers;
    private ExternalEvent? _externalEvent;

    public RevitCommandDispatcher(IEnumerable<IBridgeCommandHandler> handlers)
    {
        _handlers = handlers.ToDictionary(h => h.Command, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Must be called from a valid API context (OnStartup), not from the pipe thread.</summary>
    public void Initialize() => _externalEvent = ExternalEvent.Create(this);

    public string GetName() => "Revit MCP Bridge dispatcher";

    public IReadOnlyCollection<string> KnownCommands => _handlers.Keys;

    private sealed class WorkItem(BridgeRequest request)
    {
        public BridgeRequest Request { get; } = request;
        public TaskCompletionSource<BridgeResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Called from the pipe thread. Queues the request, wakes Revit, and waits for the answer.
    /// </summary>
    public async Task<BridgeResponse> SubmitAsync(BridgeRequest request, CancellationToken ct)
    {
        if (_externalEvent is null)
            return BridgeResponse.Fail(request, BridgeErrorCode.Internal,
                "The bridge dispatcher was not initialised.");

        // Commands that need nothing from Revit skip the queue entirely, so BRIDGE_STATUS still
        // answers while Revit is busy in a modal dialog — exactly when you want to ask.
        if (CommandCatalog.TryGet(request.Command, out var spec)
            && spec.Kind == CommandKind.BridgeLocal
            && _handlers.TryGetValue(request.Command, out var localHandler))
        {
            return RunOffThread(localHandler, request);
        }

        var item = new WorkItem(request);
        _queue.Enqueue(item);
        _externalEvent.Raise();

        var timeout = request.TimeoutMs > 0 ? request.TimeoutMs : 30_000;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            return await item.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The item may still be queued; mark it abandoned so Execute skips it.
            item.Completion.TrySetCanceled();
            return BridgeResponse.Fail(request, BridgeErrorCode.Timeout,
                $"Revit did not reach an API context within {timeout} ms. It is usually busy with a " +
                "command or showing a modal dialog — dismiss it and retry.");
        }
    }

    /// <summary>Handles a Revit-free command on the calling (pipe) thread.</summary>
    private static BridgeResponse RunOffThread(IBridgeCommandHandler handler, BridgeRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var data = handler.Execute(new CommandContext(null!, request));
            var response = BridgeResponse.Success(request, data);
            response.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return response;
        }
        catch (BridgeException ex)
        {
            return Failed(request, ex.Code, ex.Message, ex.Detail, stopwatch);
        }
        catch (Exception ex)
        {
            return Failed(request, BridgeErrorCode.Internal, ex.Message, ex.ToString(), stopwatch);
        }
    }

    /// <summary>Revit's main thread. Everything here is in a valid API context.</summary>
    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var item))
        {
            // The caller already gave up (timeout or disconnect); don't touch the model for it.
            if (item.Completion.Task.IsCompleted) continue;

            var response = RunOne(app, item.Request);
            item.Completion.TrySetResult(response);
        }
    }

    private BridgeResponse RunOne(UIApplication app, BridgeRequest request)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!CommandCatalog.TryGet(request.Command, out var spec)
            || !_handlers.TryGetValue(request.Command, out var handler))
        {
            return Failed(request, BridgeErrorCode.UnknownCommand,
                $"'{request.Command}' is not a bridge command.", null, stopwatch);
        }

        if (CommandCatalog.RequiresWriteConsent(request.Command) && !WriteConsent.Enabled)
        {
            return Failed(request, BridgeErrorCode.WriteNotPermitted,
                $"'{request.Command}' changes the model or the UI, and write mode is off. " +
                "Turn on 'Allow MCP Writes' on the Revit ribbon (MCP Bridge panel) to permit it.",
                null, stopwatch);
        }

        try
        {
            var context = new CommandContext(app, request);

            var data = spec.Kind == CommandKind.ModelWrite
                ? RunInTransaction(context, handler, request.Command)
                : handler.Execute(context);

            var response = BridgeResponse.Success(request, data);
            response.ElapsedMs = stopwatch.ElapsedMilliseconds;
            return response;
        }
        catch (BridgeException ex)
        {
            return Failed(request, ex.Code, ex.Message, ex.Detail, stopwatch);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            // Revit's own exception hierarchy: argument problems, invalid operations, etc.
            return Failed(request, BridgeErrorCode.RevitException, ex.Message, ex.ToString(), stopwatch);
        }
        catch (Exception ex)
        {
            return Failed(request, BridgeErrorCode.Internal, ex.Message, ex.ToString(), stopwatch);
        }
    }

    /// <summary>
    /// Wraps a model write in one transaction named after the command, so the user sees a single
    /// meaningful entry in Revit's undo stack and can reverse it with Ctrl+Z.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode? RunInTransaction(
        CommandContext context, IBridgeCommandHandler handler, string command)
    {
        var doc = context.Doc;

        if (doc.IsReadOnly)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "The active document is read-only, so it cannot be modified.");

        using var transaction = new Transaction(doc, $"MCP: {command}");
        if (transaction.Start() != TransactionStatus.Started)
            throw new BridgeException(BridgeErrorCode.InvalidState,
                "Revit refused to start a transaction. Another command may still be running.");

        try
        {
            var data = handler.Execute(context);

            if (transaction.Commit() != TransactionStatus.Committed)
                throw new BridgeException(BridgeErrorCode.InvalidState,
                    "The transaction did not commit; the model is unchanged.");

            return data;
        }
        catch
        {
            if (!transaction.HasEnded()) transaction.RollBack();
            throw;
        }
    }

    private static BridgeResponse Failed(BridgeRequest request, BridgeErrorCode code,
        string message, string? detail, Stopwatch stopwatch)
    {
        var response = BridgeResponse.Fail(request, code, message, detail);
        response.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return response;
    }
}
