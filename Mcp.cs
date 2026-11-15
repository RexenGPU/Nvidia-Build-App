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
    readonly StringBuilder _stderr = new();

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
                ["clientInfo"] = new JsonObject { ["name"] = "NvidiaBuildApp", ["version"] = "1.2.0" }
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
            var detail = _stderr.Length > 0 ? ex.Message + " — " + _stderr.ToString().Trim() : ex.Message;
            Error = detail.Length > 800 ? detail[..800] + "…" : detail;
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
        _proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 4000) _stderr.Remove(0, _stderr.Length - 4000);
            }
        };
        _proc.EnableRaisingEvents = true;
        _proc.Exited += (s, e) =>
        {
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var t))
                    t.TrySetException(new Exception("Le serveur MCP s'est arrêté"));
        };
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

    async Task<JsonNode> RequestAsync(string method, JsonNode? prms, int timeoutMs, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _nextId) - 1;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (prms != null) msg["params"] = prms.DeepClone();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);

        if (_cfg.Transport == "stdio")
        {
            if (_proc == null || _proc.HasExited)
                throw new Exception("Le serveur MCP n'est pas démarré");

            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            using var reg = linked.Token.Register(() =>
            {
                if (!_pending.TryRemove(id, out var t2)) return;
                if (ct.IsCancellationRequested) t2.TrySetCanceled(ct);
                else t2.TrySetException(new TimeoutException($"{method} : pas de réponse du serveur MCP"));
            });

            try
            {
                await _proc.StandardInput.WriteLineAsync(msg.ToJsonString());
                await _proc.StandardInput.FlushAsync();
                var node = await tcs.Task;
                return Handle(node);
            }
            catch
            {
                _pending.TryRemove(id, out _);
                throw;
            }
        }
        else
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
            {
                Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (_session != null)
            {
                req.Headers.Add("Mcp-Session-Id", _session);
                req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
            }

            using var resp = await _http!.SendAsync(req, linked.Token);
            var body = await resp.Content.ReadAsStringAsync(linked.Token);
            if (!resp.IsSuccessStatusCode)
                throw new Exception("MCP HTTP " + (int)resp.StatusCode + " : " +
                    (body.Length > 400 ? body[..400] + "…" : body));

            if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sess))
                _session = sess.FirstOrDefault();

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";

            JsonNode? node = null;
            if (mediaType.Contains("event-stream"))
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
            var req = new HttpRequestMessage(HttpMethod.Post, _cfg.Url)
            {
                Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (_session != null)
            {
                req.Headers.Add("Mcp-Session-Id", _session);
                req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
            }
            _ = Task.Run(async () =>
            {
                try { using var resp = await _http.SendAsync(req); }
                catch { }
                finally { req.Dispose(); }
            });
        }
    }

    // ------------------------------------------------------------------ outils

    public async Task<string> CallToolAsync(string name, string argsJson, CancellationToken ct = default)
    {
        JsonNode? args;
        try { args = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson); }
        catch { args = new JsonObject(); }
        if (args is not JsonObject) args = new JsonObject();

        var result = await RequestAsync("tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, 120000, ct);

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
    static readonly ConcurrentDictionary<string, McpConnection> Conns = new();
    static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public static bool IsConnected(string id) => Conns.TryGetValue(id, out var c) && c.Error == null;
    public static int ToolCountOf(string id) => Conns.TryGetValue(id, out var c) && c.Error == null ? c.ToolCount : 0;
    public static string? ErrorOf(string id) => Conns.TryGetValue(id, out var c) ? c.Error : null;
    public static string StatusOf(string id) => Conns.TryGetValue(id, out var c) ? c.Status : "Non connecté";
    public static int TotalTools => Conns.Values.Sum(c => c.Error == null ? c.ToolCount : 0);

    /// <summary>Synchronise les connexions avec la configuration (connecte/retire au besoin).</summary>
    public static async Task RefreshAsync(IEnumerable<McpServerConfig> servers)
    {
        await RefreshGate.WaitAsync();
        try
        {
            var wanted = servers.Where(s => s.Enabled).ToList();

            foreach (var id in Conns.Keys.Except(wanted.Select(w => w.Id)).ToList())
                if (Conns.TryRemove(id, out var old)) old.Dispose();

            foreach (var w in wanted)
            {
                if (Conns.TryGetValue(w.Id, out var existing) && existing.Matches(w)) continue;
                if (Conns.TryRemove(w.Id, out var previous)) previous.Dispose();

                // on n'expose la connexion qu'une fois initialisee : pas de liste d'outils
                // partiellement remplie visible par une generation en cours.
                var conn = new McpConnection(w);
                await conn.InitAsync();
                Conns[w.Id] = conn;
            }
        }
        finally { RefreshGate.Release(); }
    }

    public static void Drop(string id)
    {
        if (Conns.TryRemove(id, out var c)) c.Dispose();
    }

    static string SafeName(string raw, HashSet<string> used)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        var name = sb.ToString().Trim('_');
        if (name.Length == 0) name = "tool";
        if (char.IsDigit(name[0])) name = "t_" + name;
        if (name.Length > 60) name = name[..60];

        var candidate = name;
        int n = 2;
        while (!used.Add(candidate))
        {
            var suffix = "_" + n++;
            candidate = name.Length + suffix.Length > 64
                ? name[..(64 - suffix.Length)] + suffix
                : name + suffix;
        }
        return candidate;
    }

    /// <summary>Definitions d'outils au format OpenAI (tools) — null si aucun outil.</summary>
    public static JsonArray? ToolDefinitions()
    {
        var defs = new List<(string Name, JsonObject Def)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conn in Conns.Values)
        {
            if (conn.Error != null) continue;
            foreach (var t in conn.Tools)
            {
                var apiName = SafeName(t.RealName, used);
                t.ApiName = apiName;
                defs.Add((apiName, new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = apiName,
                        ["description"] = t.Description,
                        ["parameters"] = t.Schema?.DeepClone() ?? new JsonObject { ["type"] = "object" }
                    }
                }));
            }
        }

        if (defs.Count == 0) return null;
        var arr = new JsonArray();
        foreach (var d in defs.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            arr.Add(d.Def);
        return arr;
    }

    public static async Task<string> CallToolAsync(string apiName, string argsJson, CancellationToken ct = default)
    {
        foreach (var conn in Conns.Values)
        {
            if (conn.Error != null) continue;
            var tool = conn.Tools.FirstOrDefault(t => t.ApiName == apiName);
            if (tool != null)
                return await conn.CallToolAsync(tool.RealName, argsJson, ct);
        }
        throw new Exception("Outil introuvable : " + apiName);
    }
}
