using System.Diagnostics;
using System.Security.Cryptography;
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

    // rafraichissement de la liste des modeles (fichier partage : un seul a la fois)
    readonly SemaphoreSlim _modelGate = new(1, 1);
    readonly System.Windows.Forms.Timer _modelTimer = new() { Interval = 10 * 60 * 1000 };
    static readonly TimeSpan ModelRefreshMinInterval = TimeSpan.FromMinutes(5);
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30 * 60 * 1000 };

    // mise a jour (GitHub releases)
    UpdateInfo? _update;
    bool _updateBusy;
    static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromMinutes(30);

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

        // la liste des modeles suit le catalogue NVIDIA : rafraichie au demarrage,
        // toutes les 10 min, au retour sur la fenetre et a l'ouverture du selecteur
        _modelTimer.Tick += (s, e) => _ = RefreshModelsAsync();
        _modelTimer.Start();
        // le pas de mise a jour : verification au demarrage puis toutes les 30 min (derniere verification memorisee)
        _updateTimer.Tick += (s, e) => _ = CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
        Activated += (s, e) => { _ = RefreshModelsAsync(); _ = CheckForUpdatesAsync(manual: false); };

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
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // l'interface ne doit jamais quitter la page locale : tout lien part au navigateur
        core.NavigationStarting += (s, ev) =>
        {
            if (IsLocalPage(ev.Uri)) return;
            ev.Cancel = true;
            OpenExternal(ev.Uri);
        };
        core.NewWindowRequested += (s, ev) =>
        {
            ev.Handled = true;
            OpenExternal(ev.Uri);
        };

        core.NavigateToString(BuildHtml());
    }

    static bool IsLocalPage(string? uri) =>
        string.IsNullOrEmpty(uri) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>N'ouvre que des URL web(http/https) ; refuse file:, javascript:, ms-*, etc.</summary>
    static void OpenExternal(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
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

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var version = asm.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

        return html
            .Replace("{{LOGO}}", logo)
            .Replace("{{NONCE}}", nonce)
            .Replace("{{VERSION}}", version);
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
        // une trame malformee ne doit jamais faire tomber l'application
        try { OnWebMessageCore(e); }
        catch (Exception ex) { LogUi("message: " + ex.Message); }
    }

    static void LogUi(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Store.Dir, "ui_errors.log"),
                DateTime.Now.ToString("s") + " : " + line + Environment.NewLine);
        }
        catch { }
    }

    void OnWebMessageCore(CoreWebView2WebMessageReceivedEventArgs e)
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
                _ = CheckForUpdatesAsync(manual: false);
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
                OpenExternal(root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null);
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
            case "retry": RetryLast(); break;
            case "refreshModels":
                {
                    bool force = root.TryGetProperty("force", out var fm) && fm.ValueKind == JsonValueKind.True;
                    _ = RefreshModelsAsync(force, force);
                    break;
                }
            case "checkUpdate": _ = CheckForUpdatesAsync(manual: true); break;
            case "installUpdate": _ = InstallUpdateAsync(); break;
            case "skipUpdate": SkipUpdate(); break;
            case "openUpdatePage": OpenExternal(_update?.Url); break;

            case "saveSettings":
                {
                    bool clear = root.TryGetProperty("clearKey", out var ck) && ck.ValueKind == JsonValueKind.True;
                    if (clear)
                    {
                        Store.Settings.ApiKey = "";
                    }
                    else if (root.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
                    {
                        var key = keyEl.GetString() ?? "";
                        if (key.Length > 0) Store.Settings.ApiKey = key;   // vide = conserver la cle existante
                    }
                    if (root.TryGetProperty("temperature", out var tempEl))
                        Store.Settings.Temperature = Math.Clamp((float)tempEl.GetDouble(), 0f, 2f);
                    if (root.TryGetProperty("maxTokens", out var mtEl))
                        Store.Settings.MaxTokens = Math.Clamp(mtEl.GetInt32(), 256, 32768);
                    if (root.TryGetProperty("systemPrompt", out var spEl) && spEl.ValueKind == JsonValueKind.String)
                        Store.Settings.SystemPrompt = spEl.GetString() ?? Loc.Sys(Loc.L);
                    Store.SaveSettings();
                    RunJs($"window.api.setHasKey({J(!string.IsNullOrWhiteSpace(Store.Settings.ApiKey))})");
                    PushSettings();
                    PushKeyMissing();
                    PushStatus();
                    if (!string.IsNullOrWhiteSpace(Store.Settings.ApiKey))
                        _ = RefreshModelsAsync(force: true, notify: true);
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
            case "dictate": _ = StartDictationAsync(); break;
            case "dictateStop":
                _dictationCancelled = true;
                try { _capture?.StopRecording(); } catch { }
                break;
            case "setMic":
                Store.Settings.MicDeviceId = string.IsNullOrWhiteSpace(root.GetProperty("id").GetString()) ? null : root.GetProperty("id").GetString();
                Store.SaveSettings();
                PushSettings();
                break;
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
        RunJs($"window.api.setHasKey({J(!string.IsNullOrWhiteSpace(Store.Settings.ApiKey))})");
        RunJs($"window.api.setModels({J(ModelList())}, {J(CurrentModel())})");
        PushConversations();
        PushMessages();
        PushStatus();
        PushKeyMissing();
        PushUpdateState("idle");
        RunJs("window.api.setBusy(false)");
    }

    /// <summary>
    /// Liste affichee : cache de l'API filtre (modeles retires / non-chat / morts),
    /// avec repli sur la liste curatee. Ne contient jamais un modele marque mort.
    /// </summary>
    List<string> ModelList()
    {
        var dead = Store.Settings.DeadModels;
        IEnumerable<string> source = Store.Settings.LiveModels is { Count: > 0 } ? Store.Settings.LiveModels : Nvidia.Models;
        var list = source
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Where(m => !Nvidia.KnownExcluded.Contains(m) && !Nvidia.LooksNonChat(m))
            .Where(m => dead == null || !dead.Contains(m, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0)
            list = Nvidia.Models.Where(m => dead == null || !dead.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        return list;
    }

    /// <summary>Un modele a renvoye 404 : on le retire definitivement de toutes les listes.</summary>
    void MarkModelDead(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return;
        var dead = Store.Settings.DeadModels ??= new List<string>();
        if (!dead.Contains(model, StringComparer.OrdinalIgnoreCase)) dead.Add(model);
        Store.Settings.LiveModels?.RemoveAll(x => string.Equals(x, model, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(Store.Settings.LastModel, model, StringComparison.OrdinalIgnoreCase))
            Store.Settings.LastModel = "";
        if (_convo != null && string.Equals(_convo.Model, model, StringComparison.OrdinalIgnoreCase))
        {
            _convo.Model = FallbackModel(ModelList());
            _convo.Save();
        }
        Store.SaveSettings();
    }

    /// <summary>
    /// Rafraichit la liste depuis GET /models : les modeles supprimes disparaissent,
    /// les nouveaux modeles textuels apparaissent. Throttle 5 min sauf force.
    /// </summary>
    async Task RefreshModelsAsync(bool force = false, bool notify = false)
    {
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey))
        {
            if (notify)
            {
                RunJs($"window.api.setStatus({J(Loc.S("modelsNoKey"))})");
                RunJs("window.api.modelsRefreshDone()");
            }
            return;
        }
        if (!await _modelGate.WaitAsync(0)) { if (notify) RunJs("window.api.modelsRefreshDone()"); return; }   // deja en cours
        try
        {
            var fetchedAt = Store.Settings.ModelsFetchedAt;
            if (!force && fetchedAt is { } last && DateTime.UtcNow - last < ModelRefreshMinInterval) return;

            List<string> fetched;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                fetched = await LlmClient.FetchModelsAsync(Nvidia.BaseUrl, Store.Settings.ApiKey, cts.Token);
            if (fetched.Count == 0) return;

            var live = new HashSet<string>(fetched, StringComparer.OrdinalIgnoreCase);

            var list = Nvidia.Models.Where(live.Contains).ToList();   // curates toujours vivants
            foreach (var m in fetched.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (Nvidia.Models.Contains(m, StringComparer.OrdinalIgnoreCase)) continue;
                if (Nvidia.KnownExcluded.Contains(m) || Nvidia.LooksNonChat(m)) continue;
                if (list.Contains(m, StringComparer.OrdinalIgnoreCase)) continue;
                list.Add(m);                                            // nouveau modele textuel
            }
            if (list.Count == 0) return;                                // reponse inattendue : on garde le cache

            // un modele revenu en ligne n'est plus considere comme mort
            if (Store.Settings.DeadModels is { Count: > 0 } dead) dead.RemoveAll(live.Contains);

            Store.Settings.LiveModels = list;
            Store.Settings.ModelsFetchedAt = DateTime.UtcNow;

            // une conversation / un dernier modele qui pointait sur un modele disparu bascule
            var cur = CurrentModel();
            if (_convo != null && !string.IsNullOrEmpty(_convo.Model) &&
                !list.Contains(_convo.Model, StringComparer.OrdinalIgnoreCase))
            {
                _convo.Model = cur;
                _convo.Save();
            }
            if (!string.IsNullOrEmpty(Store.Settings.LastModel) &&
                !list.Contains(Store.Settings.LastModel, StringComparer.OrdinalIgnoreCase))
                Store.Settings.LastModel = cur;
            Store.SaveSettings();

            RunJs($"window.api.setModels({J(list)}, {J(cur)})");
            if (notify) RunJs($"window.api.setStatus({J(string.Format(Loc.S("modelsRefreshed"), list.Count))})");
        }
        catch
        {
            if (notify) RunJs($"window.api.setStatus({J(Loc.S("modelsRefreshFailed"))})");
        }
        finally { _modelGate.Release(); if (notify) RunJs("window.api.modelsRefreshDone()"); }
    }

    // ---------------------------------------------------------------- mise a jour

    /// <summary>Verifie les releases GitHub (30 min en auto, ou a la demande).</summary>
    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        if (!manual)
        {
            var last = Store.Settings.UpdateCheckedAt;
            if (last is { } t && DateTime.UtcNow - t < UpdateCheckInterval) return;
        }

        _updateBusy = true;
        if (manual) PushUpdateState("checking");
        try
        {
            var info = await UpdateService.CheckAsync();
            Store.Settings.UpdateCheckedAt = DateTime.UtcNow;
            Store.SaveSettings();
            _update = info;

            if (info == null)
            {
                PushUpdateState(manual ? "uptodate" : "idle");
                return;
            }
            if (!manual && string.Equals(Store.Settings.SkippedUpdate, info.Version, StringComparison.OrdinalIgnoreCase))
            {
                PushUpdateState("idle");   // deja ignoree par l'utilisateur
                return;
            }
            PushUpdateState("available");
        }
        catch (Exception ex)
        {
            PushUpdateState(manual ? "error" : "idle", manual ? ex.Message : null);
        }
        finally { _updateBusy = false; }
    }

    void PushUpdateState(string state, string? detail = null)
    {
        RunJs($"window.api.setUpdate({J(new
        {
            state,
            version = _update?.Version,
            url = _update?.Url,
            notes = _update?.Notes,
            current = UpdateService.CurrentText,
            canAuto = _update?.AssetUrl != null && !string.IsNullOrEmpty(_update.Sha256) && UpdateService.CanSelfUpdate,
            detail
        })})");
    }

    void SkipUpdate()
    {
        if (_update == null) return;
        Store.Settings.SkippedUpdate = _update.Version;
        Store.SaveSettings();
        PushUpdateState("idle");
    }

    async Task InstallUpdateAsync()
    {
        var info = _update;
        if (info == null || _updateBusy) return;
        if (_busy)
        {
            PushUpdateState("busy");
            return;
        }

        // pas de remplacement automatique possible : on ouvre la page de la release
        if (info.AssetUrl == null || string.IsNullOrEmpty(info.Sha256) || !UpdateService.CanSelfUpdate)
        {
            OpenExternal(info.Url);
            PushUpdateState("manual");
            return;
        }

        _updateBusy = true;
        try
        {
            PushUpdateState("downloading");
            var path = await UpdateService.DownloadAsync(info, new Progress<double>(p =>
                RunJs($"window.api.setUpdateProgress({p.ToString("0", System.Globalization.CultureInfo.InvariantCulture)})")));
            PushUpdateState("ready");
            UpdateService.ApplyAndRestart(path, info.Sha256);
            Close();   // le script attend la fermeture, remplace l'exe et relance
        }
        catch (Exception ex)
        {
            PushUpdateState("error", ex.Message);
        }
        finally { _updateBusy = false; }
    }

    void PushSettings()
    {
        RunJs($"window.api.setSettings({J(new
        {
            hasKey = !string.IsNullOrWhiteSpace(Store.Settings.ApiKey),
            temperature = Store.Settings.Temperature,
            maxTokens = Store.Settings.MaxTokens,
            systemPrompt = Store.Settings.SystemPrompt,
            mic = Store.Settings.MicDeviceId,
            mics = EnumerateMics()
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
            date = c.UpdatedAt.ToString("o"),
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
                    time = m.CreatedAt.ToString("o"),
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
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey) && !Store.Settings.SettingsPrompted)
        {
            Store.Settings.SettingsPrompted = true;
            Store.SaveSettings();
            RunJs("window.api.openSettings()");
        }
    }

    void NewChat()
    {
        if (_busy) { RunJs($"window.api.setStatus({J(Loc.S("busyStop"))})"); return; }
        _convo = null;
        PushMessages();
        PushConversations();
    }

    void OpenConversation(Conversation c)
    {
        if (_busy) { RunJs($"window.api.setStatus({J(Loc.S("busyStop"))})"); return; }
        _convo = c;
        PushMessages();
        PushConversations();
    }

    string CurrentModel()
    {
        var list = ModelList();
        var m = _convo?.Model ?? Store.Settings.LastModel;
        if (!string.IsNullOrEmpty(m) && list.Contains(m!)) return m!;
        return FallbackModel(list);
    }

    /// <summary>Premier modele utilisable : le modele par defaut s'il est encore en ligne, sinon le premier de la liste.</summary>
    static string FallbackModel(List<string> list)
    {
        if (list.Contains(Nvidia.DefaultModel)) return Nvidia.DefaultModel;
        return list.Count > 0 ? list[0] : Nvidia.DefaultModel;
    }

    void RenameConversation(string? id)
    {
        if (_busy) { RunJs($"window.api.setStatus({J(Loc.S("busyStop"))})"); return; }
        var c = _conversations.FirstOrDefault(x => x.Id == id);
        if (c == null) return;
        var name = PromptForm.Ask(this, Loc.S("renameTitle"), Loc.S("renameLabel"), c.Title);
        if (name is { Length: > 0 })
        {
            if (name.Length > 80) name = name[..80];
            c.Title = name;
            c.TitleLocked = true;   // un titre choisi par l'utilisateur n'est jamais ecrase
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
        if (_convo != null)
        {
            _convo.Model = name;
            _convo.Save();
        }
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
            _convo = new Conversation { Model = model, Title = Loc.S("newConvoTitle") };
            _conversations.Insert(0, _convo);
        }
        if (_convo.Messages.Count == 0)
        {
            var t = text.ReplaceLineEndings(" ").Trim();
            if (t.Length > 0) _convo.Title = t.Length > 44 ? t[..44] + "..." : t;
            else if (images.Count > 0) _convo.Title = Loc.S("imagesTitle");
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
    async Task GenerateTitleAsync(Conversation convo)
    {
        if (convo.TitleLocked || convo.TitleGenerated) return;
        if (string.IsNullOrWhiteSpace(Store.Settings.ApiKey)) return;
        try
        {
            var firstUser = convo.Messages.FirstOrDefault(m => m.Role == "user");
            if (firstUser == null) return;
            var extract = firstUser.Content.ReplaceLineEndings(" ").Trim();
            if (extract.Length == 0) return;
            if (extract.Length > 600) extract = extract[..600];

            // un modele mort (404) ne doit pas etre utilise pour generer le titre
            var titleModel = convo.Model != null && ModelList().Contains(convo.Model) ? convo.Model : CurrentModel();
            var title = await LlmClient.CompleteAsync(
                Nvidia.BaseUrl, Store.Settings.ApiKey, titleModel,
                new List<ChatMessage> { new() { Role = "user", Content = extract } },
                Nvidia.TitlePrompt, 50);

            // la conversation a pu etre renommee ou supprimee pendant l'appel
            if (convo.TitleLocked || convo.TitleGenerated || !_conversations.Contains(convo)) return;

            title = title.ReplaceLineEndings(" ").Trim().Trim('"', '\'', '`', '*', '#', '.', ':', '-');
            if (title.Length == 0) return;
            if (title.Length > 60) title = title[..60];
            convo.Title = title;
            convo.TitleGenerated = true;
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

    readonly HashSet<string> _alwaysTools = new(StringComparer.Ordinal);

    /// <summary>Historique borne : on ne coupe jamais entre un appel d'outil et son resultat.</summary>
    static List<ChatMessage> BuildHistory(Conversation convo)
    {
        var msgs = convo.Messages;
        int start = Math.Max(0, msgs.Count - 60);
        while (start < msgs.Count && msgs[start].Role != "user") start++;
        if (start >= msgs.Count) start = Math.Max(0, msgs.Count - 60);
        return msgs.Skip(start).ToList();
    }

    /// <summary>Conserve une reponse interrompue (arret utilisateur ou erreur reseau).</summary>
    static void SavePartial(Conversation convo, string model, string content, string reasoning, long elapsedMs)
    {
        var (thinkReason, clean) = Think.Split(content ?? "");
        if (string.IsNullOrEmpty(reasoning) && thinkReason.Length > 0) reasoning = thinkReason;
        if (string.IsNullOrWhiteSpace(clean) && string.IsNullOrWhiteSpace(reasoning)) return;
        if (convo.Messages.LastOrDefault() is { Role: "assistant" } last && last.Content == clean) return;

        convo.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Model = model,
            Content = clean,
            Reasoning = string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
            ElapsedMs = elapsedMs,
        });
        convo.Save();
    }

    ToolConfirmForm.Answer AskToolApproval(ToolCallData tc)
    {
        if (_alwaysTools.Contains(tc.Name)) return ToolConfirmForm.Answer.Once;
        var args = tc.Arguments ?? "{}";
        if (args.Length > 6000) args = args[..6000] + "\n…";
        var answer = ToolConfirmForm.Ask(this, tc.Name, args);
        if (answer == ToolConfirmForm.Answer.Always) _alwaysTools.Add(tc.Name);
        return answer;
    }

    async Task GenerateReplyAsync()
    {
        var convo = _convo;
        if (convo == null) return;
        var model = CurrentModel();

        _busy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunJs("window.api.setBusy(true)");
        PushStatus();

        var sw = Stopwatch.StartNew();
        string reasonAll = "";
        string roundContent = "", roundReason = "";
        int rounds = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                rounds++;
                if (rounds > 8)
                {
                    convo.Messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = Loc.S("rounds"),
                        Model = model
                    });
                    convo.Save();
                    RunJs("window.api.cancelStream()");
                    PushMessages();
                    break;
                }

                RunJs($"window.api.streamStart({J(model)})");

                var contentBuf = new StringBuilder();
                var reasonBuf = new StringBuilder();
                var toolAcc = new List<ToolAcc>();
                long lastTick = 0;
                roundContent = "";
                roundReason = "";

                var history = BuildHistory(convo);
                await foreach (var d in LlmClient.StreamAsync(
                    Nvidia.BaseUrl, Store.Settings.ApiKey, model, history,
                    Store.Settings.SystemPrompt, Store.Settings.Temperature, Store.Settings.MaxTokens,
                    Mcp.ToolDefinitions(), ct))
                {
                    ct.ThrowIfCancellationRequested();
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

                roundContent = contentBuf.ToString();
                roundReason = reasonBuf.ToString();

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
                    convo.Messages.Add(final);
                    convo.Save();

                    int idx = convo.Messages.Count - 1;
                    double elapsed = Math.Round(sw.Elapsed.TotalSeconds, 1);
                    RunJs($"window.api.streamEnd({J(final.Content)}, {J(final.Reasoning)}, {J(model)}, {idx}, {elapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)})");

                    // auto-titre apres le premier echange (meme s'il y a eu des outils)
                    bool firstExchange = !convo.TitleGenerated && !convo.TitleLocked
                        && convo.Messages.Count(m => m.Role == "user") == 1;
                    if (firstExchange) _ = GenerateTitleAsync(convo);
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
                convo.Messages.Add(callMsg);
                convo.Save();

                RunJs("window.api.cancelStream()");
                PushMessages();

                foreach (var tc in toolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    RunJs($"window.api.setStatus({J(string.Format(Loc.S("tool"), tc.Name))})");

                    string result;
                    var answer = AskToolApproval(tc);
                    if (answer == ToolConfirmForm.Answer.Deny)
                    {
                        result = Loc.S("toolDenied");
                    }
                    else
                    {
                        try { result = await Mcp.CallToolAsync(tc.Name, tc.Arguments, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception tex) { result = Loc.S("toolError") + tex.Message; }
                    }

                    convo.Messages.Add(new ChatMessage
                    {
                        Role = "tool",
                        ToolCallId = tc.Id,
                        ToolName = tc.Name,
                        Content = result
                    });
                }
                convo.Save();
                PushMessages();
            }
        }
        catch (OperationCanceledException)
        {
            RunJs("window.api.cancelStream()");
            SavePartial(convo, model, roundContent, roundReason, sw.ElapsedMilliseconds);
            PushMessages();
        }
        catch (LlmException ex) when (ex.StatusCode == 404)
        {
            // modele supprime du catalogue : on le bannit definitivement et on bascule
            SavePartial(convo, model, roundContent, roundReason, sw.ElapsedMilliseconds);
            MarkModelDead(model);
            PushMessages();
            RunJs($"window.api.setModels({J(ModelList())}, {J(CurrentModel())})");
            RunJs("window.api.cancelStream()");
            RunJs($"window.api.showError({J(Loc.S("err404"))}, true)");
            _ = RefreshModelsAsync(force: true);   // recupere la liste a jour tout de suite
        }
        catch (Exception ex)
        {
            RunJs("window.api.cancelStream()");
            SavePartial(convo, model, roundContent, roundReason, sw.ElapsedMilliseconds);
            PushMessages();
            RunJs($"window.api.showError({J(FriendlyError(ex))}, true)");
        }
        finally
        {
            _busy = false;
            _cts?.Dispose(); _cts = null;
            RunJs("window.api.setBusy(false)");
            PushConversations();
            PushStatus();
        }
    }

    static string FriendlyError(Exception ex)
    {
        if (ex is LlmException le)
        {
            if (le.StatusCode == 401 || le.StatusCode == 403) return Loc.S("errAuth");
            if (le.StatusCode == 429) return Loc.S("errRate");
            if (le.StatusCode >= 500) return Loc.S("errServer");
        }
        if (ex is HttpRequestException or TaskCanceledException or IOException)
            return Loc.S("errNetwork");
        return ex.Message;
    }

    void RetryLast()
    {
        if (_busy || _convo == null) return;
        var msgs = _convo.Messages;
        // retire la reponse partielle / les resultats d'outils apres le dernier message utilisateur
        while (msgs.Count > 0 && msgs[^1].Role != "user") msgs.RemoveAt(msgs.Count - 1);
        if (msgs.Count == 0 || msgs[^1].Role != "user") return;
        _convo.Save();
        PushMessages();
        PushConversations();
        _ = GenerateReplyAsync();
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
        if (index == 0) _convo.TitleGenerated = false;   // le premier message a change : nouveau titre possible
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
        c.TitleGenerated = false;   // un nouveau premier message pourra re-titrer
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
            DateTime.Now.ToString(Loc.S("dateFmt"), Loc.Culture), c.Model ?? CurrentModel()));
        sb.AppendLine();
        foreach (var m in c.Messages)
        {
            sb.AppendLine(m.Role == "user"
                ? "## " + Loc.S("exportUser")
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

    // ------------------------------------------------ dictee vocale : enregistrer puis transcrire
    // (le flux bloquant SAPI se bloque : on enregistre via WASAPI le micro choisi,
    //  on ecrit un WAV temporaire puis SAPI transcrit le fichier — teste et valide)

    NAudio.CoreAudioApi.WasapiCapture? _capture;
    readonly object _dictLock = new();
    List<float> _dictMono = new();
    bool _speechDetected;
    bool _dictating;
    bool _dictationCancelled;
    double _minRms = 1.0;
    long _dictStartTick, _lastSpeechTick;

    async Task StartDictationAsync()
    {
        if (_dictating) return;
        _dictating = true;
        _dictationCancelled = false;
        var wavPath = "";
        try
        {
            RunJs("window.api.micState(true)");
            RunJs($"window.api.setStatus({J(Loc.S("listen"))})");

            wavPath = await RecordDictationAsync();

            if (_dictationCancelled) { PushStatus(); return; }
            if (!_speechDetected) { RunJs($"window.api.setStatus({J(Loc.S("sttEmpty"))})"); return; }

            RunJs($"window.api.setStatus({J(Loc.S("sttProc"))})");
            var text = await Task.Run(() => TranscribeWav(wavPath));
            wavPath = "";

            if (_dictationCancelled) { PushStatus(); return; }
            if (!string.IsNullOrWhiteSpace(text))
                RunJs($"window.api.insertDictation({J(text.Trim())})");
            else
                RunJs($"window.api.setStatus({J(Loc.S("sttEmpty"))})");
        }
        catch (Exception ex)
        {
            if (!_dictationCancelled)
                RunJs($"window.api.setStatus({J(Loc.S("sttErr") + ex.Message)})");
        }
        finally
        {
            if (wavPath.Length > 0) try { File.Delete(wavPath); } catch { }
            RunJs("window.api.micState(false)");
            _dictating = false;
        }
    }

    async Task<string> RecordDictationAsync()
    {
        // peripherique choisi dans les Parametres (sinon micro par defaut)
        NAudio.CoreAudioApi.MMDevice? dev = null;
        if (!string.IsNullOrWhiteSpace(Store.Settings.MicDeviceId))
        {
            try { dev = new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDevice(Store.Settings.MicDeviceId); }
            catch { }
        }
        var capture = dev != null
            ? new NAudio.CoreAudioApi.WasapiCapture(dev)
            : new NAudio.CoreAudioApi.WasapiCapture();
        _capture = capture;

        lock (_dictLock)
        {
            _dictMono = new List<float>();
            _speechDetected = false;
            _minRms = 1.0;
        }
        _dictStartTick = Environment.TickCount64;
        _lastSpeechTick = _dictStartTick;

        var fmt = capture.WaveFormat;
        int ch = Math.Max(1, fmt.Channels);
        bool isFloat = fmt.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat;
        int bpf = Math.Max(1, (fmt.BitsPerSample / 8) * ch);

        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.DataAvailable += (s, e) =>
        {
            try
            {
                if (e.BytesRecorded == 0) return;
                int frames = e.BytesRecorded / bpf;
                if (frames == 0) return;
                double sumSq = 0;
                lock (_dictLock)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        double acc = 0;
                        for (int c = 0; c < ch; c++)
                            acc += isFloat
                                ? BitConverter.ToSingle(e.Buffer, i * bpf + c * 4)
                                : (fmt.BitsPerSample == 16
                                    ? BitConverter.ToInt16(e.Buffer, i * bpf + c * 2) / 32768.0
                                    : 0.0);
                        var v = acc / ch;
                        _dictMono.Add((float)v);
                        sumSq += v * v;
                    }
                }
                double rms = Math.Sqrt(sumSq / frames);

                long now = Environment.TickCount64;
                lock (_dictLock)
                {
                    _minRms = Math.Min(_minRms, rms);
                    double threshold = Math.Max(0.012, _minRms * 3.0);
                    if (rms > threshold)
                    {
                        _speechDetected = true;
                        _lastSpeechTick = now;
                    }
                }

                bool spoke;
                lock (_dictLock) spoke = _speechDetected;
                // auto-stop : 1,6 s de silence apres la parole / 9 s sans parole / cap 45 s
                if ((spoke && now - _lastSpeechTick > 1600) || (!spoke && now - _dictStartTick > 9000)
                    || now - _dictStartTick > 45000)
                {
                    try { capture.StopRecording(); } catch { }
                }
            }
            catch { }
        };
        capture.RecordingStopped += (s, e) => stopped.TrySetResult(true);

        capture.StartRecording();
        await stopped.Task;

        try { capture.Dispose(); } catch { }
        _capture = null;

        // WAV 16 kHz mono 16 bits (resampling lineaire)
        List<float> mono;
        bool speech;
        lock (_dictLock) { mono = _dictMono; speech = _speechDetected; }
        _speechDetected = speech;

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NvidiaBuildApp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "dictation.wav");
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            double step = fmt.SampleRate / 16000.0;
            var pcm = new List<byte>(mono.Count * 2 / 3 + 44);
            double pos = 0;
            while (pos + 1 < mono.Count)
            {
                int i0 = (int)Math.Floor(pos);
                double frac = pos - i0;
                float v = mono[i0] + (mono[i0 + 1] - mono[i0]) * (float)frac;
                short s16 = (short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue);
                pcm.Add((byte)(s16 & 0xFF));
                pcm.Add((byte)((s16 >> 8) & 0xFF));
                pos += step;
            }
            byte[] a = System.Text.Encoding.ASCII.GetBytes("RIFF");
            byte[] b = System.Text.Encoding.ASCII.GetBytes("WAVE");
            byte[] f = System.Text.Encoding.ASCII.GetBytes("fmt ");
            byte[] d = System.Text.Encoding.ASCII.GetBytes("data");
            w.Write(a); w.Write(36 + pcm.Count); w.Write(b);
            w.Write(f); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write(d); w.Write(pcm.Count); w.Write(pcm.ToArray());
        }
        return path;
    }

    string? TranscribeWav(string path)
    {
        try
        {
            var recs = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers();
            if (recs.Count == 0) throw new InvalidOperationException(Loc.S("sttLangMissing"));

            var tag = Loc.L switch
            {
                "fr" => "fr-FR", "en" => "en-US", "es" => "es-ES",
                "de" => "de-DE", "it" => "it-IT", _ => "pt-BR",
            };
            var ri = recs.FirstOrDefault(r => string.Equals(r.Culture.Name, tag, StringComparison.OrdinalIgnoreCase))
                     ?? recs[0];

            using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(ri);
            engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
            engine.SetInputToWaveFile(path);
            var result = engine.Recognize(TimeSpan.FromSeconds(60));
            return result?.Text;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }


    static List<object> EnumerateMics()
    {
        var list = new List<object>();
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                list.Add(new { id = d.ID, name = d.FriendlyName });
        }
        catch { }
        return list;
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
