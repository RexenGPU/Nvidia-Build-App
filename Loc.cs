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

    /// <summary>Culture .NET correspondante (formats de date localises coherents).</summary>
    public static System.Globalization.CultureInfo Culture => L switch
    {
        "fr" => System.Globalization.CultureInfo.GetCultureInfo("fr-FR"),
        "es" => System.Globalization.CultureInfo.GetCultureInfo("es-ES"),
        "de" => System.Globalization.CultureInfo.GetCultureInfo("de-DE"),
        "it" => System.Globalization.CultureInfo.GetCultureInfo("it-IT"),
        "pt" => System.Globalization.CultureInfo.GetCultureInfo("pt-BR"),
        _ => System.Globalization.CultureInfo.GetCultureInfo("en-US"),
    };

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
        ["modelsRefreshed"] = new()
        {
            ["fr"] = "Liste des modèles actualisée ({0} modèles)",
            ["en"] = "Model list updated ({0} models)",
            ["es"] = "Lista de modelos actualizada ({0} modelos)",
            ["de"] = "Modellliste aktualisiert ({0} Modelle)",
            ["it"] = "Elenco dei modelli aggiornato ({0} modelli)",
            ["pt"] = "Lista de modelos atualizada ({0} modelos)",
        },
        ["modelsRefreshFailed"] = new()
        {
            ["fr"] = "Impossible d'actualiser la liste des modèles (réseau ou clé API).",
            ["en"] = "Could not refresh the model list (network or API key).",
            ["es"] = "No se pudo actualizar la lista de modelos (red o clave API).",
            ["de"] = "Modellliste konnte nicht aktualisiert werden (Netzwerk oder API-Schlüssel).",
            ["it"] = "Impossibile aggiornare l'elenco dei modelli (rete o chiave API).",
            ["pt"] = "Não foi possível atualizar a lista de modelos (rede ou chave de API).",
        },
        ["modelsNoKey"] = new()
        {
            ["fr"] = "Ajoutez votre clé API pour actualiser la liste des modèles.",
            ["en"] = "Add your API key to refresh the model list.",
            ["es"] = "Añade tu clave API para actualizar la lista de modelos.",
            ["de"] = "Füge deinen API-Schlüssel hinzu, um die Modellliste zu aktualisieren.",
            ["it"] = "Aggiungi la chiave API per aggiornare l'elenco dei modelli.",
            ["pt"] = "Adicione sua chave de API para atualizar a lista de modelos.",
        },
        ["listen"] = new()
        {
            ["fr"] = "Écoute…", ["en"] = "Listening…", ["es"] = "Escuchando…",
            ["de"] = "Aufnahme…", ["it"] = "In ascolto…", ["pt"] = "Ouvindo…",
        },
        ["micDenied"] = new()
        {
            ["fr"] = "Micro indisponible — vérifiez les autorisations (Paramètres Windows > Confidentialité > Micro)",
            ["en"] = "Microphone unavailable — check permissions (Windows Settings > Privacy > Microphone)",
            ["es"] = "Micrófono no disponible - comprueba los permisos (Configuración de Windows > Privacidad > Micrófono)",
            ["de"] = "Mikrofon nicht verfügbar - Berechtigungen prüfen (Windows-Einstellungen > Datenschutz > Mikrofon)",
            ["it"] = "Microfono non disponibile - controlla le autorizzazioni (Impostazioni Windows > Privacy > Microfono)",
            ["pt"] = "Microfone indisponível - verifique as permissões (Configurações do Windows > Privacidade > Microfone)",
        },
        ["sttEmpty"] = new()
        {
            ["fr"] = "Aucune parole détectée", ["en"] = "No speech detected", ["es"] = "No se detectó voz",
            ["de"] = "Keine Sprache erkannt", ["it"] = "Nessuna voce rilevata", ["pt"] = "Nenhuma fala detectada",
        },
        ["sttProc"] = new()
        {
            ["fr"] = "Transcription…", ["en"] = "Transcribing…", ["es"] = "Transcribiendo…",
            ["de"] = "Transkribierung…", ["it"] = "Trascrizione…", ["pt"] = "Transcrevendo…",
        },
        ["sttErr"] = new()
        {
            ["fr"] = "Échec de la reconnaissance vocale : ", ["en"] = "Speech recognition failed: ",
            ["es"] = "Fallo de reconocimiento de voz: ", ["de"] = "Spracherkennung fehlgeschlagen: ",
            ["it"] = "Riconoscimento vocale non riuscito: ", ["pt"] = "Falha no reconhecimento de fala: ",
        },
        ["sttLangMissing"] = new()
        {
            ["fr"] = "Aucun moteur de reconnaissance vocale installé sur Windows — installez-en un dans Paramètres > Heure et langue > Voix",
            ["en"] = "No speech recognition engine installed on Windows — add one in Settings > Time and language > Speech",
            ["es"] = "No hay ningún motor de reconocimiento de voz instalado en Windows - instala uno en Configuración > Hora e idioma > Voz",
            ["de"] = "Keine Spracherkennung unter Windows installiert - füge eine unter Einstellungen > Zeit und Sprache > Sprache hinzu",
            ["it"] = "Nessun motore di riconoscimento vocale installato su Windows - aggiungine uno in Impostazioni > Ora e lingua > Voce",
            ["pt"] = "Nenhum motor de reconhecimento de fala instalado no Windows - adicione um em Configurações > Hora e idioma > Fala",
        },
        ["errNoVision"] = new()
        {
            ["fr"] = "Ce modèle ne gère pas les images. Choisissez un modèle vision (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) avec /model.",
            ["en"] = "This model does not support images. Pick a vision model (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) with /model.",
            ["es"] = "Este modelo no admite imágenes. Elige un modelo de visión (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) con /model.",
            ["de"] = "Dieses Modell unterstützt keine Bilder. Wähle ein Vision-Modell (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) mit /model.",
            ["it"] = "Questo modello non supporta le immagini. Scegli un modello vision (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) con /model.",
            ["pt"] = "Este modelo não aceita imagens. Escolha um modelo de visão (Kimi K3, Muse Glimmer, Nemotron Omni, DeepSeek V4.1 Flash, GLM-5.3 Flash) com /model.",
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
        ["alreadyRunning"] = new()
        {
            ["fr"] = "NVIDIA Build App est déjà ouverte.", ["en"] = "NVIDIA Build App is already running.",
            ["es"] = "NVIDIA Build App ya está abierta.", ["de"] = "NVIDIA Build App läuft bereits.",
            ["it"] = "NVIDIA Build App è già aperta.", ["pt"] = "O NVIDIA Build App já está aberto.",
        },
        ["busyStop"] = new()
        {
            ["fr"] = "Arrêtez d'abord la génération.", ["en"] = "Stop the current generation first.",
            ["es"] = "Detén primero la generación.", ["de"] = "Stoppe zuerst die Generierung.",
            ["it"] = "Ferma prima la generazione.", ["pt"] = "Pare primeiro a geração.",
        },
        ["exportUser"] = new()
        {
            ["fr"] = "Vous", ["en"] = "You", ["es"] = "Tú", ["de"] = "Du", ["it"] = "Tu", ["pt"] = "Você",
        },
        ["newConvoTitle"] = new()
        {
            ["fr"] = "Nouvelle conversation", ["en"] = "New conversation", ["es"] = "Nueva conversación",
            ["de"] = "Neues Gespräch", ["it"] = "Nuova conversazione", ["pt"] = "Nova conversa",
        },
        ["imagesTitle"] = new()
        {
            ["fr"] = "Images", ["en"] = "Images", ["es"] = "Imágenes", ["de"] = "Bilder",
            ["it"] = "Immagini", ["pt"] = "Imagens",
        },
        ["toolAskTitle"] = new()
        {
            ["fr"] = "Autoriser l'outil ?", ["en"] = "Allow tool?", ["es"] = "¿Permitir la herramienta?",
            ["de"] = "Werkzeug erlauben?", ["it"] = "Consentire lo strumento?", ["pt"] = "Permitir a ferramenta?",
        },
        ["toolAskBody"] = new()
        {
            ["fr"] = "L'assistant veut exécuter l'outil « {0} » avec les arguments ci-dessous.",
            ["en"] = "The assistant wants to run the tool \"{0}\" with the arguments below.",
            ["es"] = "El asistente quiere ejecutar la herramienta «{0}» con los argumentos siguientes.",
            ["de"] = "Der Assistent möchte das Werkzeug „{0}“ mit den folgenden Argumenten ausführen.",
            ["it"] = "L'assistente vuole eseguire lo strumento «{0}» con gli argomenti seguenti.",
            ["pt"] = "O assistente quer executar a ferramenta «{0}» com os argumentos abaixo.",
        },
        ["toolAskHint"] = new()
        {
            ["fr"] = "« Toujours autoriser » ne vaut que pour cette session.",
            ["en"] = "\"Always allow\" applies to this session only.",
            ["es"] = "«Permitir siempre» solo se aplica a esta sesión.",
            ["de"] = "„Immer erlauben“ gilt nur für diese Sitzung.",
            ["it"] = "«Consenti sempre» vale solo per questa sessione.",
            ["pt"] = "«Permitir sempre» vale apenas para esta sessão.",
        },
        ["toolAllow"] = new()
        {
            ["fr"] = "Autoriser", ["en"] = "Allow", ["es"] = "Permitir", ["de"] = "Erlauben",
            ["it"] = "Consenti", ["pt"] = "Permitir",
        },
        ["toolAlways"] = new()
        {
            ["fr"] = "Toujours autoriser", ["en"] = "Always allow", ["es"] = "Permitir siempre",
            ["de"] = "Immer erlauben", ["it"] = "Consenti sempre", ["pt"] = "Permitir sempre",
        },
        ["toolDeny"] = new()
        {
            ["fr"] = "Refuser", ["en"] = "Deny", ["es"] = "Denegar", ["de"] = "Ablehnen",
            ["it"] = "Rifiuta", ["pt"] = "Recusar",
        },
        ["toolDenied"] = new()
        {
            ["fr"] = "L'utilisateur a refusé l'exécution de cet outil.",
            ["en"] = "The user denied this tool call.",
            ["es"] = "El usuario denegó la ejecución de esta herramienta.",
            ["de"] = "Der Benutzer hat die Ausführung dieses Werkzeugs abgelehnt.",
            ["it"] = "L'utente ha rifiutato l'esecuzione di questo strumento.",
            ["pt"] = "O usuário recusou a execução desta ferramenta.",
        },
        ["toolError"] = new()
        {
            ["fr"] = "Erreur de l'outil : ", ["en"] = "Tool error: ", ["es"] = "Error de la herramienta: ",
            ["de"] = "Werkzeugfehler: ", ["it"] = "Errore dello strumento: ", ["pt"] = "Erro da ferramenta: ",
        },
        ["errAuth"] = new()
        {
            ["fr"] = "Clé API invalide ou non autorisée.", ["en"] = "Invalid or unauthorized API key.",
            ["es"] = "Clave API inválida o no autorizada.", ["de"] = "Ungültiger oder nicht autorisierter API-Schlüssel.",
            ["it"] = "Chiave API non valida o non autorizzata.", ["pt"] = "Chave de API inválida ou não autorizada.",
        },
        ["errRate"] = new()
        {
            ["fr"] = "Limite de requêtes atteinte. Réessayez dans un instant.",
            ["en"] = "Rate limit reached. Try again shortly.",
            ["es"] = "Límite de solicitudes alcanzado. Inténtalo de nuevo en un momento.",
            ["de"] = "Anfragelimit erreicht. Versuche es gleich noch einmal.",
            ["it"] = "Limite di richieste raggiunto. Riprova tra poco.",
            ["pt"] = "Limite de solicitações atingido. Tente novamente em instantes.",
        },
        ["errServer"] = new()
        {
            ["fr"] = "Erreur côté serveur NVIDIA.", ["en"] = "NVIDIA server error.",
            ["es"] = "Error del servidor de NVIDIA.", ["de"] = "Fehler auf dem NVIDIA-Server.",
            ["it"] = "Errore del server NVIDIA.", ["pt"] = "Erro no servidor da NVIDIA.",
        },
        ["errNetwork"] = new()
        {
            ["fr"] = "Connexion impossible.", ["en"] = "Connection failed.",
            ["es"] = "No se pudo conectar.", ["de"] = "Verbindung fehlgeschlagen.",
            ["it"] = "Connessione non riuscita.", ["pt"] = "Falha na conexão.",
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
