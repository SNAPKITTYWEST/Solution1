using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
namespace Sovereign.Bedrock;
/// <summary>OpenAI-compatible loopback HTTP transport; no AWS SDK or credentials.</summary>
public sealed class BedrockProvider
{
    private readonly HttpClient _client; private readonly Uri _endpoint;
    public BedrockProvider(HttpClient client, Uri endpoint)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)) throw new ArgumentException("Model endpoint must be an HTTP(S) loopback URL.");
        _client = client; _endpoint = new Uri(endpoint, "v1/chat/completions");
    }
    private static JsonObject Request(string modelId, JsonArray messages, int maxTokens, double temperature, bool stream)
    {
        if (string.IsNullOrWhiteSpace(modelId) || maxTokens is < 1 or > 8192 || !double.IsFinite(temperature) || temperature is < 0 or > 2) throw new ArgumentException("Invalid model parameters.");
        return new JsonObject { ["model"] = modelId, ["messages"] = messages.DeepClone(), ["max_tokens"] = maxTokens, ["temperature"] = temperature, ["stream"] = stream };
    }
    public async Task<JsonNode> InvokeModelAsync(string modelId, JsonArray messages, int maxTokens = 1024, double temperature = 0, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(_endpoint, Request(modelId, messages, maxTokens, temperature, false), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("Empty model response.");
    }
    public async IAsyncEnumerable<JsonNode> InvokeModelStreamAsync(string modelId, JsonArray messages, int maxTokens = 1024, double temperature = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(Request(modelId, messages, maxTokens, temperature, true)) };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (line[6..] == "[DONE]") yield break;
            yield return JsonNode.Parse(line[6..]) ?? throw new InvalidDataException("Invalid SSE JSON.");
        }
    }
}
