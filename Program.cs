using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var urls = builder.Configuration.GetSection("Hosting:Urls").Get<string[]>();
if (urls is { Length: > 0 })
{
    builder.WebHost.UseUrls(urls);
}
var serialRequests = builder.Configuration.GetSection("SerialRequests");
builder.Services.Configure<SerialRequestsOptions>(serialRequests);
builder.Services.AddRequestTimeouts();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddSingleton<SerialRequestsGate>(sp =>
{
    var options = serialRequests.Get<SerialRequestsOptions>() ?? new SerialRequestsOptions();
    return new SerialRequestsGate(options.Concurrency);
});

var app = builder.Build();

app.UseMiddleware<SerialRequestsMiddleware>();
app.UseRequestTimeouts();
app.MapReverseProxy();

app.Run();

public sealed class SerialRequestsMiddleware
{
    private readonly RequestDelegate _next;

    public SerialRequestsMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, SerialRequestsGate gate, IOptions<SerialRequestsOptions> options)
    {
        var targetPaths = options.Value.TargetPaths;
        var requestPath = context.Request.Path.Value ?? "";

        if (targetPaths.Any(path => path == requestPath))
        {
            await gate.Semaphore.WaitAsync(context.RequestAborted);
            try { await _next(context); }
            finally { gate.Semaphore.Release(); }
        }
        else { await _next(context); }
    }
}

public sealed class SerialRequestsGate(int concurrency) 
{
    public SemaphoreSlim Semaphore { get; } = new(concurrency, concurrency);
}
public sealed class SerialRequestsOptions
{
    public string[] TargetPaths { get; init; } = [];
    public int Concurrency { get; set; } = 1;
}