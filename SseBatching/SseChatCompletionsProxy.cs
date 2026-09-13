using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LlamaWorkaround.SseBatching;

public sealed class SseChatCompletionsProxy(
    HttpClient httpClient,
    IOptions<SseBatchingOptions> options)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    private readonly int _chunkCount = options.Value.ChunkCount;

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(context);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        CopyResponseHeaders(context.Response, response);

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (!IsEventStream(response.Content.Headers.ContentType))
        {
            await responseStream.CopyToAsync(context.Response.Body, cancellationToken);
            return;
        }

        await BatchEventStreamAsync(responseStream, context.Response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpContext context)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            new Uri(httpClient.BaseAddress!, context.Request.Path + context.Request.QueryString))
        {
            Content = new StreamContent(context.Request.Body)
        };

        foreach (var header in context.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return request;
    }

    private static void CopyResponseHeaders(HttpResponse destination, HttpResponseMessage source)
    {
        destination.StatusCode = (int)source.StatusCode;

        foreach (var header in source.Headers.Concat(source.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key) &&
                !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                destination.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }

    private static bool IsEventStream(MediaTypeHeaderValue? contentType) =>
        string.Equals(contentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private async Task BatchEventStreamAsync(Stream source, HttpResponse destination, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, true, 4096, leaveOpen: false);
        List<string> eventLines = [];
        PendingChunk? pending = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length != 0)
            {
                eventLines.Add(line);
                continue;
            }

            pending = await ProcessEventAsync(eventLines, pending, destination, cancellationToken);
            eventLines.Clear();
        }

        if (eventLines.Count != 0)
        {
            pending = await ProcessEventAsync(eventLines, pending, destination, cancellationToken);
        }

        if (pending is not null)
        {
            await WriteChunkAsync(destination, pending, cancellationToken);
        }
    }

    private async Task<PendingChunk?> ProcessEventAsync(
        List<string> eventLines,
        PendingChunk? pending,
        HttpResponse destination,
        CancellationToken cancellationToken)
    {
        if (!TryParseChunk(eventLines, out var chunk))
        {
            if (pending is not null)
            {
                await WriteChunkAsync(destination, pending, cancellationToken);
            }

            await WriteEventAsync(destination, eventLines, cancellationToken);
            return null;
        }

        var channel = GetChunkChannel(chunk);
        if (pending is null)
        {
            return new PendingChunk(chunk, GetNonDataLines(eventLines), 1, channel);
        }

        if (channel is not null && pending.Channel is not null && channel != pending.Channel)
        {
            await WriteChunkAsync(destination, pending, cancellationToken);
            return new PendingChunk(chunk, GetNonDataLines(eventLines), 1, channel);
        }

        MergeChunk(pending.Chunk, chunk);
        var count = pending.Count + 1;
        if (count < _chunkCount)
        {
            return pending with { Count = count };
        }

        var completed = pending with { Count = count };
        await WriteChunkAsync(destination, completed, cancellationToken);
        return null;
    }

    private static bool TryParseChunk(List<string> eventLines, out JsonObject chunk)
    {
        var data = eventLines
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].TrimStart())
            .ToArray();

        if (data.Length != 1 || data[0] == "[DONE]")
        {
            chunk = null!;
            return false;
        }

        try
        {
            chunk = JsonNode.Parse(data[0]) as JsonObject ?? null!;
            return chunk?["choices"] is JsonArray;
        }
        catch (JsonException)
        {
            chunk = null!;
            return false;
        }
    }

    private static List<string> GetNonDataLines(List<string> eventLines) =>
        eventLines.Where(line => !line.StartsWith("data:", StringComparison.Ordinal)).ToList();

    private static string? GetChunkChannel(JsonObject chunk)
    {
        var channels = chunk["choices"]!.AsArray()
            .OfType<JsonObject>()
            .Select(choice => choice["delta"] as JsonObject)
            .Where(delta => delta is not null)
            .Select(GetDeltaChannel)
            .Where(channel => channel is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return channels.Length switch
        {
            0 => null,
            1 => channels[0],
            _ => "mixed"
        };
    }

    private static string? GetDeltaChannel(JsonObject? delta)
    {
        if (HasText(delta, "reasoning_content") || HasText(delta, "reasoning"))
        {
            return "reasoning";
        }

        if (HasText(delta, "content"))
        {
            return "content";
        }

        if (HasText(delta, "refusal"))
        {
            return "refusal";
        }

        return delta?["tool_calls"] is JsonArray ? "tool_calls" : null;
    }

    private static bool HasText(JsonObject? delta, string propertyName) =>
        delta?[propertyName] is JsonValue value && value.TryGetValue<string>(out _);

    private static void MergeChunk(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Key == "choices")
            {
                MergeChoices(destination["choices"]!.AsArray(), property.Value!.AsArray());
            }
            else
            {
                destination[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static void MergeChoices(JsonArray destination, JsonArray source)
    {
        foreach (var sourceChoice in source.OfType<JsonObject>())
        {
            var index = sourceChoice["index"]?.GetValue<int>() ?? 0;
            var destinationChoice = destination.OfType<JsonObject>()
                .FirstOrDefault(choice => (choice["index"]?.GetValue<int>() ?? 0) == index);

            if (destinationChoice is null)
            {
                destination.Add(sourceChoice.DeepClone());
                continue;
            }

            foreach (var property in sourceChoice)
            {
                if (property.Key == "delta" && property.Value is JsonObject sourceDelta)
                {
                    if (destinationChoice["delta"] is JsonObject destinationDelta)
                    {
                        MergeDelta(destinationDelta, sourceDelta);
                    }
                    else
                    {
                        destinationChoice["delta"] = sourceDelta.DeepClone();
                    }
                }
                else
                {
                    destinationChoice[property.Key] = property.Value?.DeepClone();
                }
            }
        }
    }

    private static void MergeDelta(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Key is "content" or "reasoning_content" or "reasoning" or "refusal" &&
                property.Value is JsonValue sourceValue &&
                sourceValue.TryGetValue<string>(out var sourceText) &&
                destination[property.Key] is JsonValue destinationValue &&
                destinationValue.TryGetValue<string>(out var destinationText))
            {
                destination[property.Key] = destinationText + sourceText;
            }
            else if (property.Key == "tool_calls" && property.Value is JsonArray sourceToolCalls &&
                     destination[property.Key] is JsonArray destinationToolCalls)
            {
                MergeToolCalls(destinationToolCalls, sourceToolCalls);
            }
            else
            {
                destination[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static void MergeToolCalls(JsonArray destination, JsonArray source)
    {
        foreach (var sourceCall in source.OfType<JsonObject>())
        {
            var index = sourceCall["index"]?.GetValue<int>() ?? 0;
            var destinationCall = destination.OfType<JsonObject>()
                .FirstOrDefault(call => (call["index"]?.GetValue<int>() ?? 0) == index);

            if (destinationCall is null)
            {
                destination.Add(sourceCall.DeepClone());
                continue;
            }

            foreach (var property in sourceCall)
            {
                if (property.Key == "function" && property.Value is JsonObject sourceFunction &&
                    destinationCall[property.Key] is JsonObject destinationFunction)
                {
                    MergeFunction(destinationFunction, sourceFunction);
                }
                else
                {
                    destinationCall[property.Key] = property.Value?.DeepClone();
                }
            }
        }
    }

    private static void MergeFunction(JsonObject destination, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Key == "arguments" &&
                property.Value is JsonValue sourceValue &&
                sourceValue.TryGetValue<string>(out var sourceText) &&
                destination[property.Key] is JsonValue destinationValue &&
                destinationValue.TryGetValue<string>(out var destinationText))
            {
                destination[property.Key] = destinationText + sourceText;
            }
            else
            {
                destination[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static async Task WriteChunkAsync(HttpResponse destination, PendingChunk pending, CancellationToken cancellationToken)
    {
        var lines = new List<string>(pending.NonDataLines) { $"data: {pending.Chunk.ToJsonString()}" };
        await WriteEventAsync(destination, lines, cancellationToken);
    }

    private static async Task WriteEventAsync(HttpResponse destination, List<string> lines, CancellationToken cancellationToken)
    {
        await destination.WriteAsync(string.Join('\n', lines) + "\n\n", cancellationToken);
        await destination.Body.FlushAsync(cancellationToken);
    }

    private sealed record PendingChunk(
        JsonObject Chunk,
        List<string> NonDataLines,
        int Count,
        string? Channel);
}
