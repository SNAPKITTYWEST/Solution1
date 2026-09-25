using System.Text.Json.Nodes;
namespace Sovereign.Bedrock;
/// <summary>Compatibility facade for an explicitly configured local model server.</summary>
public sealed class BedrockBackend(HttpClient client, Uri endpoint, string modelId)
{
    public string ModelId { get; } = modelId;
    private readonly BedrockProvider _provider = new(client, endpoint);
    public async Task<string> GenerateAsync(IReadOnlyList<(string Role, string Content)> messages,
        double temperature = 0, int? maxTokens = null, CancellationToken cancellationToken = default)
    {
        var json = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());
        var response = await _provider.InvokeModelAsync(ModelId, json, maxTokens ?? 1024, temperature, cancellationToken);
        return response["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidDataException("Local model response has no text content.");
    }
}
