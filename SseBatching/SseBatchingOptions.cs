using System.ComponentModel.DataAnnotations;

namespace LlamaWorkaround.SseBatching;

/// <summary>Configures batching for streamed OpenAI chat-completion responses.</summary>
public sealed record SseBatchingOptions
{
    /// <summary>The llama.cpp server URL used for batched chat completions.</summary>
    [Required, Url]
    public required string DestinationAddress { get; init; }

    /// <summary>The number of upstream SSE chunks to combine before sending one downstream chunk.</summary>
    [Range(1, 1000)]
    public int ChunkCount { get; init; } = 4;
}
