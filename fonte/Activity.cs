// "Jogando ..." (jogo aberto agora) e prévia de links, pro Concorde.exe
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

static class Activity
{
    public class Game { public string Name, Store; public long Since; }

    // pastas onde as lojas instalam os jogos: qualquer programa rodando daqui é um jogo
    static readonly string[][] Stores = {
        new[] { @"\steamapps\common\", "steam" },
        new[] { @"\Epic Games\", "epic" },
        new[] { @"\Riot Games\", "riot" },
        new[] { @"\XboxGames\", "xbox" },
        new[] { @"\GOG Galaxy\Games\", "gog" },
        new[] { @"\EA Games\", "ea" },
        new[] { @"\Ubisoft Game Launcher\games\", "ubisoft" },
        new[] { @"\Rockstar Games\", "rockstar" },
        new[] { @"\Battle.net\", "battlenet" },
    };
    // programas que ficam nessas pastas mas não são o jogo em si
    static readonly Regex NotGame = new Regex(@"(launcher|crashhandler|crashreport|unitycrash|helper|updater|setup|install|redist|vc_redist|dxsetup|easyanticheat|battleye|beservice|riotclient|vanguard|overlay|webhelper|steamservice|epicgames|socialclub|uplay|upc\.exe|bootstrap)", RegexOptions.IgnoreCase);

    static readonly Regex NotGameFolder = new Regex(@"^(wallpaper_engine|Steamworks Shared|SteamVR|Steam Controller Configs|Soundpad|OBS Studio|Lossless Scaling|Spacewar|Desktop Mate|Blender|Aseprite|Medal|FPS Monitor|MSI Afterburner)$", RegexOptions.IgnoreCase);
    static Game cached; static DateTime cachedAt;
    public static Game CurrentGame()
    {
        if ((DateTime.Now - cachedAt).TotalSeconds < 10) return cached;
        cachedAt = DateTime.Now;
        Game best = null;
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT ExecutablePath, CreationDate FROM Win32_Process"))
            foreach (ManagementObject p in q.Get())
            {
                var exe = p["ExecutablePath"] as string;
                if (exe == null || NotGame.IsMatch(Path.GetFileName(exe))) continue;
                foreach (var s in Stores)
                {
                    int i = exe.IndexOf(s[0], StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    // nome = pasta do jogo logo depois da pasta da loja ("...\steamapps\common\Soulmask\..." -> Soulmask)
                    string rest = exe.Substring(i + s[0].Length);
                    int slash = rest.IndexOf('\\');
                    string name = slash > 0 ? rest.Substring(0, slash) : Path.GetFileNameWithoutExtension(exe);
                    if (NotGameFolder.IsMatch(name)) break; // programas da loja que não são jogo (papel de parede, VR...)
                    long since = 0;
                    try { since = (long)(ManagementDateTimeConverter.ToDateTime((string)p["CreationDate"]).ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds; } catch { }
                    if (best == null || since < best.Since) best = new Game { Name = name, Store = s[1], Since = since };
                    break;
                }
            }
        }
        catch { }
        cached = best;
        return best;
    }

    // ---------- busca de jogos na Steam ----------
    public static string SearchGames(string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 60) return "{\"items\":[]}";
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create("https://store.steampowered.com/api/storesearch/?l=brazilian&cc=BR&term=" + Uri.EscapeDataString(q.Trim()));
            req.Timeout = 6000; req.ReadWriteTimeout = 6000; req.UserAgent = "Concorde";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = s.ReadToEnd();
                // só o que importa: número, nome e miniatura de cada jogo
                var sb = new StringBuilder("{\"items\":[");
                bool first = true;
                // cada jogo começa em {"type":"app"; lê nome, número e miniatura dentro de cada pedaço
                var starts = Regex.Matches(json, "\\{\"type\":\"(app|game)\"");
                for (int k = 0; k < starts.Count; k++)
                {
                    int a = starts[k].Index, b = k + 1 < starts.Count ? starts[k + 1].Index : json.Length;
                    string part = json.Substring(a, b - a);
                    var nm = Regex.Match(part, "\"name\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                    var id = Regex.Match(part, "\"id\":(\\d+)");
                    var img = Regex.Match(part, "\"tiny_image\":\"([^\"]*)\"");
                    if (!nm.Success || !id.Success) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":" + id.Groups[1].Value + ",\"name\":\"" + nm.Groups[1].Value + "\",\"tiny_image\":\"" + (img.Success ? img.Groups[1].Value : "") + "\"}");
                }
                return sb.Append("]}").ToString();
            }
        }
        catch { return "{\"items\":[]}"; }
    }

    // imagem do jogo (os jogos novos da Steam guardam a imagem num endereço com código): pega da própria Steam
    public static string GameImage(string id)
    {
        int n;
        if (!int.TryParse(id, out n) || n <= 0) return "{}";
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create("https://store.steampowered.com/api/appdetails?filters=basic&appids=" + n);
            req.Timeout = 6000; req.ReadWriteTimeout = 6000; req.UserAgent = "Concorde";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var m = Regex.Match(s.ReadToEnd(), "\"header_image\":\"([^\"]+)\"");
                return m.Success ? "{\"img\":\"" + m.Groups[1].Value + "\"}" : "{}";
            }
        }
        catch { return "{}"; }
    }

    // ---------- prévia de link ----------
    static readonly Dictionary<string, string> previewCache = new Dictionary<string, string>();
    public static string Preview(string url)
    {
        Uri u;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return "{}";
        // não deixa olhar dentro da rede de casa (roteador, outros PCs)
        string h = u.Host.ToLowerInvariant();
        if (h == "localhost" || h.EndsWith(".local") || Regex.IsMatch(h, @"^(127\.|10\.|192\.168\.|169\.254\.|172\.(1[6-9]|2\d|3[01])\.|0\.|\[)")) return "{}";
        lock (previewCache) { string hit; if (previewCache.TryGetValue(url, out hit)) return hit; }
        string json = "{}";
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var req = (HttpWebRequest)WebRequest.Create(u);
            req.Timeout = 6000; req.ReadWriteTimeout = 6000;
            req.UserAgent = "Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)"; // os sites mandam o cartão pra esse robô
            req.Accept = "text/html";
            req.AllowAutoRedirect = true; req.MaximumAutomaticRedirections = 4;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            {
                if (resp.ContentType != null && resp.ContentType.StartsWith("image/"))
                    json = "{\"image\":" + Esc(resp.ResponseUri.ToString()) + ",\"site\":" + Esc(resp.ResponseUri.Host) + "}";
                else
                {
                    var ms = new MemoryStream(); var buf = new byte[16384]; int n;
                    while (ms.Length < 600000 && (n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    string html = Encoding.UTF8.GetString(ms.ToArray());
                    string title = Meta(html, "og:title") ?? Meta(html, "twitter:title") ?? Tag(html, "title");
                    string desc = Meta(html, "og:description") ?? Meta(html, "twitter:description") ?? Meta(html, "description");
                    string image = Meta(html, "og:image") ?? Meta(html, "twitter:image");
                    string site = Meta(html, "og:site_name") ?? resp.ResponseUri.Host;
                    string color = Meta(html, "theme-color");
                    if (image != null && image.StartsWith("/")) image = new Uri(resp.ResponseUri, image).ToString();
                    if (title != null || desc != null || image != null)
                        json = "{\"title\":" + Esc(Cut(title, 200)) + ",\"desc\":" + Esc(Cut(desc, 350)) + ",\"image\":" + Esc(image) +
                               ",\"site\":" + Esc(Cut(site, 60)) + ",\"color\":" + Esc(color) + "}";
                }
            }
        }
        catch { }
        lock (previewCache) { if (previewCache.Count > 300) previewCache.Clear(); previewCache[url] = json; }
        return json;
    }
    static string Meta(string html, string key)
    {
        foreach (Match m in Regex.Matches(html, @"<meta\s[^>]*>", RegexOptions.IgnoreCase))
        {
            string t = m.Value;
            var k = Regex.Match(t, @"(?:property|name)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!k.Success || !k.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var v = Regex.Match(t, @"content\s*=\s*(""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
            if (v.Success) { string s = v.Groups[2].Success ? v.Groups[2].Value : v.Groups[3].Value; s = WebUtility.HtmlDecode(s).Trim(); if (s.Length > 0) return s; }
        }
        return null;
    }
    static string Tag(string html, string tag)
    {
        var m = Regex.Match(html, "<" + tag + @"[^>]*>([^<]{1,300})</" + tag + ">", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }
    static string Cut(string s, int n) { return s == null ? null : s.Length > n ? s.Substring(0, n) + "…" : s; }
    static string Esc(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }
}
