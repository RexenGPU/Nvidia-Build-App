using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NvidiaBuildApp;

class MainForm : Form
{
    Conversation? _convo;
    readonly List<Conversation> _conversations = new();
    bool _busy;
    CancellationTokenSource? _cts;
    bool _jsReady;
    readonly Queue<string> _pending = new();

    readonly WebView2 _web = new();

    public MainForm()
    {
        Text = "NVIDIA Build App";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 660);
        Size = new Size(1240, 840);
        BackColor = Color.FromArgb(0x18, 0x18, 0x18);
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "?")
                   ?? System.Drawing.SystemIcons.Application;
        }
        catch { }

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = Color.FromArgb(0x18, 0x18, 0x18);
        Controls.Add(_web);

        _web.CoreWebView2InitializationCompleted += OnCoreInit;
        _web.WebMessageReceived += OnWebMessage;

        Shown += async (s, e) => await InitAsync();
        FormClosing += (s, e) => OnClosingAll();

        _conversations.AddRange(Store.LoadConversations());
        if (_conversations.Count > 0) _convo = _conversations[0];

        // relocalisation auto au demarrage : le prompt systeme suit la langue de l'interface
        // s'il correspond a un defaut connu d'une autre langue (etat mixte apres un ancien bug)
        foreach (var l in Loc.Languages)
        {
            if (l != Loc.L && Store.Settings.SystemPrompt == Loc.Sys(l))
            {
                Store.Settings.SystemPrompt = Loc.Sys(Loc.L);
                Store.SaveSettings();
                break;
            }
        }
    }

    async Task InitAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NvidiaBuildApp", "WebView2");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.S("webviewMissing") + ex.Message,
                "NVIDIA Build App", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void OnCoreInit(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            MessageBox.Show("Échec de l'initialisation de WebView2 : " + e.InitializationException?.Message,
                "NVIDIA Build App", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (s, ev) =>
        {
            ev.Handled = true;
            try { Process.Start(new ProcessStartInfo(ev.Uri) { UseShellExecute = true }); } catch { }
        };
        core.NavigateToString(BuildHtml());
    }

    string BuildHtml()
    {
        var asm = typeof(MainForm).Assembly;
        string html;
        using (var s = asm.GetManifestResourceStream("NvidiaBuildApp.ChatUi.html"))
        using (var r = new StreamReader(s ?? Stream.Null, Encoding.UTF8))
            html = r.ReadToEnd();

        string logo;
        using (var s = asm.GetManifestResourceStream("NvidiaBuildApp.assets.logo_small.png"))
        using (var ms = new MemoryStream())
        {
            (s ?? Stream.Null).CopyTo(ms);
            logo = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        return html.Replace("{{LOGO}}", logo);
    }

    // ---------------------------------------------------------------- pont JS

    static string J(object? o) => JsonSerializer.Serialize(o);

    void RunJs(string script)
    {
        if (!_jsReady) { _pending.Enqueue(script); return; }
        try { _ = _web.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        string t = root.TryGetProperty("t", out var tv) ? tv.GetString() ?? "" : "";

        switch (t)
        {
            case "ready":
                _jsReady = true;
                while (_pending.Count > 0) RunJs(_pending.Dequeue());
                PushAll();
                if (Store.Settings.FirstRun) RunJs("window.api.openLangPicker()");
                else FirstRunCheck();
                _ = RefreshMcpAndPush();
                _ = RefreshModelsAsync();
                try { File.AppendAllText(Path.Combine(Store.Dir, "ui_ready.log"), DateTime.Now.ToString("s") + Environment.NewLine); } catch { }
                break;
            case "newChat": NewChat(); break;
            case "open":
                {
                    var c = _conversations.FirstOrDefault(x => x.Id == root.GetProperty("id").GetString());
                    if (c != null) OpenConversation(c);
                    break;
                }
            case "rename": RenameConversation(root.GetProperty("id").GetString()); break;
            case "delete": DeleteConvo(root.GetProperty("id").GetString()); break;
            case "send":
                {
                    var text = root.GetProperty("text").GetString() ?? "";
                    var images = new List<string>();
                    if (root.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                        foreach (var im in imgs.EnumerateArray())
                        {
                            var s = im.GetString();
                            if (!string.IsNullOrEmpty(s)) images.Add(s!);
                        }
                    DoSend(text, images);
                    break;
                }
            case "stop": try { _cts?.Cancel(); } catch { } break;
            case "copy":
                try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { }
                break;
            case "copyMsg": CopyMessage(root.TryGetProperty("index", out var ci) ? ci.GetInt32() : -1); break;
            case "openUrl":
                try { Process.Start(new ProcessStartInfo(root.GetProperty("url").GetString() ?? "") { UseShellExecute = true }); } catch { }
                break;
            case "regenerate": RegenerateFrom(root.GetProperty("index").GetInt32()); break;
            case "deleteMsg": DeleteMessage(root.GetProperty("index").GetInt32()); break;
            case "editMsg":
                EditMessage(root.GetProperty("index").GetInt32(), root.GetProperty("text").GetString() ?? "");
                break;
            case "model": SetModel(root.GetProperty("name").GetString() ?? ""); break;
            case "pin": TogglePin(root.TryGetProperty("id", out var pidx) ? pidx.GetString() : null); break;
            case "export": ExportConversation(root.TryGetProperty("id", out var eidx) ? eidx.GetString() : null); break;
            case "clearMessages": ClearMessages(root.TryGetProperty("id", out var clidx) ? clidx.GetString() : null); break;

            case "saveSettings":
                {
                    Store.Settings.ApiKey = root.GetProperty("key").GetString() ?? "";
                    if (root.TryGetProperty("temperature", out var tempEl))
                        Store.Settings.Temperature = Math.Clamp((float)tempEl.GetDouble(), 0f, 2f);
                    if (root.TryGetProperty("maxTokens", out var mtEl))
                        Store.Settings.MaxTokens = Math.Clamp((int)mtEl.GetInt32(), 256, 32768);
                    Store.Settings.SystemPrompt = root.GetProperty("systemPrompt").GetString() ?? Nvidia.SystemPrompt;
                    Store.SaveSettings();
                    PushKeyMissing();
                    PushStatus();
                    _ = RefreshModelsAsync();
                    break;
                }
            case "addMcp":
                {
                    var cfg = new McpServerConfig
                    {
                        Name = root.GetProperty("name").GetString() ?? "Serveur MCP",
                        Transport = root.GetProperty("transport").GetString() == "http" ? "http" : "stdio",
                        Command = root.GetProperty("command").GetString() ?? "",
                        Url = root.GetProperty("url").GetString() ?? "",
                        Enabled = true,
                    };
                    if (cfg.Transport == "stdio" && cfg.Command.Trim().Length == 0) break;
                    if (cfg.Transport == "http" && cfg.Url.Trim().Length == 0) break;
                    Store.Settings.McpServers.Add(cfg);
                    Store.SaveSettings();
                    _ = RefreshMcpAndPush();
                    break;
                }
            case "removeMcp":
                {
                    var id = root.GetProperty("id").GetString();
                    var cfg = Store.Settings.McpServers.FirstOrDefault(s => s.Id == id);
                    if (cfg != null) { Store.Settings.McpServers.Remove(cfg); Store.SaveSettings(); }
                    if (id != null) Mcp.Drop(id);
                    PushMcp();
                    break;
                }
            case "toggleMcp":
                {
                    var id = root.GetProperty("id").GetString();
                    var cfg = Store.Settings.McpServers.FirstOrDefault(s => s.Id == id);
                    if (cfg != null) { cfg.Enabled = !cfg.Enabled; Store.SaveSettings(); }
                    _ = RefreshMcpAndPush();
                    break;
                }
            case "reconnectMcp":
                {
                    var id = root.GetProperty("id").GetString();
                    if (id != null) Mcp.Drop(id);
                    _ = RefreshMcpAndPush();
                    break;
                }
            case "deleteAllConvos": DeleteAllConversations(); break;
            case "setLang":
                {
                    var lang = root.GetProperty("lang").GetString() ?? "en";
                    if (!Loc.Languages.Contains(lang)) lang = "en";
                    var prev = string.IsNullOrWhiteSpace(Store.Settings.Lang) ? "en" : Store.Settings.Lang;
                    if (prev != lang)
                    {
                        // si le prompt est un defaut connu (quelle que soit la langue), le relocaliser
                        foreach (var l in Loc.Languages)
                        {
                            if (Store.Settings.SystemPrompt == Loc.Sys(l))
                            {
                                Store.Settings.SystemPrompt = Loc.Sys(lang);
                                break;
                            }
                        }
                    }
                    bool wasFirstRun = Store.Settings.FirstRun;
                    Store.Settings.Lang = lang;
                    Store.Settings.FirstRun = false;
                    Store.SaveSettings();
                    PushSettings();
                    PushConversations();
                    PushStatus();
                    if (wasFirstRun) FirstRunCheck();   // apres la langue : configurer la cle si absente
                    break;
                }
            case "jsError":
                try
                {
                    File.AppendAllText(Path.Combine(Store.Dir, "js_errors.log"),
                        DateTime.Now + " : " + root.GetProperty("error").GetString() + Environment.NewLine);
                }
                catch { }
                break;
        }
    }

    // ---------------------------------------------------------------- pushes

    void PushAll()
    {
        RunJs($"window.api.setLang({J(Store.Settings.Lang)})");
        PushSettings();
        RunJs($"window.api.setKey({J(Store.Settings.ApiKey)})");
        RunJs($"window.api.setModels({J(ModelList())}, {J(CurrentModel())})");
        PushConversations();
        PushMessages();
        PushStatus();
        PushKeyMissing();
        RunJs("window.api.setBusy(false)");
    }

    List<string> ModelList() =>
        Store.Settings.LiveModels is { Count: > 0 } ? Store.Settings.LiveModels : Nvidia.Models.ToList();

    /// <summary>
    /// Liste auto-adaptee : curates verifies presents sur l'API + nouveaux modeles textuels,
    /// les modeles disparus ou non-chat sont retires automatiquement.
    /// </summary>
    async Task RefreshModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey)) return;
        try
        {
            var fetched = await LlmClient.FetchModelsAsync(Nvidia.BaseUrl, Store.Settings.ApiKey);

            var list = Nvidia.Models.Where(fetched.Contains).ToList();   // curates toujours vivants
            foreach (var m in fetched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (list.Contains(m) || Nvidia.Models.Contains(m)) continue;
                if (Nvidia.KnownExcluded.Contains(m)) continue;         // morts / non-chat connus
                if (Nvidia.LooksNonChat(m)) continue;                   // nom typique non-chat
                list.Add(m);                                            // nouveau modele textuel
            }

            if (list.Count > 0)
            {
                Store.Settings.LiveModels = list;
                Store.SaveSettings();
                RunJs($"window.api.setModels({J(list)}, {J(CurrentModel())})");
            }
        }
        catch { }
    }

    void PushSettings()
    {
        RunJs($"window.api.setSettings({J(new
        {
            key = Store.Settings.ApiKey,
            temperature = Store.Settings.Temperature,
            maxTokens = Store.Settings.MaxTokens,
            systemPrompt = Store.Settings.SystemPrompt
        })})");
    }

    void PushMcp()
    {
        var rows = Store.Settings.McpServers.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            transport = s.Transport,
            command = s.Command,
            url = s.Url,
            enabled = s.Enabled,
            statusKey = !s.Enabled ? "disabled"
                : Mcp.ErrorOf(s.Id) != null ? "err"
                : Mcp.IsConnected(s.Id) ? "ok"
                : "off",
            tools = Mcp.ToolCountOf(s.Id),
            error = Mcp.ErrorOf(s.Id)
        }).ToList();
        RunJs($"window.api.setMcp({J(rows)})");
    }

    async Task RefreshMcpAndPush()
    {
        try { await Mcp.RefreshAsync(Store.Settings.McpServers); }
        catch { }
        PushMcp();
    }

    void PushConversations()
    {
        _conversations.Sort((a, b) =>
        {
            if (a.Pinned != b.Pinned) return b.Pinned ? 1 : -1;
            return b.UpdatedAt.CompareTo(a.UpdatedAt);
        });
        RunJs($"window.api.setConversations({J(_conversations.Select(c => new
        {
            id = c.Id,
            title = c.Title,
            date = c.UpdatedAt.ToString("dd/MM/yyyy HH:mm"),
            pinned = c.Pinned,
            active = c == _convo
        }).ToList())})");
    }

    void PushMessages()
    {
        var arr = new List<object>();
        if (_convo != null)
        {
            for (int i = 0; i < _convo.Messages.Count; i++)
            {
                var m = _convo.Messages[i];
                arr.Add(new
                {
                    role = m.Role,
                    content = m.Content,
                    reasoning = m.Reasoning,
                    model = m.Model,
                    index = i,
                    time = m.CreatedAt.ToString("HH:mm"),
                    elapsed = m.ElapsedMs.HasValue ? Math.Round(m.ElapsedMs.Value / 1000.0, 1) : (double?)null,
                    toolName = m.ToolName,
                    toolCalls = m.ToolCalls?.Select(tc => new { id = tc.Id, name = tc.Name, arguments = tc.Arguments }).ToList(),
                    thumbs = m.ImageThumbs
                });
            }
        }
        RunJs($"window.api.renderMessages({J(arr)})");
    }

    void PushStatus() => RunJs($"window.api.setStatus({J(StatusText())})");

    void PushKeyMissing()
    {
        bool missing = string.IsNullOrWhiteSpace(Store.Settings.ApiKey);
        RunJs($"window.api.setKeyMissing({J(missing)})");
    }

    string StatusText()
    {
        if (_busy) return Loc.S("busy");
        return string.IsNullOrWhiteSpace(Store.Settings.ApiKey) ? Loc.S("nokey") : Loc.S("ready");
    }

    // ---------------------------------------------------------------- actions

    void FirstRunCheck()
    {
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey))
            RunJs("window.api.openSettings()");
    }

    void NewChat()
    {
        if (_busy) return;
        _convo = null;
        PushMessages();
        PushConversations();
    }

    void OpenConversation(Conversation c)
    {
        if (_busy) return;
        _convo = c;
        PushMessages();
        PushConversations();
    }

    string CurrentModel()
    {
        var m = _convo?.Model ?? Store.Settings.LastModel;
        if (!string.IsNullOrEmpty(m) && ModelList().Contains(m!)) return m!;
        return Nvidia.DefaultModel;
    }

    void RenameConversation(string? id)
    {
        if (_busy) return;
        var c = _conversations.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        var name = PromptForm.Ask(this, Loc.S("renameTitle"), Loc.S("renameLabel"), c.Title);
        if (name is { Length: > 0 })
        {
            c.Title = name;
            c.Save();
            PushConversations();
        }
    }

    void DeleteConvo(string? id)
    {
        if (_busy) return;
        var c = _conversations.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        Store.DeleteConversation(c);
        _conversations.Remove(c);
        if (_convo == c) { _convo = null; PushMessages(); }
        PushConversations();
    }

    void DeleteAllConversations()
    {
        if (_busy) return;
        foreach (var c in _conversations.ToList())
            Store.DeleteConversation(c);
        _conversations.Clear();
        _convo = null;
        PushMessages();
        PushConversations();
    }

    void SetModel(string name)
    {
        if (!ModelList().Contains(name)) return;
        Store.Settings.LastModel = name;
        if (_convo != null) _convo.Model = name;
        Store.SaveSettings();
        PushStatus();
    }

    void DoSend(string text, List<string>? images = null)
    {
        images ??= new List<string>();
        if (_busy || (text.Trim().Length == 0 && images.Count == 0)) return;
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey))
        {
            RunJs("window.api.openSettings()");
            return;
        }

        var model = CurrentModel();
        if (images.Count > 0 && ModelList().Contains(model) && !Nvidia.LooksVision(model))
        {
            // les images restent dans la barre de pieces jointes (non effacees cote JS)
            RunJs($"window.api.showError({J(Loc.S("errNoVision"))})");
            return;
        }

        if (_convo == null)
        {
            _convo = new Conversation { Model = model };
            _conversations.Insert(0, _convo);
        }
        if (_convo.Messages.Count == 0)
        {
            var t = text.ReplaceLineEndings(" ").Trim();
            _convo.Title = t.Length > 0
                ? (t.Length > 44 ? t[..44] + "..." : t)
                : (images.Count > 0 ? "Images" : _convo.Title);
        }

        var msg = new ChatMessage { Role = "user", Content = text };
        if (images.Count > 0)
        {
            msg.Images = images;
            msg.ImageThumbs = images.Select(MakeThumb).ToList();
        }
        _convo.Messages.Add(msg);
        _convo.Save();

        if (images.Count > 0) RunJs("window.api.clearImages()");
        PushMessages();
        PushConversations();
        _ = GenerateReplyAsync();
    }

    /// <summary>Miniature 96px (JPEG) pour l'affichage dans l'UI.</summary>
    static string MakeThumb(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            using var ms = new MemoryStream(bytes);
            using var img = System.Drawing.Image.FromStream(ms);
            const int box = 96;
            var ratio = Math.Min((double)box / img.Width, (double)box / img.Height);
            var nw = Math.Max(1, (int)Math.Round(img.Width * ratio));
            var nh = Math.Max(1, (int)Math.Round(img.Height * ratio));
            using var bmp = new System.Drawing.Bitmap(nw, nh);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, 0, 0, nw, nh);
            }
            using var outMs = new MemoryStream();
            bmp.Save(outMs, System.Drawing.Imaging.ImageFormat.Jpeg);
            return "data:image/jpeg;base64," + Convert.ToBase64String(outMs.ToArray());
        }
        catch { return dataUrl; }
    }

    /// <summary>Titre de conversation genere par le modele apres le premier echange.</summary>
    async Task GenerateTitleAsync()
    {
        var convo = _convo;
        if (convo == null || Store.Settings.ApiKey.Length == 0) return;
        try
        {
            var firstUser = convo.Messages.FirstOrDefault(m => m.Role == "user");
            if (firstUser == null) return;
            var extract = firstUser.Content.ReplaceLineEndings(" ").Trim();
            if (extract.Length == 0) return;
            if (extract.Length > 600) extract = extract[..600];

            var title = await LlmClient.CompleteAsync(
                Nvidia.BaseUrl, Store.Settings.ApiKey, CurrentModel(),
                new List<ChatMessage> { new() { Role = "user", Content = extract } },
                Nvidia.TitlePrompt, 50);

            title = title.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`', '*', '#', '.', ':', '-');
            if (title.Length == 0) return;
            if (title.Length > 60) title = title[..60];
            convo.Title = title;
            convo.Save();
            PushConversations();
        }
        catch { }
    }

    class ToolAcc
    {
        public string? Id;
        public string? Name;
        public readonly StringBuilder Args = new();
    }

    async Task GenerateReplyAsync()
    {
        if (_convo == null) return;
        var model = CurrentModel();

        _busy = true;
        _cts = new CancellationTokenSource();
        RunJs("window.api.setBusy(true)");
        PushStatus();

        var sw = Stopwatch.StartNew();
        string reasonAll = "";
        int rounds = 0;
        string? error = null;

        try
        {
            while (true)
            {
                rounds++;
                if (rounds > 8)
                {
                    _convo.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = Loc.S("rounds"),
                        Model = model
                    });
                    _convo.Save();
                    RunJs("window.api.cancelStream()");
                    PushMessages();
                    break;
                }

                RunJs($"window.api.streamStart({J(model)})");

                var contentBuf = new StringBuilder();
                var reasonBuf = new StringBuilder();
                var toolAcc = new List<ToolAcc>();
                long lastTick = 0;

                var history = _convo.Messages.TakeLast(60).ToList();
                await foreach (var d in LlmClient.StreamAsync(
                    Nvidia.BaseUrl, Store.Settings.ApiKey, model, history,
                    Store.Settings.SystemPrompt, Store.Settings.Temperature, Store.Settings.MaxTokens,
                    Mcp.ToolDefinitions(), _cts.Token))
                {
                    if (d.Reasoning is { Length: > 0 }) reasonBuf.Append(d.Reasoning);
                    if (d.Content is { Length: > 0 }) contentBuf.Append(d.Content);
                    if (d.ToolChunks != null)
                    {
                        foreach (var ch in d.ToolChunks)
                        {
                            while (toolAcc.Count <= ch.Index) toolAcc.Add(new ToolAcc());
                            if (ch.Id != null && toolAcc[ch.Index].Id == null) toolAcc[ch.Index].Id = ch.Id;
                            if (ch.Name != null && toolAcc[ch.Index].Name == null) toolAcc[ch.Index].Name = ch.Name;
                            if (ch.ArgsDelta != null) toolAcc[ch.Index].Args.Append(ch.ArgsDelta);
                        }
                    }

                    long now = Environment.TickCount64;
                    if (now - lastTick > 80)
                    {
                        lastTick = now;
                        var (liveReason, liveContent) = Think.Split(contentBuf.ToString());
                        string? reasoning = reasonBuf.Length > 0 ? reasonBuf.ToString()
                            : (liveReason.Length > 0 ? liveReason : null);
                        RunJs($"window.api.streamUpdate({J(liveContent)}, {J(reasoning)})");
                    }
                }

                if (reasonBuf.Length > 0)
                    reasonAll += (reasonAll.Length > 0 ? "\n" : "") + reasonBuf.ToString();

                var toolCalls = toolAcc
                    .Where(ta => !string.IsNullOrEmpty(ta.Name))
                    .Select(ta => new ToolCallData
                    {
                        Id = ta.Id ?? "call_" + Guid.NewGuid().ToString("N")[..12],
                        Name = ta.Name!,
                        Arguments = ta.Args.Length > 0 ? ta.Args.ToString() : "{}"
                    })
                    .ToList();

                if (toolCalls.Count == 0)
                {
                    // ---- réponse finale
                    var (thinkReason, clean) = Think.Split(contentBuf.ToString());
                    if (string.IsNullOrEmpty(reasonAll) && thinkReason.Length > 0) reasonAll = thinkReason;

                    var final = new ChatMessage
                    {
                        Role = "assistant",
                        Model = model,
                        Content = clean.Length > 0 ? clean : (reasonAll.Length > 0 ? "" : Loc.S("emptyReply")),
                        Reasoning = reasonAll.Length > 0 ? reasonAll : null,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                    _convo.Messages.Add(final);
                    _convo.Save();

                    int idx = _convo.Messages.Count - 1;
                    double elapsed = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    RunJs($"window.api.streamEnd({J(final.Content)}, {J(final.Reasoning)}, {J(model)}, {idx}, {elapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)})");

                    // auto-titre apres le premier echange
                    if (_convo.Messages.Count == 2)
                        _ = GenerateTitleAsync();
                    break;
                }

                // ---- le modele demande des outils
                var callMsg = new ChatMessage
                {
                    Role = "assistant",
                    Model = model,
                    Content = Think.Strip(contentBuf.ToString()),
                    Reasoning = reasonBuf.Length > 0 ? reasonBuf.ToString() : null,
                    ToolCalls = toolCalls,
                };
                _convo.Messages.Add(callMsg);
                _convo.Save();

                RunJs("window.api.cancelStream()");
                PushMessages();

                foreach (var tc in toolCalls)
                {
                    RunJs($"window.api.setStatus({J(string.Format(Loc.S("tool"), tc.Name))})");
                    string result;
                    try { result = await Mcp.CallToolAsync(tc.Name, tc.Arguments); }
                    catch (Exception tex) { result = "Erreur : " + tex.Message; }
                    _convo.Messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        Content = result
                    });
                }
                _convo.Save();
                PushMessages();
            }
        }
        catch (OperationCanceledException) { RunJs("window.api.cancelStream()"); }
        catch (LlmException ex) when (ex.StatusCode == 404)
        {
            // modele plus disponible : le retirer de la liste et revenir au modele par defaut
            if (Store.Settings.LiveModels != null && Store.Settings.LiveModels.RemoveAll(x => x == model) > 0)
                Store.SaveSettings();
            if (Store.Settings.LastModel == model) Store.Settings.LastModel = Nvidia.DefaultModel;
            if (_convo != null && _convo.Model == model) _convo.Model = Nvidia.DefaultModel;
            RunJs($"window.api.setModels({J(ModelList())}, {J(CurrentModel())})");
            RunJs("window.api.cancelStream()");
            RunJs($"window.api.showError({J(Loc.S("err404"))})");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            RunJs("window.api.cancelStream()");
            RunJs($"window.api.showError({J(ex.Message)})");
        }

        _busy = false;
        _cts?.Dispose(); _cts = null;
        RunJs("window.api.setBusy(false)");
        PushConversations();
        PushStatus();
    }

    void RegenerateFrom(int index)
    {
        if (_busy || _convo == null) return;
        if (index < 0 || index >= _convo.Messages.Count) return;
        if (_convo.Messages[index].Role != "assistant") return;
        _convo.Messages.RemoveRange(index, _convo.Messages.Count - index);
        _convo.Save();
        PushMessages();
        _ = GenerateReplyAsync();
    }

    void DeleteMessage(int index)
    {
        if (_busy || _convo == null) return;
        if (index < 0 || index >= _convo.Messages.Count) return;
        _convo.Messages.RemoveAt(index);
        _convo.Save();
        PushMessages();
    }

    void EditMessage(int index, string text)
    {
        if (_busy || _convo == null || string.IsNullOrWhiteSpace(text)) return;
        if (index < 0 || index >= _convo.Messages.Count) return;
        if (_convo.Messages[index].Role != "user") return;

        var oldImages = _convo.Messages[index].Images;
        var oldThumbs = _convo.Messages[index].ImageThumbs;
        _convo.Messages.RemoveRange(index, _convo.Messages.Count - index);
        var edited = new ChatMessage { Role = "user", Content = text, Images = oldImages, ImageThumbs = oldThumbs };
        _convo.Messages.Add(edited);
        _convo.Save();

        PushMessages();
        PushConversations();
        _ = GenerateReplyAsync();
    }

    void CopyMessage(int index)
    {
        if (_convo == null) return;
        var m = _convo.Messages.ElementAtOrDefault(index);
        if (m == null) return;
        try { Clipboard.SetText(m.Content); } catch { }
    }

    void TogglePin(string? id)
    {
        var c = _conversations.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        c.Pinned = !c.Pinned;
        c.Save();
        PushConversations();
    }

    void ClearMessages(string? id)
    {
        if (_busy) return;
        var c = _conversations.FirstOrDefault(x => x.Id == id) ?? _convo;
        if (c == null || c.Messages.Count == 0) return;
        c.Messages.Clear();
        c.Save();
        if (c == _convo) PushMessages();
        PushConversations();
    }

    void ExportConversation(string? id)
    {
        var c = _conversations.FirstOrDefault(x => x.Id == id) ?? _convo;
        if (c == null || c.Messages.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("# " + c.Title);
        sb.AppendLine();
        sb.AppendLine(string.Format(Loc.S("exportHeader"),
            DateTime.Now.ToString(Loc.S("dateFmt").Replace("HH", "HH").Replace("mm", "mm")), c.Model ?? CurrentModel()));
        sb.AppendLine();
        foreach (var m in c.Messages)
        {
            sb.AppendLine(m.Role == "user"
                ? "## Vous"
                : m.Role == "tool"
                    ? $"## {m.ToolName}"
                    : $"## NVIDIA Build App{(m.Model != null ? $" ({m.Model})" : "")}");
            sb.AppendLine();
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }

        var invalid = Path.GetInvalidFileNameChars();
        var fileName = new string(c.Title.Where(ch => !invalid.Contains(ch)).ToArray());
        if (fileName.Length == 0) fileName = "conversation";
        if (fileName.Length > 60) fileName = fileName[..60];

        using var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Texte (*.txt)|*.txt",
            FileName = fileName + ".md",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            RunJs($"window.api.setStatus({J(string.Format(Loc.S("exported"), Path.GetFileName(dlg.FileName)))})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.S("exportFail") + ex.Message, "NVIDIA Build App",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnClosingAll()
    {
        try { _cts?.Cancel(); } catch { }
        _convo?.Save();
        Store.SaveSettings();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(this);
    }
}
