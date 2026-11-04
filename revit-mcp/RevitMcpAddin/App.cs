using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace RevitMcp
{
    /// <summary>
    /// Revit add-in entry point. Starts a localhost HTTP listener that receives
    /// commands from the MCP server and executes them on Revit's main thread
    /// via an ExternalEvent (the Revit API is not thread-safe).
    /// </summary>
    public class App : IExternalApplication
    {
        public const int DefaultPort = 8765;

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private ExternalEvent _externalEvent;
        private readonly ConcurrentQueue<PendingRequest> _queue = new();

        internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var handler = new RequestHandler(_queue);
                _externalEvent = ExternalEvent.Create(handler);

                int port = DefaultPort;
                var envPort = Environment.GetEnvironmentVariable("REVIT_MCP_PORT");
                if (int.TryParse(envPort, out var p)) port = p;

                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();

                _cts = new CancellationTokenSource();
                Task.Run(() => ListenLoop(_cts.Token));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RevitMcp", "Failed to start MCP bridge:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
                _externalEvent?.Dispose();
            }
            catch { /* shutting down */ }
            return Result.Succeeded;
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            string responseJson;
            int status = 200;
            try
            {
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url.AbsolutePath == "/health")
                {
                    responseJson = "{\"ok\":true}";
                }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath == "/command")
                {
                    // Block web pages from driving Revit: browsers always send an Origin
                    // header on cross-site POSTs, and require a CORS preflight (which we
                    // never approve) before sending application/json. The MCP server sends neither.
                    if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"]) ||
                        !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("Requests from web browsers are not allowed.");
                    }

                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = await reader.ReadToEndAsync();

                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var command = root.GetProperty("command").GetString();
                    var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;

                    var pending = new PendingRequest(command, args);
                    _queue.Enqueue(pending);
                    _externalEvent.Raise();

                    var done = await Task.WhenAny(pending.Completion.Task, Task.Delay(TimeSpan.FromSeconds(55)));
                    if (done != pending.Completion.Task)
                    {
                        pending.Cancelled = true;
                        status = 504;
                        responseJson = Error("Revit is busy (a dialog or command may be open). Try again.");
                    }
                    else
                    {
                        responseJson = pending.Completion.Task.Result;
                    }
                }
                else
                {
                    status = 404;
                    responseJson = Error("Not found");
                }
            }
            catch (Exception ex)
            {
                status = ex is UnauthorizedAccessException ? 403 : 400;
                responseJson = Error(ex.Message);
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(responseJson);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { /* client went away */ }
        }

        internal static string Error(string message) =>
            JsonSerializer.Serialize(new { ok = false, error = message }, Json);
    }

    internal class PendingRequest
    {
        public PendingRequest(string command, JsonElement args)
        {
            Command = command;
            Args = args;
        }

        public string Command { get; }
        public JsonElement Args { get; }
        public volatile bool Cancelled;
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Runs queued requests on Revit's main thread.</summary>
    internal class RequestHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<PendingRequest> _queue;
        public RequestHandler(ConcurrentQueue<PendingRequest> queue) => _queue = queue;

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var req))
            {
                if (req.Cancelled) { req.Completion.TrySetResult(App.Error("Cancelled")); continue; }
                try
                {
                    var result = Commands.Run(app, req.Command, req.Args);
                    req.Completion.TrySetResult(JsonSerializer.Serialize(new { ok = true, result }, App.Json));
                }
                catch (Exception ex)
                {
                    req.Completion.TrySetResult(App.Error(ex.Message));
                }
            }
        }

        public string GetName() => "RevitMcp Request Handler";
    }
}
