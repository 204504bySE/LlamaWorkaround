using LlamaWorkaround.SseBatching;
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

builder.Services.AddOptions<SseBatchingOptions>()
    .BindConfiguration("SseBatching")
    .ValidateDataAnnotations()
    .Validate(
        static options => Uri.TryCreate(options.DestinationAddress, UriKind.Absolute, out _),
        "SseBatching:DestinationAddress must be an absolute URL.")
    .ValidateOnStart();
builder.Services.AddHttpClient<SseChatCompletionsProxy>((services, client) =>
{
    var options = services.GetRequiredService<IOptions<SseBatchingOptions>>().Value;
    client.BaseAddress = new Uri(options.DestinationAddress, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddRequestTimeouts();
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseRequestTimeouts();
app.MapPost("/v1/chat/completions", static async (
    HttpContext context,
    SseChatCompletionsProxy proxy,
    CancellationToken cancellationToken) =>
{
    await proxy.ForwardAsync(context, cancellationToken);
});
app.MapReverseProxy();

app.Run();
