using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Sovereign.Bedrock;

/// <summary>
/// AWS Bedrock unified provider adapter — part of SOVEREIGN PYTHON LLM ENGINE.
/// Supports all Bedrock models with automatic format conversion.
/// Port of sovereign-engine-v2's src/runtime/providers/bedrock.py.
/// </summary>
public class BedrockProvider
{
    private readonly AmazonBedrockRuntimeClient _bedrock;

    public BedrockProvider(string region = "us-east-1")
    {
        _bedrock = new AmazonBedrockRuntimeClient(Amazon.RegionEndpoint.GetBySystemName(region));
    }

    public async Task<JsonNode> InvokeModelAsync(
        string modelId,
        JsonArray messages,
        int maxTokens = 4096,
        double temperature = 1.0,
        JsonObject? extra = null)
    {
        var requestBody = FormatRequest(modelId, messages, maxTokens, temperature, extra);

        var response = await _bedrock.InvokeModelAsync(new InvokeModelRequest
        {
            ModelId = modelId,
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody.ToJsonString())),
        });

        using var reader = new StreamReader(response.Body);
        var responseBody = JsonNode.Parse(await reader.ReadToEndAsync())!;

        return FormatResponse(modelId, responseBody);
    }

    public async IAsyncEnumerable<JsonNode> InvokeModelStreamAsync(
        string modelId,
        JsonArray messages,
        int maxTokens = 4096,
        double temperature = 1.0,
        JsonObject? extra = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = FormatRequest(modelId, messages, maxTokens, temperature, extra);

        var response = await _bedrock.InvokeModelWithResponseStreamAsync(new InvokeModelWithResponseStreamRequest
        {
            ModelId = modelId,
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody.ToJsonString())),
        }, cancellationToken);

        await foreach (var payload in response.Body.ReadAllAsync(cancellationToken))
        {
            if (payload is not PayloadPart part) continue;
            using var reader = new StreamReader(part.Bytes);
            var chunk = JsonNode.Parse(await reader.ReadToEndAsync())!;
            yield return FormatResponse(modelId, chunk);
        }
    }

    /// <summary>Convert to model-specific format.</summary>
    private static JsonObject FormatRequest(
        string modelId,
        JsonArray messages,
        int maxTokens,
        double temperature,
        JsonObject? extra)
    {
        JsonObject request;

        if (modelId.Contains("anthropic.claude"))
        {
            // Anthropic format
            request = new JsonObject
            {
                ["anthropic_version"] = "bedrock-2023-05-31",
                ["messages"] = messages.DeepClone(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
            };
        }
        else if (modelId.Contains("meta.llama"))
        {
            // Llama format
            request = new JsonObject
            {
                ["prompt"] = MessagesToPrompt(messages),
                ["max_gen_len"] = maxTokens,
                ["temperature"] = temperature,
            };
        }
        else if (modelId.Contains("mistral"))
        {
            // Mistral format
            request = new JsonObject
            {
                ["prompt"] = MessagesToPrompt(messages),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
            };
        }
        else
        {
            // Default format
            request = new JsonObject
            {
                ["messages"] = messages.DeepClone(),
                ["max_tokens"] = maxTokens,
                ["temperature"] = temperature,
            };
        }

        if (extra is not null)
        {
            foreach (var kvp in extra)
            {
                request[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        return request;
    }

    /// <summary>Convert response to standard format.</summary>
    private static JsonNode FormatResponse(string modelId, JsonNode response)
    {
        if (modelId.Contains("anthropic.claude"))
        {
            // Already in standard format
            return response;
        }

        if (modelId.Contains("meta.llama"))
        {
            // Convert Llama format
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = response["generation"]?.GetValue<string>() ?? "",
                    },
                },
                ["stop_reason"] = response["stop_reason"]?.DeepClone(),
                ["usage"] = response["usage"]?.DeepClone() ?? new JsonObject(),
            };
        }

        if (modelId.Contains("mistral"))
        {
            // Convert Mistral format
            var firstOutput = response["outputs"]?.AsArray().FirstOrDefault();
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = firstOutput?["text"]?.GetValue<string>() ?? "",
                    },
                },
                ["stop_reason"] = response["stop_reason"]?.DeepClone(),
                ["usage"] = new JsonObject(),
            };
        }

        // Return as-is
        return response;
    }

    /// <summary>Convert messages to a prompt string (for non-Anthropic models).</summary>
    private static string MessagesToPrompt(JsonArray messages)
    {
        var parts = new List<string>();
        foreach (var msg in messages)
        {
            var role = msg!["role"]!.GetValue<string>();
            var contentNode = msg["content"];

            string content;
            if (contentNode is JsonArray contentArray)
            {
                content = string.Join(" ", contentArray
                    .Where(item => item?["type"]?.GetValue<string>() == "text")
                    .Select(item => item?["text"]?.GetValue<string>() ?? ""));
            }
            else
            {
                content = contentNode?.GetValue<string>() ?? "";
            }

            parts.Add($"{role}: {content}");
        }

        return string.Join("\n\n", parts);
    }
}
