namespace NvidiaBuildApp;

/// <summary>Localisation cote host : statuts, messages, dates, prompt systeme par defaut. Fallback : anglais.</summary>
internal static class Loc
{
    public static readonly string[] Languages = { "fr", "en", "es", "de", "it", "pt" };

    public static string L
    {
        get
        {
            var l = Store.Settings.Lang;
            return Languages.Contains(l) ? l : "en";
        }
    }

    public static string S(string key)
    {
        if (Dict.TryGetValue(key, out var row) && row.TryGetValue(L, out var v) && !string.IsNullOrEmpty(v))
            return v;
        return Dict.TryGetValue(key, out var en) ? en["en"] : key;
    }

    public static string Sys(string lang) =>
        Dict.TryGetValue("sysPrompt", out var row) && row.TryGetValue(Languages.Contains(lang) ? lang : "en", out var v)
            ? v : row?["en"] ?? "";

    static readonly Dictionary<string, Dictionary<string, string>> Dict = new()
    {
        ["busy"] = new()
        {
            ["fr"] = "Génération en cours…",
            ["en"] = "Generating…",
            ["es"] = "Generando…",
            ["de"] = "Wird generiert…",
            ["it"] = "Generazione in corso…",
            ["pt"] = "Gerando…",
        },
        ["ready"] = new()
        {
            ["fr"] = "Prêt", ["en"] = "Ready", ["es"] = "Listo", ["de"] = "Bereit", ["it"] = "Pronto", ["pt"] = "Pronto",
        },
        ["nokey"] = new()
        {
            ["fr"] = "Clé API requise — ouvrez les Paramètres",
            ["en"] = "API key required — open Settings",
            ["es"] = "Clave API requerida — abre los Ajustes",
            ["de"] = "API-Schlüssel erforderlich — Einstellungen öffnen",
            ["it"] = "Chiave API richiesta — apri le Impostazioni",
            ["pt"] = "Chave de API necessária — abra as Configurações",
        },
        ["tool"] = new()
        {
            ["fr"] = "Outil : {0}…", ["en"] = "Tool: {0}…", ["es"] = "Herramienta: {0}…",
            ["de"] = "Werkzeug: {0}…", ["it"] = "Strumento: {0}…", ["pt"] = "Ferramenta: {0}…",
        },
        ["err404"] = new()
        {
            ["fr"] = "Modèle introuvable sur l'API (404). Ce modèle n'est probablement pas exposé par l'API hébergée NVIDIA — choisissez-en un autre avec /model ou la pastille en bas (la liste est actualisée automatiquement depuis l'API).",
            ["en"] = "Model not found on the API (404). This model is probably not exposed by the NVIDIA hosted API — pick another one with /model or the pill at the bottom (the list refreshes automatically from the API).",
            ["es"] = "Modelo no encontrado en la API (404). Probablemente no está expuesto por la API alojada de NVIDIA — elige otro con /model o la pastilla de abajo (la lista se actualiza automáticamente).",
            ["de"] = "Modell auf der API nicht gefunden (404). Dieses Modell ist wahrscheinlich nicht über die NVIDIA-Hosted-API verfügbar — wähle ein anderes mit /model oder der Pille unten (die Liste aktualisiert sich automatisch).",
            ["it"] = "Modello non trovato sull'API (404). Probabilmente non è esposto dall'API hosted NVIDIA — scegline un altro con /model o la pastiglia in basso (l'elenco si aggiorna automaticamente).",
            ["pt"] = "Modelo não encontrado na API (404). Este modelo provavelmente não está exposto pela API hospedada da NVIDIA — escolha outro com /model ou a pílula embaixo (a lista é atualizada automaticamente).",
        },
        ["emptyReply"] = new()
        {
            ["fr"] = "(réponse vide)", ["en"] = "(empty reply)", ["es"] = "(respuesta vacía)",
            ["de"] = "(leere Antwort)", ["it"] = "(risposta vuota)", ["pt"] = "(resposta vazia)",
        },
        ["errNoVision"] = new()
        {
            ["fr"] = "Ce modèle ne gère pas les images. Choisissez un modèle vision (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) avec /model.",
            ["en"] = "This model does not support images. Pick a vision model (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) with /model.",
            ["es"] = "Este modelo no admite imágenes. Elige un modelo de visión (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) con /model.",
            ["de"] = "Dieses Modell unterstützt keine Bilder. Wähle ein Vision-Modell (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) mit /model.",
            ["it"] = "Questo modello non supporta le immagini. Scegli un modello vision (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) con /model.",
            ["pt"] = "Este modelo não aceita imagens. Escolha um modelo de visão (Llama 3.2 Vision, Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) com /model.",
        },
        ["rounds"] = new()
        {
            ["fr"] = "(Limite d'itérations d'outils atteinte — reformulez votre demande.)",
            ["en"] = "(Tool iteration limit reached — please rephrase.)",
            ["es"] = "(Límite de iteraciones de herramientas alcanzado — reformula tu petición.)",
            ["de"] = "(Grenze der Werkzeug-Iterationen erreicht — bitte formuliere neu.)",
            ["it"] = "(Limite di iterazioni degli strumenti raggiunto — riformula la richiesta.)",
            ["pt"] = "(Limite de iterações de ferramentas atingido — reformule seu pedido.)",
        },
        ["exported"] = new()
        {
            ["fr"] = "Conversation exportée : {0}", ["en"] = "Conversation exported: {0}",
            ["es"] = "Conversación exportada: {0}", ["de"] = "Unterhaltung exportiert: {0}",
            ["it"] = "Conversazione esportata: {0}", ["pt"] = "Conversa exportada: {0}",
        },
        ["exportFail"] = new()
        {
            ["fr"] = "Échec de l'export : ", ["en"] = "Export failed: ", ["es"] = "Fallo al exportar: ",
            ["de"] = "Export fehlgeschlagen: ", ["it"] = "Esportazione non riuscita: ", ["pt"] = "Falha na exportação: ",
        },
        ["exportHeader"] = new()
        {
            ["fr"] = "_Exporté de NVIDIA Build App le {0} · modèle : {1}_",
            ["en"] = "_Exported from NVIDIA Build App on {0} · model: {1}_",
            ["es"] = "_Exportado de NVIDIA Build App el {0} · modelo: {1}_",
            ["de"] = "_Exportiert von NVIDIA Build App am {0} · Modell: {1}_",
            ["it"] = "_Esportato da NVIDIA Build App il {0} · modello: {1}_",
            ["pt"] = "_Exportado do NVIDIA Build App em {0} · modelo: {1}_",
        },
        ["renameTitle"] = new()
        {
            ["fr"] = "Renommer la conversation", ["en"] = "Rename conversation", ["es"] = "Renombrar conversación",
            ["de"] = "Unterhaltung umbenennen", ["it"] = "Rinomina conversazione", ["pt"] = "Renomear conversa",
        },
        ["renameLabel"] = new()
        {
            ["fr"] = "Nouveau nom :", ["en"] = "New name:", ["es"] = "Nuevo nombre:",
            ["de"] = "Neuer Name:", ["it"] = "Nuovo nome:", ["pt"] = "Novo nome:",
        },
        ["ok"] = new()
        {
            ["fr"] = "OK", ["en"] = "OK", ["es"] = "OK", ["de"] = "OK", ["it"] = "OK", ["pt"] = "OK",
        },
        ["cancel"] = new()
        {
            ["fr"] = "Annuler", ["en"] = "Cancel", ["es"] = "Cancelar", ["de"] = "Abbrechen",
            ["it"] = "Annulla", ["pt"] = "Cancelar",
        },
        ["webviewMissing"] = new()
        {
            ["fr"] = "Le runtime WebView2 est requis pour l'interface.\n\nTéléchargez-le ici :\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
            ["en"] = "The WebView2 runtime is required for the interface.\n\nDownload it here:\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
            ["es"] = "Se necesita el runtime WebView2 para la interfaz.\n\nDescárgalo aquí:\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
            ["de"] = "Die WebView2-Runtime wird für die Oberfläche benötigt.\n\nHier herunterladen:\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
            ["it"] = "Il runtime WebView2 è necessario per l'interfaccia.\n\nScaricalo qui:\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
            ["pt"] = "O runtime WebView2 é necessário para a interface.\n\nBaixe aqui:\nhttps://developer.microsoft.com/microsoft-edge/webview2\n\n",
        },
        ["dateFmt"] = new()
        {
            ["fr"] = "dd/MM/yyyy HH:mm", ["en"] = "MMM d, yyyy HH:mm", ["es"] = "dd/MM/yyyy HH:mm",
            ["de"] = "dd.MM.yyyy HH:mm", ["it"] = "dd/MM/yyyy HH:mm", ["pt"] = "dd/MM/yyyy HH:mm",
        },
        ["sysPrompt"] = new()
        {
            ["fr"] = Nvidia.SystemPrompt,
            ["en"] = Nvidia.SystemPromptEn,
            ["es"] = "Eres NVIDIA Build App, un asistente de IA útil, conciso y amable. Responde en español salvo que el usuario pida otro idioma. Usa bloques de código markdown cuando sea relevante.",
            ["de"] = "Du bist NVIDIA Build App, ein hilfreicher, prägnanter und freundlicher KI-Assistent. Antworte auf Deutsch, außer der Nutzer wünscht eine andere Sprache. Nutze Markdown-Codeblöcke, wenn sinnvoll.",
            ["it"] = "Sei NVIDIA Build App, un assistente IA utile, conciso e cordiale. Rispondi in italiano salvo diversa richiesta. Usa blocchi di codice markdown quando utile.",
            ["pt"] = "Você é o NVIDIA Build App, um assistente de IA útil, conciso e amigável. Responda em português salvo se o usuário pedir outro idioma. Use blocos de código markdown quando pertinente.",
        },
    };
}
