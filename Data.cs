using System.Text.Json;
using System.Text.Json.Serialization;

namespace NvidiaBuildApp;

/// <summary>Configuration NVIDIA NIM — API, prompt systeme et derniers modeles chat (build.nvidia.com, 24/09/2026).</summary>
public static class Nvidia
{
    public const string BaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string DefaultModel = "nvidia/nemotron-3-super-120b-a12b";
    public const float Temperature = 0.7f;
    public const int MaxTokens = 8192;

    public const string SystemPrompt =
        "Tu es NVIDIA Build App, un assistant IA utile, concis et amical. " +
        "Reponds en francais sauf si l'utilisateur demande une autre langue. " +
        "Utilise des blocs de code markdown quand c'est pertinent.";

    public const string SystemPromptEn =
        "You are NVIDIA Build App, a helpful, concise and friendly AI assistant. " +
        "Answer in English unless the user asks for another language. " +
        "Use markdown code blocks when relevant.";

    // Modeles VERIFIES sur l'API (GET /models, 24/09/2026) — seuls ceux qui existent reellement
    public static readonly string[] Models =
    {
        "nvidia/nemotron-3-super-120b-a12b",              // equilibre parfait, 1M contexte
        "nvidia/nemotron-3-ultra-550b-a55b",              // fleuron Nemotron 3
        "nvidia/nemotron-3.5-lightning-30b-a3b",          // ultra rapide
        "deepseek-ai/deepseek-v4.1-flash",                // MoE 552B, multimodal
        "z-ai/glm-5.3",                                    // MoE 753B raisonnement + outils
        "z-ai/glm-5.3-flash",                              // MoE 320B multimodal rapide
        "moonshotai/kimi-k3",                              // MoE ~2.8T multimodal
        "moonshotai/kimi-k2.6",                            // Kimi K2.6
        "google/gemma-4-31b-it",                           // dense 31B raisonnement/code
        "mistralai/mistral-nemotron",                      // agentique, code, appels d'outils
        "openai/gpt-oss-20b",                              // MoE compact raisonnement
        "poolside/laguna-xs-2.1",                          // code agentique 33B MoE
        "meta/muse-glimmer-30b",                           // multimodal + tool calling
        "nvidia/nemotron-3-nano-omni-30b-a3b-reasoning",   // omni-modal
    };
}

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string? Reasoning { get; set; }
    public string? Model { get; set; }
    public long? ElapsedMs { get; set; }
    public string? ToolCallId { get; set; }       // role = tool : id de l'appel
    public string? ToolName { get; set; }          // role = tool : nom de l'outil
    public List<ToolCallData>? ToolCalls { get; set; }  // role = assistant : appels demandes
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class ToolCallData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

/// <summary>Configuration d'un serveur MCP (Model Context Protocol).</summary>
public class McpServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Serveur MCP";
    public string Transport { get; set; } = "stdio";   // stdio | http
    public string Command { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Nouvelle conversation";
    public string? Model { get; set; }
    public bool Pinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonIgnore] public string FilePath => Path.Combine(Store.ConversationsDir, Id + ".json");

    public void Save() => Store.SaveConversation(this);

    public void DeleteFile()
    {
        try { File.Delete(FilePath); } catch { }
    }
}

public class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string? LastModel { get; set; }
    public string Lang { get; set; } = "en";
    public bool FirstRun { get; set; } = true;
    public List<string>? LiveModels { get; set; }   // liste reelle recuperee de l'API (GET /models)
    public float Temperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 8192;
    public string SystemPrompt { get; set; } = Nvidia.SystemPromptEn;
    public List<McpServerConfig> McpServers { get; set; } = new();
}

public static class Store
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NvidiaBuildApp");
    public static readonly string ConversationsDir = Path.Combine(Dir, "conversations");
    public static readonly string SettingsPath = Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions JOpt = new() { WriteIndented = true };

    public static AppSettings Settings { get; private set; } = new();

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(ConversationsDir);
        LoadSettings();
    }

    public static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JOpt) ?? new AppSettings();
            else
                Settings = new AppSettings();
        }
        catch { Settings = new AppSettings(); }
    }

    public static void SaveSettings()
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, JOpt)); } catch { }
    }

    public static void SaveConversation(Conversation c)
    {
        try
        {
            c.UpdatedAt = DateTime.Now;
            File.WriteAllText(c.FilePath, JsonSerializer.Serialize(c, JOpt));
        }
        catch { }
    }

    public static List<Conversation> LoadConversations()
    {
        var list = new List<Conversation>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(ConversationsDir, "*.json"))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(f), JOpt);
                    if (c != null) list.Add(c);
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public static void DeleteConversation(Conversation c)
    {
        try { c.DeleteFile(); } catch { }
    }
}
