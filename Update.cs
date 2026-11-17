using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NvidiaBuildApp;

/// <summary>Nouvelle version publiee sur GitHub (tag, page, notes, binaire et empreinte SHA-256).</summary>
public record UpdateInfo(string Version, string Url, string Notes, string? AssetUrl, string? Sha256);

/// <summary>
/// Mise a jour depuis les releases GitHub du depot officiel uniquement :
/// verification de version, telechargement verifie par SHA-256, remplacement de l'exe au redemarrage.
/// </summary>
public static class UpdateService
{
    const string Owner = "RexenGPU";
    const string Repo = "Nvidia-Build-App";
    const string AssetName = "NvidiaBuildApp.exe";
    const long MinAssetSize = 10L * 1024 * 1024;    // un binaire valide fait ~50 Mo
    const long MaxAssetSize = 300L * 1024 * 1024;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // le binaire fait ~53 Mo : un delai court ferait echouer les connexions lentes
    static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };

    public static Version CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0);

    public static string CurrentText => Display(CurrentVersion);

    static string Display(Version v) =>
        v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    /// <summary>L'app tourne depuis son propre .exe (pas via dotnet ou un hote de developpement).</summary>
    public static bool CanSelfUpdate
    {
        get
        {
            var p = Environment.ProcessPath;
            return !string.IsNullOrEmpty(p)
                && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(p), "NvidiaBuildApp",
                    StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Verifie les releases GitHub : tag, page, notes, binaire et empreinte SHA-256 (API ou page HTML).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            return await CheckApiAsync(ct);
        }
        catch
        {
            // quota API epuise, rate-limit ou JSON inattendu : on retombe sur la page des releases
            return await CheckHtmlAsync(ct);
        }
    }

    static async Task<UpdateInfo?> CheckApiAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");
        req.Headers.UserAgent.ParseAdd("NvidiaBuildApp/" + CurrentText);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var latest = ParseTag(tag);
        if (latest == null || latest <= CurrentVersion) return null;

        string? assetUrl = null, sha = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!IsTrustedAssetUrl(url)) continue;
                assetUrl = url;
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    var digest = d.GetString() ?? "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..].Trim().ToLowerInvariant();
                }
                break;
            }
        }

        var html = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        if (notes.Length > 4000) notes = notes[..4000];
        return Build(latest, assetUrl, sha, html, notes);
    }

    static async Task<UpdateInfo?> CheckHtmlAsync(CancellationToken ct)
    {
        // la page /releases.atom liste les tags sans quota ni authentification
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{Owner}/{Repo}/releases.atom");
        req.Headers.UserAgent.ParseAdd("NvidiaBuildApp/" + CurrentText);

        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var xml = await resp.Content.ReadAsStringAsync(ct);
        var match = System.Text.RegularExpressions.Regex
            .Match(xml, @"<entry>.*?</entry>", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success) return null;

        var entry = match.Value;
        // le tag est dans l'URL de la release (l'<id> atom n'en contient qu'un suffixe non fiable)
        var link = System.Text.RegularExpressions.Regex
            .Match(entry, @"<link[^>]+rel=""alternate""[^>]+href=""([^""]+)""").Groups[1].Value;
        if (link.Length == 0)
            link = System.Text.RegularExpressions.Regex.Match(entry, @"<link[^>]+href=""([^""]+)""").Groups[1].Value;

        var tag = System.Text.RegularExpressions.Regex
            .Match(link, @"/releases/tag/([^/?#]+)").Groups[1].Value;
        if (tag.Length == 0)
        {
            var title = System.Text.RegularExpressions.Regex
                .Match(entry, @"<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
            tag = System.Text.RegularExpressions.Regex.Match(title, @"v(\d+(?:\.\d+)+)").Value;
            if (tag.Length > 0) tag = "v" + tag;
        }
        var latest = ParseTag(tag);
        if (latest == null || latest <= CurrentVersion) return null;

        // contenu : le feed echappe le HTML (![CDATA[ absent) puis l'encode a nouveau
        var content = System.Text.RegularExpressions.Regex
            .Match(entry, @"<content[^>]*>(.*?)</content>", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Groups[1].Value;
        content = System.Net.WebUtility.HtmlDecode(content);
        if (content.TrimStart().StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            content = content.Substring(9);
            var end = content.IndexOf("]]>", StringComparison.Ordinal);
            if (end >= 0) content = content[..end];
            content = System.Net.WebUtility.HtmlDecode(content);
        }
        var notes = System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", "")).Trim();

        return Build(latest, null, null, link, notes);
    }

    static UpdateInfo Build(Version latest, string? assetUrl, string? sha, string? html, string notes)
    {
        if (!IsTrustedReleaseUrl(html)) html = $"https://github.com/{Owner}/{Repo}/releases";
        if (notes.Length > 4000) notes = notes[..4000];
        return new UpdateInfo(Display(latest), html!, notes, assetUrl, sha);
    }

    /// <summary>v3.1, 3.1.0, V3.1.0-beta -> Version. Null si le tag n'est pas numerique.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var core = s.Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var nums = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return null;
        return new Version(nums[0], nums[1], nums[2], nums[3]);
    }

    /// <summary>Telecharge l'exe de la release et verifie son empreinte. Retourne le chemin local.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.AssetUrl) || !IsTrustedAssetUrl(info.AssetUrl))
            throw new InvalidOperationException("Adresse de telechargement refusee.");
        // sans empreinte publiee, impossible de garantir l'integrite : telechargement manuel
        if (string.IsNullOrEmpty(info.Sha256))
            throw new InvalidDataException("Empreinte SHA-256 indisponible : telechargement manuel requis.");

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NvidiaBuildApp", "updates");
        Directory.CreateDirectory(dir);

        var dest = Path.Combine(dir, AssetName);
        var tmp = dest + ".part";
        try
        {
            using (var resp = await DownloadHttp.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                // GitHub redirige vers son CDN : on n'accepte que github.com / *.githubusercontent.com
                if (!IsTrustedFinalUri(resp.RequestMessage?.RequestUri))
                    throw new InvalidOperationException("Redirection de telechargement refusee.");
                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, useAsync: true);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total > 0) progress?.Report(Math.Clamp(done * 100.0 / total, 0, 100));
                }
            }

            var len = new FileInfo(tmp).Length;
            if (len < MinAssetSize) throw new InvalidDataException("Fichier telecharge trop petit.");
            if (len > MaxAssetSize) throw new InvalidDataException("Fichier telecharge trop volumineux.");

            if (!string.IsNullOrEmpty(info.Sha256))
            {
                var actual = Sha256Of(tmp);
                if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Empreinte SHA-256 incorrecte.");
            }

            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Lance un petit script PowerShell qui attend la fermeture de l'app,
    /// remplace l'exe par la nouvelle version verifiee puis la redemarre.
    /// </summary>
    public static void ApplyAndRestart(string newExe, string? expectedSha256 = null)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrEmpty(target) || !CanSelfUpdate)
            throw new InvalidOperationException("Mise a jour automatique indisponible dans ce mode de lancement.");
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new InvalidOperationException("Dossier de l'application introuvable.");

        // le fichier telecharge n'a pas pu changer depuis la verification de l'empreinte
        if (!string.IsNullOrEmpty(expectedSha256) &&
            !string.Equals(Sha256Of(newExe), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le fichier telecharge ne correspond plus a l'empreinte attendue.");

        var log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NvidiaBuildApp", "update.log");

        // le dossier doit etre inscriptible (sinon telechargement manuel)
        var probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N")[..6] + ".tmp");
        using (File.Create(probe)) { }
        File.Delete(probe);

        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        var script = Path.Combine(Path.GetTempPath(), "nba_update_" + Guid.NewGuid().ToString("N")[..8] + ".ps1");
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("$src = " + Q(newExe));
        sb.AppendLine("$dst = " + Q(target));
        sb.AppendLine("$app = " + Environment.ProcessId);
        sb.AppendLine("$dir = " + Q(dir));
        sb.AppendLine("$sha = " + Q(expectedSha256 ?? ""));
        // on attend la disparition de notre propre process (ou de tout hote 'dotnet' pendant le developpement)
        sb.AppendLine("while ($true) {");
        sb.AppendLine("  $alive = $false");
        sb.AppendLine("  try { $p = Get-Process -Id $app -ErrorAction Stop; if ($p) { $alive = $true } } catch { }");
        sb.AppendLine("  if ($alive) { Start-Sleep -Milliseconds 400; continue }");
        sb.AppendLine("  $mine = $false");
        sb.AppendLine("  try {");
        sb.AppendLine("    foreach ($q in (Get-Process dotnet -ErrorAction Stop)) {");
        sb.AppendLine("      try { if ((Resolve-Path $q.Path) -eq $dst) { $mine = $true; break } } catch { }");
        sb.AppendLine("    }");
        sb.AppendLine("  } catch { }");
        sb.AppendLine("  if ($mine) { Start-Sleep -Milliseconds 400; continue }");
        sb.AppendLine("  $ok = $false");
        sb.AppendLine("  $want = $sha");
        sb.AppendLine("  if ($want -eq '') { $want = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLower() }");
        sb.AppendLine("  for ($i = 0; $i -lt 120; $i++) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop");
        sb.AppendLine("      if ((Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash.ToLower() -eq $want) { $ok = $true; break }");
        sb.AppendLine("    } catch { }");
        sb.AppendLine("    Start-Sleep -Milliseconds 1000");
        sb.AppendLine("  }");
        sb.AppendLine("  if ($ok) {");
        sb.AppendLine("    Start-Process -FilePath $dst -WorkingDirectory " + Q(dir));
        sb.AppendLine("    Remove-Item -LiteralPath $src -Force");
        sb.AppendLine("  } else {");
        sb.AppendLine("    Add-Content -LiteralPath " + Q(log) + " -Value ((Get-Date -Format s) + ' remplacement impossible : ' + $dst)");
        sb.AppendLine("    Start-Process -FilePath $dst -WorkingDirectory " + Q(dir));
        sb.AppendLine("  }");
        sb.AppendLine("  Remove-Item -LiteralPath $PSCommandPath -Force");
        sb.AppendLine("  break");
        sb.AppendLine("}");
        // BOM : PowerShell 5.1 lit ainsi correctement les chemins Unicode
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));

        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    static bool IsTrustedAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps
            && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTrustedFinalUri(Uri? uri)
    {
        return uri != null
            && uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    static bool IsTrustedReleaseUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.Scheme == Uri.UriSchemeHttps
            && string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith($"/{Owner}/{Repo}/releases", StringComparison.OrdinalIgnoreCase);
    }

    static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }
}
