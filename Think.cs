using System.Text;
using System.Text.RegularExpressions;

namespace NvidiaBuildApp;

/// <summary>Gestion des balises de raisonnement emises par les modeles "thinking".</summary>
internal static class Think
{
    // \u003C = '<'  (evite les sequences d'entites dans le fichier)
    static readonly Regex Closed =
        new("\u003Cthink\u003E(.*?)\u003C/think\u003E", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Open =
        new("\u003Cthink\u003E", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Supprime les blocs de raisonnement (fermes ou non) — affichage pendant le streaming.</summary>
    public static string Strip(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Closed.Replace(s, "");
        var m = Open.Match(s);
        if (m.Success) s = s[..m.Index];
        return s;
    }

    /// <summary>Extrait le raisonnement et le contenu propre d'une reponse finale.</summary>
    public static (string reasoning, string content) Split(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("", "");
        var sb = new StringBuilder();
        foreach (Match m in Closed.Matches(raw))
            sb.AppendLine(m.Groups[1].Value);
        var content = Closed.Replace(raw, "");
        var open = Open.Match(content);
        if (open.Success)
        {
            sb.Append(content[(open.Index + open.Length)..]);
            content = content[..open.Index];
        }
        return (sb.ToString().Trim(), content.Trim());
    }
}
