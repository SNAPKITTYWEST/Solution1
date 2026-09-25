using System.Text;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Sovereign.Bedrock;

/// <summary>
/// AWS Bedrock inference backend — temporary wire — Ahmad's sovereign SDK replaces this.
/// Routes through AWS Bedrock using the default AWS credential chain.
/// Port of sovereign-engine-v2's src/inference/bedrock_backend.py.
/// </summary>
public class BedrockBackend
{
    public string ModelId { get; }

    private readonly AmazonBedrockRuntimeClient _client;

    public BedrockBackend(string? modelId = null, string region = "us-east-1")
    {
        ModelId = modelId ?? "us.anthropic.claude-haiku-4-5-20251001-v1:0";
        _client = new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.GetBySystemName(region));
    }

    public async Task<string> GenerateAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        double temperature = 0.0,
        int? maxTokens = null)
    {
        // Strip to role+content only — Anthropic rejects extra fields.
        // System prompt goes to top-level, not in the messages array.
        var systemParts = messages
            .Where(m => m.Role == "system")
            .Select(m => m.Content)
            .ToList();

        var cleanMessages = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            cleanMessages.Add(new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content,
            });
        }

        var body = new JsonObject
        {
            ["anthropic_version"] = "bedrock-2023-05-31",
            ["max_tokens"] = maxTokens ?? 1024,
            ["temperature"] = temperature,
            ["messages"] = cleanMessages,
        };
        if (systemParts.Count > 0)
        {
            body["system"] = string.Join("\n", systemParts);
        }

        var response = await _client.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = ModelId,
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString())),
            ContentType = "application/json",
            Accept = "application/json",
        });

        using var reader = new StreamReader(response.Body);
        var result = JsonNode.Parse(await reader.ReadToEndAsync());
        return result!["content"]![0]!["text"]!.GetValue<string>();
    }
}
