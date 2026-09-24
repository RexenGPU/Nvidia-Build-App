using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NvidiaBuildApp;

public class McpTool
{
    public string ServerId = "";
    public string RealName = "";
    public string ApiName = "";      // nom expose a l'API (prefixe si collision)
    public string Description = "";
    public JsonNode? Schema;
}

/// <summary>
/// Client MCP minimal (Model Context Protocol) : transport stdio (ligne de commande locale)
/// et Streamable HTTP (URL /mcp). Gere initialize, tools/list et tools/call en JSON-RPC 2.0.
/// </summary>
class McpConnection : IDisposable
{
    readonly McpServerConfig _cfg;
    Process? _proc;
    HttpClient? _http;
    string? _session;
    int _nextId;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();

    public List<McpTool> Tools { get; } = new();
    public string? Error { get; private set; }
    public string Status { get; private set; } = "Connexion…";
    public int ToolCount => Tools.Count;

    public McpConnection(McpServerConfig cfg) => _cfg = cfg;

    public bool Matches(McpServerConfig c) =>
        c.Transport == _cfg.Transport && c.Command == _cfg.Command && c.Url == _cfg.Url && c.Name == _cfg.Name;

    // ------------------------------------------------------------------ cycle de vie

    public async Task InitAsync()
    {
        try
        {
            Tools.Clear();
            if (_cfg.Transport == "http") _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            else StartStdio();

            var init = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "NvidiaBuildApp", ["version"] = "1.4" }
            }, 20000);

            Notify("notifications/initialized");

            var res = await RequestAsync("tools/list", null, 20000);
            if (res["tools"] is JsonArray ta)
            {
                foreach (var t in ta)
                {
                    var name = t?["name"]?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    Tools.Add(new McpTool
                    {
                        ServerId = _cfg.Id,
                        RealName = name,
                        Description = t?["description"]?.ToString() ?? "",
                        Schema = t?["inputSchema"]?.DeepClone()
                    });
                }
            }
            Error = null;
            Status = $"Connecté · {Tools.Count} outil(s)";
        }
        catch (Exception ex)
        {
            Tools.Clear();
            Error = ex.Message;
            Status = "Erreur de connexion";
            TryKill();
        }
    }

    void StartStdio()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + _cfg.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        _proc = Process.Start(psi) ?? throw new Exception("Impossible de lancer : " + _cfg.Command);
        _proc.OutputDataReceived += (s, e) => { if (e.Data != null) OnLine(e.Data); };
        _proc.ErrorDataReceived += (s, e) => { };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    void OnLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return;
        JsonNode? n;
        try { n = JsonNode.Parse(line); } catch { return; }
        if (n?["id"] is not JsonValue idv) return;      // notifications ignorees
        int id;
        try { id = (int)idv; } catch { return; }
        if (_pending.TryRemove(id, out var tcs))
            tcs.TrySetResult(n);
    }

    // ------------------------------------------------------------------ JSON-RPC

    async Task<JsonNode> RequestAsync(string method, JsonNode? prms, int timeoutMs)
    {
        int id = Interlocked.Increment(ref _nextId) - 1;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (prms != null) msg["params"] = prms.DeepClone();

        if (_cfg.Transport == "stdio")
        {
            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await _proc!.StandardInput.WriteLineAsync(msg.ToJsonString());
            await _proc.StandardInput.FlushAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var t2))
                    t2.TrySetException(new TimeoutException($"{method} : pas de réponse du serveur MCP"));
            });
            var node = await tcs.Task;
            return Handle(node);
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
            {
                Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (_session != null) req.Headers.Add("Mcp-Session-Id", _session);
            using var resp = await _http!.SendAsync(req);
            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sess))
                _session = sess.FirstOrDefault();

            var body = await resp.Content.ReadAsStringAsync();
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";

            JsonNode? node = null;
            if (ct.Contains("event-stream"))
            {
                foreach (var raw in body.Split('\n'))
                {
                    var t = raw.Trim();
                    if (!t.StartsWith("data:")) continue;
                    try
                    {
                        var dn = JsonNode.Parse(t[5..].Trim());
                        if (dn?["id"] is JsonValue dv && (int)dv == id) { node = dn; break; }
                    }
                    catch { }
                }
            }
            else
            {
                try { node = JsonNode.Parse(body); } catch { }
            }
            if (node == null) throw new Exception("Réponse MCP illisible (HTTP " + (int)resp.StatusCode + ")");
            return Handle(node);
        }
    }

    static JsonNode Handle(JsonNode n)
    {
        if (n["error"] is JsonObject err)
            throw new Exception(err["message"]?.ToString() ?? "Erreur MCP");
        return n["result"] ?? n;
    }

    void Notify(string method)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (_cfg.Transport == "stdio")
        {
            try
            {
                _proc?.StandardInput.WriteLine(msg.ToJsonString());
                _proc?.StandardInput.Flush();
            }
            catch { }
        }
        else if (_http != null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
            {
                Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (_session != null) req.Headers.Add("Mcp-Session-Id", _session);
            _ = _http.SendAsync(req);
        }
    }

    // ------------------------------------------------------------------ outils

    public async Task<string> CallToolAsync(string name, string argsJson)
    {
        JsonNode? args;
        try { args = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson); }
        catch { args = new JsonObject(); }
        if (args is not JsonObject) args = new JsonObject();

        var result = await RequestAsync("tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, 120000);

        var sb = new StringBuilder();
        if (result["content"] is JsonArray arr)
            foreach (var c in arr)
            {
                var txt = c?["text"]?.ToString();
                if (txt != null) sb.AppendLine(txt);
            }
        if (sb.Length == 0) sb.AppendLine(result.ToJsonString());
        if (result["isError"]?.GetValue<bool>() == true)
            throw new Exception("L'outil a signalé une erreur : " + sb.ToString().TrimEnd());
        return sb.ToString().TrimEnd();
    }

    void TryKill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        TryKill();
        try { _proc?.Dispose(); } catch { }
        _http?.Dispose();
    }
}

