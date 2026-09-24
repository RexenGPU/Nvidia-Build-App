using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NvidiaBuildApp;

public record ToolCallChunk(int Index, string? Id, string? Name, string? ArgsDelta);
public record StreamDelta(string? Reasoning, string? Content, IReadOnlyList<ToolCallChunk>? ToolChunks);

public class LlmException : Exception
{
    public int StatusCode { get; }
    public LlmException(string message, int statusCode = 0) : base(message) { StatusCode = statusCode; }
}

/// <summary>
/// Client pour toute API compatible OpenAI (/chat/completions en streaming SSE).
/// Preconfigure pour NVIDIA NIM (integrate.api.nvidia.com) — supporte les tools (function calling).
/// </summary>
public static class LlmClient
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static string? Str(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return n?.ToString();
    }

    public static async IAsyncEnumerable<StreamDelta> StreamAsync(
        string baseUrl,
        string apiKey,
        string model,
        IReadOnlyList<ChatMessage> messages,
        string? systemPrompt,
        float temperature,
        int maxTokens,
        JsonArray? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ---- construction du payload (JsonNode pour les tool_calls / content null)
        var msgs = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgs.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role };
            if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
            {
                o["tool_call_id"] = m.ToolCallId;
                o["content"] = m.Content;
            }
            else if (m.ToolCalls is { Count: > 0 })
            {
                if (!string.IsNullOrEmpty(m.Content)) o["content"] = m.Content;
                var tcs = new JsonArray();
                foreach (var tc in m.ToolCalls)
                    tcs.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = tc.Name, ["arguments"] = tc.Arguments }
                    });
                o["tool_calls"] = tcs;
            }
            else
            {
                o["content"] = m.Content;
            }
            msgs.Add(o);
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = msgs,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
        };
        if (tools is { Count: > 0 }) payload["tools"] = tools.DeepClone();

        var json = payload.ToJsonString();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromMinutes(10));

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!resp.IsSuccessStatusCode)
        {
            string body = "";
            try { body = await resp.Content.ReadAsStringAsync(linked.Token); } catch { }
            throw new LlmException(ExtractError(body, (int)resp.StatusCode), (int)resp.StatusCode);
        }

        using var stream = await resp.Content.ReadAsStreamAsync(linked.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (true)
        {
            string? line = await reader.ReadLineAsync(linked.Token);
            if (line is null) break;
            linked.Token.ThrowIfCancellationRequested();

            if (line.Length == 0 || line[0] == ':') continue;
            if (!line.StartsWith("data", StringComparison.OrdinalIgnoreCase)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;

            string data = line[(colon + 1)..].Trim();
            if (data.Length == 0 || data == "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(data); } catch { continue; }

            var delta = (node?["choices"] as JsonArray)?[0]?["delta"];
            if (delta is null) continue;

            string? reasoning = Str(delta["reasoning_content"]);   // modeles "thinking"
            string? content = Str(delta["content"]);

            List<ToolCallChunk>? chunks = null;
            if (delta["tool_calls"] is JsonArray tca)
            {
                chunks = new List<ToolCallChunk>();
                foreach (var tce in tca)
                {
                    int idx = 0;
                    if (tce?["index"] is JsonValue iv && iv.TryGetValue<int>(out var i2)) idx = i2;
                    string? tid = Str(tce?["id"]);
                    string? fname = Str(tce?["function"]?["name"]);
                    string? fargs = Str(tce?["function"]?["arguments"]);
                    if (tid != null || fname != null || fargs != null)
                        chunks.Add(new ToolCallChunk(idx, tid, fname, fargs));
                }
                if (chunks.Count == 0) chunks = null;
            }

            if (reasoning is not null || content is not null || chunks is not null)
                yield return new StreamDelta(reasoning, content, chunks);
        }
    }

    /// <summary>GET {base}/models — liste reelle des modeles exposes par l'API.</summary>
    public static async Task<List<string>> FetchModelsAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body = "";
            try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }
            throw new LlmException(ExtractError(body, (int)resp.StatusCode), (int)resp.StatusCode);
        }
        var list = new List<string>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
                }
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    static string ExtractError(string body, int code)
    {
        string detail = "";
        try
        {
            var n = JsonNode.Parse(body);
            detail = Str(n?["error"]?["message"]) ?? Str(n?["detail"]) ?? Str(n?["message"]) ?? "";
        }
        catch { }
        if (string.IsNullOrWhiteSpace(detail))
            detail = string.IsNullOrWhiteSpace(body) ? "" : (body.Length > 400 ? body[..400] + "..." : body);
        if (string.IsNullOrWhiteSpace(detail))
            detail = "Erreur HTTP " + code;
        return detail;
    }
}
