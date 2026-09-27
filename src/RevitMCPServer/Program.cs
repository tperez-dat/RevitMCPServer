using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitMCPServer;

var builder = Host.CreateApplicationBuilder(args);

// stdio is the MCP transport, so stdout carries protocol frames only: every log line must go to
// stderr or it corrupts the session.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<BridgeClient>();
builder.Services.AddSingleton<ToolGateway>();
builder.Services.AddSingleton<PdfGeometryExtractor>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
        {
            Name = "revit-mcp",
            Version = "0.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