/// <summary>Gestionnaire global des serveurs MCP + definitions d'outils pour l'API.</summary>
static class Mcp
{
    static readonly Dictionary<string, McpConnection> Conns = new();

    public static bool IsConnected(string id) => Conns.TryGetValue(id, out var c) && c.Error == null;
    public static int ToolCountOf(string id) => Conns.TryGetValue(id, out var c) && c.Error == null ? c.ToolCount : 0;
    public static string? ErrorOf(string id) => Conns.TryGetValue(id, out var c) ? c.Error : null;
    public static string StatusOf(string id) => Conns.TryGetValue(id, out var c) ? c.Status : "Non connecté";
    public static int TotalTools => Conns.Values.Sum(c => c.Error == null ? c.ToolCount : 0);

    /// <summary>Synchronise les connexions avec la configuration (connecte/retire au besoin).</summary>
    public static async Task RefreshAsync(IEnumerable<McpServerConfig> servers)
    {
        var wanted = servers.Where(s => s.Enabled).ToList();

        foreach (var id in Conns.Keys.Except(wanted.Select(w => w.Id)).ToList())
        {
            Conns[id].Dispose();
            Conns.Remove(id);
        }

        foreach (var w in wanted)
        {
            if (Conns.TryGetValue(w.Id, out var existing) && existing.Matches(w)) continue;
            if (Conns.TryGetValue(w.Id, out var old)) { old.Dispose(); Conns.Remove(w.Id); }
            var conn = new McpConnection(w);
            Conns[w.Id] = conn;
            await conn.InitAsync();
        }
    }

    public static void Drop(string id)
    {
        if (Conns.Remove(id, out var c)) c.Dispose();
    }

    /// <summary>Definitions d'outils au format OpenAI (tools) — null si aucun outil.</summary>
    public static JsonArray? ToolDefinitions()
    {
        var arr = new JsonArray();
        var used = new HashSet<string>();
        foreach (var (id, conn) in Conns)
        {
            if (conn.Error != null) continue;
            foreach (var t in conn.Tools)
            {
                var apiName = t.RealName;
                if (!used.Add(apiName)) apiName = id + "__" + apiName;
                t.ApiName = apiName;
                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = apiName,
                        ["description"] = t.Description,
                        ["parameters"] = t.Schema?.DeepClone() ?? new JsonObject { ["type"] = "object" }
                    }
                });
            }
        }
        return arr.Count > 0 ? arr : null;
    }

    public static async Task<string> CallToolAsync(string apiName, string argsJson)
    {
        foreach (var (_, conn) in Conns)
        {
            if (conn.Error != null) continue;
            var tool = conn.Tools.FirstOrDefault(t => t.ApiName == apiName);
            if (tool != null)
                return await conn.CallToolAsync(tool.RealName, argsJson);
        }
        throw new Exception("Outil introuvable : " + apiName);
    }
}
