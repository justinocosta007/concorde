// TelaCall: serve o app em http://localhost e abre numa janela própria do Edge/Chrome.
// Com --startup (quando abre junto com o Windows) a janela já começa minimizada.
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Concorde")]
[assembly: AssemblyProduct("Concorde")]
[assembly: AssemblyDescription("Call com amigos e compartilhar tela")]
[assembly: AssemblyVersion("1.0.0.0")]

static class Program
{
    const int Port = 47821; // porta fixa: mantém amigos e permissões salvos entre aberturas
    static readonly string Url = "http://localhost:" + Port + "/";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Concorde";
    const string AppName = "Concorde";
    static readonly string[] OldRunNames = { "Programa call dos liseiro", "TelaCall" }; // nomes antigos do app
    // os nomes internos (pasta de dados, porta) continuam "TelaCall" pra não perder amigos e mensagens já salvos

    static readonly string DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TelaCall");
    static readonly string CachePath = Path.Combine(DataRoot, "app.html"); // versão nova do app baixada da internet
    static readonly string DataDir = Path.Combine(DataRoot, "Navegador");  // perfil do navegador do app
    // Documentos\Concorde: com o OneDrive ligado vai pra nuvem (formatou o PC, as contas continuam lá)
    static readonly string CloudDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Concorde");
    static string AccountUser(string u) { return u != null && Regex.IsMatch(u, "^[a-z0-9]{3,20}$") ? u : null; }
    // a cópia é desta conta? (olha o usuário dentro do "tc-me", não o dos cofres dos amigos)
    static bool IsAccountOf(string json, string user)
    {
        int i = json.IndexOf("\"tc-me\":\""); if (i < 0) return false;
        int end = json.IndexOf("}\"", i); if (end < 0) end = json.Length;
        return json.IndexOf("\\\"user\\\":\\\"" + user + "\\\"", i, end - i) >= 0;
    }
    // grava num .tmp e troca pelo arquivo de uma vez (nunca fica pela metade). Apagar e renomear falhava quando
    // o Windows (antivírus, indexação) estava com o arquivo aberto: o arquivo antigo ficava e o novo se perdia.
    static void SafeWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        for (int i = 0; ; i++)
        {
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                if (File.ReadAllText(path, Encoding.UTF8).Length != text.Length) throw new IOException("não gravou");
                return;
            }
            catch
            {
                if (i >= 4) { try { File.Copy(tmp, path, true); File.Delete(tmp); return; } catch { throw; } }
                Thread.Sleep(150 * (i + 1));
            }
        }
    }
    // uma cópia por dia em historico\ (guarda 14 dias): se algo der errado, dá pra voltar pra de ontem
    static void DailyCopy(string json, string user)
    {
        try
        {
            string dir = Path.Combine(DataRoot, "historico");
            string f = Path.Combine(dir, (user ?? "conta") + "-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
            if (File.Exists(f) && new FileInfo(f).Length > json.Length) return; // a do dia que tem mais coisa fica
            SafeWrite(f, json);
            foreach (var old in Directory.GetFiles(dir, "*.json"))
                if ((DateTime.Now - File.GetLastWriteTime(old)).TotalDays > 14) File.Delete(old);
        }
        catch { }
    }
    static string Newest(string a, string b)
    {
        bool ea = File.Exists(a), eb = File.Exists(b);
        if (!ea) return eb ? b : null;
        if (!eb) return a;
        return File.GetLastWriteTimeUtc(a) >= File.GetLastWriteTimeUtc(b) ? a : b;
    }
    static volatile byte[] html;
    static volatile int servedVersion;
    static readonly object updLock = new object();

    [STAThread]
    static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--desinstalar") >= 0) { Uninstall(Array.IndexOf(args, "--silencioso") >= 0); return; }
        if (Setup.IsInstaller(args)) { Setup.Run(args); return; } // concorde-setup.exe: este mesmo arquivo instala
        bool startup = Array.IndexOf(args, "--startup") >= 0;
        bool updated = Array.IndexOf(args, "--updated") >= 0;
        bool created;
        using (var mutex = new Mutex(true, "TelaCall-single-instance", out created))
        {
            // acabou de se atualizar: espera a versão velha terminar de fechar
            if (!created && updated)
            {
                try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                if (!startup) MessageBox.Show("O " + AppName + " já está aberto.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { File.Delete(Application.ExecutablePath + ".old"); } catch { }

            // se o .exe mudou de pasta, a inicialização com o Windows passa a apontar pro lugar novo
            if (AutostartEnabled()) SetAutostart(true);

            LoadHtml();
            // toda vez que abre: procura atualização. Se o próprio .exe mudou, troca e abre o novo.
            if (CheckUpdates() == 2)
            {
                mutex.ReleaseMutex();
                var again = new ProcessStartInfo(Application.ExecutablePath, (startup ? "--startup " : "") + "--updated");
                again.UseShellExecute = false;
                Process.Start(again);
                return;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add(Url);
            try { listener.Start(); }
            catch (Exception e)
            {
                MessageBox.Show("Não foi possível iniciar o " + AppName + " (porta " + Port + " em uso).\n\n" + e.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var server = new Thread(() => Serve(listener));
            server.IsBackground = true;
            server.Start();

            string dataDir = DataDir;
            string browser = FindBrowser();
            if (browser == null)
            {
                Process.Start(Url);
                MessageBox.Show("O " + AppName + " abriu no seu navegador.\nFeche esta mensagem só quando quiser encerrar.",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                listener.Stop();
                return;
            }

            var psi = new ProcessStartInfo(browser,
                "--app=" + Url + " --user-data-dir=\"" + dataDir + "\" --no-first-run --no-default-browser-check --window-size=1280,800" +
                " --autoplay-policy=no-user-gesture-required");
            psi.UseShellExecute = false;
            Process.Start(psi);
            var bar = new Thread(HideShareBar); bar.IsBackground = true; bar.Start();

            if (startup) MinimizeWhenReady(dataDir);

            // fica rodando enquanto a janela do app existir
            Thread.Sleep(5000);
            // logo depois de instalar/atualizar, o Edge antigo ainda pode estar fechando e "engolir" a janela nova: abre de novo
            for (int i = 0; i < 2 && !BrowserRunning(dataDir); i++) { Thread.Sleep(2000); if (!BrowserRunning(dataDir)) { Process.Start(psi); Thread.Sleep(5000); } }
            int misses = 0;
            while (misses < 2)
            {
                Thread.Sleep(3000);
                if (BrowserRunning(dataDir)) misses = 0; else misses++;
            }
            listener.Stop();
        }
    }

    // ---------- atualização ----------
    static int ParseVersion(byte[] b)
    {
        var m = Regex.Match(Encoding.UTF8.GetString(b), @"const APP_VERSION = (\d+);");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
    static void LoadHtml()
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TelaCall.html"))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            html = ms.ToArray();
        }
        servedVersion = ParseVersion(html);
        try
        {
            if (File.Exists(CachePath))
            {
                byte[] c = File.ReadAllBytes(CachePath);
                int cv = ParseVersion(c);
                if (cv > servedVersion) { html = c; servedVersion = cv; }
            }
        }
        catch { }
    }
    static byte[] FetchBytes(string url, int timeoutMs)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.Timeout = timeoutMs; req.ReadWriteTimeout = timeoutMs;
        req.UserAgent = "ProgramaCallDosLiseiro";
        req.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var s = resp.GetResponseStream())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
    static int JsonInt(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
    static string JsonStr(string json, string key)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }
    static volatile bool exeReplaced;
    // 0 = nada novo, 1 = app atualizado, 2 = o .exe foi trocado (precisa reabrir)
    static int CheckUpdates()
    {
        if (Config.UpdateUrl == "") return 0;
        lock (updLock)
        {
            try
            {
                string bust = "?t=" + DateTime.UtcNow.Ticks;
                string json = Encoding.UTF8.GetString(FetchBytes(Config.UpdateUrl + "version.json" + bust, 6000));
                int ver = JsonInt(json, "version"), lver = JsonInt(json, "launcher");
                string exeName = JsonStr(json, "exe");
                if (!exeReplaced && lver > Config.LauncherVersion && exeName != null)
                {
                    byte[] exe = FetchBytes(Config.UpdateUrl + Uri.EscapeDataString(exeName) + bust, 60000);
                    if (exe.Length > 50000 && exe[0] == 'M' && exe[1] == 'Z')
                    {
                        // um .exe em uso não pode ser sobrescrito, mas pode ser renomeado
                        string me = Application.ExecutablePath, old = me + ".old";
                        try { File.Delete(old); } catch { }
                        File.Move(me, old);
                        File.WriteAllBytes(me, exe);
                        exeReplaced = true;
                        return 2;
                    }
                }
                if (ver > servedVersion)
                {
                    byte[] page = FetchBytes(Config.UpdateUrl + "index.html" + bust, 30000);
                    if (ParseVersion(page) == ver)
                    {
                        Directory.CreateDirectory(DataRoot);
                        File.WriteAllBytes(CachePath, page);
                        html = page; servedVersion = ver;
                        return 1;
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    static void Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext c;
            try { c = listener.GetContext(); } catch { return; }
            try
            {
                string path = c.Request.Url.AbsolutePath;
                if (path == "/" || path == "/index.html")
                {
                    byte[] page = html;
                    c.Response.ContentType = "text/html; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(page, 0, page.Length);
                }
                else if (path == "/api/update" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // o app pergunta de tempos em tempos: baixa a versão nova se tiver
                    int r = c.Request.HttpMethod == "POST" ? CheckUpdates() : 0;
                    byte[] body = Encoding.UTF8.GetBytes("{\"app\":" + servedVersion + ",\"launcher\":" + Config.LauncherVersion +
                        ",\"restart\":" + (exeReplaced ? "true" : "false") + ",\"enabled\":" + (Config.UpdateUrl != "" ? "true" : "false") +
                        ",\"url\":\"" + Config.UpdateUrl.Replace("\\", "").Replace("\"", "") + "\"}");
                    c.Response.ContentType = "application/json";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/apps" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // programas abertos, pra escolher de qual vai o som da transmissão
                    var sb = new StringBuilder("{\"supported\":" + (AppAudio.Supported ? "true" : "false") + ",\"apps\":[");
                    bool first = true;
                    foreach (var a in AppAudio.ListApps(Process.GetCurrentProcess().Id, DataDir))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"pid\":" + a.Pid + ",\"name\":" + JsonEsc(a.Name) + ",\"title\":" + JsonEsc(a.Title) + "}");
                    }
                    byte[] body = Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/appaudio" && c.Request.IsWebSocketRequest && c.Request.Headers["Origin"] == "http://localhost:" + Port)
                {
                    // o som do programa escolhido vai pro app por WebSocket (só a página do próprio Concorde pode abrir)
                    int pid; if (!int.TryParse(c.Request.QueryString["pid"], out pid) || pid < 0) { c.Response.StatusCode = 400; c.Response.Close(); continue; }
                    var ctx = c;
                    var t = new Thread(() => StreamAppAudio(ctx, pid));
                    t.IsBackground = true;
                    t.SetApartmentState(ApartmentState.MTA);
                    t.Start();
                    continue; // a conexão fica aberta
                }
                else if (path == "/api/backup" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // cópia de segurança da conta (fora do navegador): se o navegador perder os dados, o app restaura daqui
                    string file = Path.Combine(DataRoot, "conta.json");
                    // segunda cópia em Documentos\Concorde (no OneDrive vai pra nuvem: formatou o PC, a conta continua salva)
                    string cloud = Path.Combine(CloudDir, "conta.json");
                    // e cada conta que já entrou neste PC fica em contas\<usuário>.json (pra sair e entrar de novo, ou trocar de conta)
                    string user = AccountUser(c.Request.QueryString["user"]);
                    byte[] body;
                    if (c.Request.HttpMethod == "POST")
                    {
                        string json;
                        using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) json = r.ReadToEnd();
                        if (json.Length > 20 && json.Length < 30000000 && json.StartsWith("{") && json.Contains("\"tc-me\""))
                        {
                            bool now = c.Request.QueryString["agora"] == "1";
                            Directory.CreateDirectory(DataRoot);
                            if (File.Exists(file)) { try { File.Copy(file, Path.Combine(DataRoot, "conta.anterior.json"), true); } catch { } }
                            SafeWrite(file, json);
                            if (user != null) SafeWrite(Path.Combine(DataRoot, @"contas\" + user + ".json"), json);
                            DailyCopy(json, user);
                            // a da nuvem é atualizada no máximo a cada 10 min (pra não ficar sincronizando o tempo todo); ao sair, na hora
                            try
                            {
                                if (now || !File.Exists(cloud) || (DateTime.Now - File.GetLastWriteTime(cloud)).TotalMinutes >= 10)
                                {
                                    SafeWrite(cloud, json);
                                    if (user != null) SafeWrite(Path.Combine(CloudDir, @"contas\" + user + ".json"), json);
                                }
                            }
                            catch { }
                            body = Encoding.UTF8.GetBytes("{\"ok\":true}");
                        }
                        else body = Encoding.UTF8.GetBytes("{\"ok\":false}");
                    }
                    else if (c.Request.HttpMethod == "DELETE")
                    {
                        // saiu da conta: não entra mais sozinho nela (a cópia em contas\ continua)
                        // só apaga se a cópia for mesmo da conta que saiu (outra conta ou perfil de teste não apaga a de ninguém)
                        foreach (var f in new[] { file, cloud }) try { if (File.Exists(f) && user != null && IsAccountOf(File.ReadAllText(f, Encoding.UTF8), user)) File.Delete(f); } catch { }
                        body = Encoding.UTF8.GetBytes("{\"ok\":true}");
                    }
                    else
                    {
                        string src;
                        if (user != null) src = Newest(Path.Combine(DataRoot, @"contas\" + user + ".json"), Path.Combine(CloudDir, @"contas\" + user + ".json"));
                        else src = File.Exists(file) ? file : File.Exists(cloud) ? cloud : null; // a cópia do PC; se não tiver (PC formatado), a da nuvem
                        try { body = src != null ? File.ReadAllBytes(src) : Encoding.UTF8.GetBytes("{}"); }
                        catch { body = Encoding.UTF8.GetBytes("{}"); }
                    }
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/accounts" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // contas guardadas neste PC e no OneDrive: [{"user":"x","t":ms}]
                    var seen = new System.Collections.Generic.Dictionary<string, long>();
                    foreach (var dir in new[] { Path.Combine(DataRoot, "contas"), Path.Combine(CloudDir, "contas") })
                    {
                        try
                        {
                            if (!Directory.Exists(dir)) continue;
                            foreach (var f in Directory.GetFiles(dir, "*.json"))
                            {
                                string u = AccountUser(Path.GetFileNameWithoutExtension(f));
                                if (u == null) continue;
                                long t = (long)(File.GetLastWriteTimeUtc(f) - new DateTime(1970, 1, 1)).TotalMilliseconds;
                                long old; if (!seen.TryGetValue(u, out old) || t > old) seen[u] = t;
                            }
                        }
                        catch { }
                    }
                    var sb = new StringBuilder("[");
                    foreach (var kv in seen) { if (sb.Length > 1) sb.Append(','); sb.Append("{\"user\":\"" + kv.Key + "\",\"t\":" + kv.Value + "}"); }
                    byte[] body = Encoding.UTF8.GetBytes(sb.Append(']').ToString());
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/activity" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // jogo aberto agora (pra mostrar "Jogando ..." igual Discord)
                    var g = Activity.CurrentGame();
                    string json = g == null ? "{\"game\":null}"
                        : "{\"game\":" + JsonEsc(g.Name) + ",\"since\":" + g.Since + ",\"store\":" + JsonEsc(g.Store) + "}";
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/games" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // busca de jogos na loja da Steam (pra "Jogos que eu gosto" e "Lista de desejos")
                    string gid = c.Request.QueryString["img"];
                    byte[] body = Encoding.UTF8.GetBytes(gid != null ? Activity.GameImage(gid) : Activity.SearchGames(c.Request.QueryString["q"]));
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/preview" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // prévia de link (título, descrição, imagem), igual os cartões do Discord
                    byte[] body = Encoding.UTF8.GetBytes(Activity.Preview(c.Request.QueryString["url"]));
                    c.Response.ContentType = "application/json; charset=utf-8";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/autostart" && c.Request.Headers["X-TelaCall"] == "1")
                {
                    // o cabeçalho próprio impede outros sites de mexerem nisso
                    if (c.Request.HttpMethod == "POST") SetAutostart(c.Request.QueryString["on"] == "1");
                    byte[] body = Encoding.UTF8.GetBytes("{\"enabled\":" + (AutostartEnabled() ? "true" : "false") + "}");
                    c.Response.ContentType = "application/json";
                    c.Response.Headers["Cache-Control"] = "no-store";
                    c.Response.OutputStream.Write(body, 0, body.Length);
                }
                else c.Response.StatusCode = 404;
                c.Response.Close();
            }
            catch { }
        }
    }

    // "Desinstalar" (pelo Windows → Aplicativos instalados): tira o programa, os atalhos e o "abrir com o Windows".
    // Amigos e mensagens (AppData\Local\TelaCall) ficam, caso instale de novo.
    static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Desinstalar o Concorde deste PC?\n\nSeus amigos e mensagens ficam guardados caso você instale de novo.",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        // fecha a janela com calma (igual o X) pra não perder dados; força só se não fechar
        var browsers = new System.Collections.Generic.List<Process>();
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
            foreach (ManagementObject p in q.Get())
            {
                var cmd = p["CommandLine"] as string;
                if (cmd != null && cmd.IndexOf(DataDir, StringComparison.OrdinalIgnoreCase) >= 0) try { browsers.Add(Process.GetProcessById((int)(uint)p["ProcessId"])); } catch { }
            }
        }
        catch { }
        foreach (var b in browsers) try { if (b.MainWindowHandle != IntPtr.Zero) b.CloseMainWindow(); } catch { }
        var until = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < until && browsers.Exists(b => { try { return !b.HasExited; } catch { return false; } })) Thread.Sleep(250);
        foreach (var b in browsers) try { if (!b.HasExited) b.Kill(); } catch { }
        foreach (var p in Process.GetProcessesByName("Concorde")) { if (p.Id != Process.GetCurrentProcess().Id) try { p.Kill(); } catch { } }
        SetAutostart(false);
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Concorde", false); } catch { }
        foreach (var lnk in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Concorde.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Concorde.lnk") })
            try { File.Delete(lnk); } catch { }
        if (!quiet) MessageBox.Show("O Concorde foi desinstalado.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        // o .exe em uso não se apaga sozinho: pede pro Windows apagar a pasta logo depois que este programa fechar
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        if (dir.EndsWith(@"\Programs\Concorde", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }
    }

    static string JsonEsc(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s ?? "")
        {
            if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
            else if (ch < 32) sb.Append("\\u").Append(((int)ch).ToString("x4"));
            else sb.Append(ch);
        }
        return sb.Append('"').ToString();
    }

    // manda o som do programa (float estéreo 48 kHz) até o app fechar a conexão
    static void StreamAppAudio(HttpListenerContext c, int pid)
    {
        System.Net.WebSockets.WebSocket ws;
        try { ws = c.AcceptWebSocketAsync(null).Result.WebSocket; } catch { return; }
        var stop = new ManualResetEvent(false);
        var rx = new Thread(() =>
        {
            var b = new byte[256];
            try
            {
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(b), CancellationToken.None).Result;
                    if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                }
            }
            catch { }
            stop.Set();
        });
        rx.IsBackground = true;
        rx.Start();
        Action<float[], int> send = (buf, n) =>
        {
            var bytes = new byte[n * 4];
            Buffer.BlockCopy(buf, 0, bytes, 0, n * 4);
            try { ws.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Binary, true, CancellationToken.None).Wait(); }
            catch { stop.Set(); }
        };
        // pid 0 = "som do PC inteiro" sem o Discord e sem as vozes da call
        string err = pid == 0 ? AppAudio.CapturePc(Process.GetCurrentProcess().Id, DataDir, send, stop) : AppAudio.Capture(pid, send, stop);
        try
        {
            if (err != null)
            {
                var e = Encoding.UTF8.GetBytes("erro:" + err);
                ws.SendAsync(new ArraySegment<byte>(e), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).Wait(1000);
            }
            ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000);
        }
        catch { }
    }

    static bool AutostartEnabled()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                if (k == null) return false;
                if (k.GetValue(RunName) != null) return true;
                foreach (var n in OldRunNames) if (k.GetValue(n) != null) return true;
                return false;
            }
        }
        catch { return false; }
    }

    static void SetAutostart(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --startup");
                else k.DeleteValue(RunName, false);
                foreach (var n in OldRunNames) k.DeleteValue(n, false); // troca o nome antigo pelo novo

            }
        }
        catch { }
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    // a barrinha do Edge "http://localhost:47821 está compartilhando sua tela. [Parar] [Ocultar]": esconde sozinho,
    // igual clicar em "Ocultar" (pra parar a live tem o botão no próprio Concorde). Só a barra do Concorde, pelo endereço no título;
    // avisos do tipo "localhost:47821 diz" não têm "compartilh" no título e ficam como estão.
    static void HideShareBar()
    {
        string origin = "localhost:" + Port;
        var t = new StringBuilder(300); var c = new StringBuilder(64);
        while (true)
        {
            Thread.Sleep(700);
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    c.Clear(); GetClassName(h, c, 64);
                    if (c.ToString() != "Chrome_WidgetWin_1") return true;
                    t.Clear(); GetWindowText(h, t, 300);
                    string s = t.ToString();
                    if (s.IndexOf(origin, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (s.IndexOf("compartilh", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("sharing", StringComparison.OrdinalIgnoreCase) >= 0))
                        ShowWindow(h, 0); // 0 = esconder
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }
    }

    // acha a janela pelo processo do navegador do app (o título muda enquanto carrega)
    static void MinimizeWhenReady(string dataDir)
    {
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(500);
            var pids = new System.Collections.Generic.HashSet<uint>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                foreach (ManagementObject p in q.Get())
                {
                    var cmd = p["CommandLine"] as string;
                    if (cmd != null && cmd.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) >= 0) pids.Add((uint)p["ProcessId"]);
                }
            }
            catch { }
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // 4 = tem dono (popup)
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) { Thread.Sleep(800); ShowWindow(found, 6); return; } // 6 = minimizar
        }
    }

    static string FindBrowser()
    {
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates = {
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        return null;
    }

    static bool BrowserRunning(string dataDir)
    {
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
            foreach (ManagementObject p in q.Get())
            {
                var cmd = p["CommandLine"] as string;
                if (cmd != null && cmd.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        catch { return true; }
    }
}
