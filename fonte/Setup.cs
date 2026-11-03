// Instalador do Concorde: coloca o Concorde.exe em AppData\Local\Programs\Concorde (sem precisar de administrador),
// cria atalhos, registra em "Aplicativos instalados" (com Desinstalar) e abre o programa.
// Os dados (amigos, mensagens) ficam em AppData\Local\TelaCall e não são mexidos.
// O instalador é o próprio Concorde.exe: com "setup" no nome do arquivo (concorde-setup.exe) ele instala uma cópia de si
// mesmo. Um arquivo só pra assinar digitalmente a cada versão.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Concorde");
    static readonly string Exe = Path.Combine(Dir, "Concorde.exe");
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Concorde";
    public const string PrivacyUrl = "https://github.com/justinocosta007/concorde#privacidade";

    // é pra instalar? (nome do arquivo com "setup"/"instalar", ou /silencioso, /navegador)
    public static bool IsInstaller(string[] args)
    {
        string me = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
        return me.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0 || me.IndexOf("instalar", StringComparison.OrdinalIgnoreCase) >= 0 ||
               Array.IndexOf(args, "/silencioso") >= 0 || Array.IndexOf(args, "/navegador") >= 0 || Array.IndexOf(args, "/instalar") >= 0;
    }

    public static void Run(string[] args)
    {
        Application.EnableVisualStyles();
        bool quiet = Array.IndexOf(args, "/silencioso") >= 0;
        ForceWeb = Array.IndexOf(args, "/navegador") >= 0;
        if (quiet)
        {
            string err = Install(true, false);
            try { File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TelaCall\instalar.txt"), DateTime.Now + " " + (err ?? (WebMode ? "ok (navegador)" : "ok"))); } catch { }
            return;
        }

        var f = new Form
        {
            Text = "Instalar Concorde", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, ClientSize = new Size(440, 300), BackColor = Color.FromArgb(26, 26, 30),
            ForeColor = Color.White, Font = new Font("Segoe UI", 10f), Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        };
        var logo = new PictureBox { Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(24, 24, 64, 64) };
        var title = new Label { Text = "Concorde", Font = new Font("Segoe UI", 20f, FontStyle.Bold), AutoSize = true, Location = new Point(100, 26) };
        var sub = new Label { Text = "Call, tela em Quad HD 60fps e chat com os amigos.", AutoSize = true, ForeColor = Color.FromArgb(181, 186, 193), Location = new Point(102, 66) };
        var desk = new CheckBox { Text = "Criar atalho na Área de Trabalho", Checked = true, AutoSize = true, Location = new Point(28, 118) };
        var auto = new CheckBox { Text = "Abrir junto com o Windows (fica online pros amigos)", Checked = true, AutoSize = true, Location = new Point(28, 148) };
        var note = new Label { Text = "Instala em: " + Dir + "\nSeus amigos e mensagens continuam salvos.", AutoSize = false, Bounds = new Rectangle(28, 182, 390, 34), ForeColor = Color.FromArgb(148, 155, 164), Font = new Font("Segoe UI", 8.5f) };
        // política de privacidade (o que sai do PC e pra onde), mostrada antes de instalar
        var priv = new LinkLabel { Text = "Política de privacidade", AutoSize = true, Location = new Point(28, 216), Font = new Font("Segoe UI", 8.5f),
                                   LinkColor = Color.FromArgb(0, 168, 252), ActiveLinkColor = Color.FromArgb(0, 168, 252) };
        priv.LinkClicked += (s, e) => { try { Process.Start(PrivacyUrl); } catch { } };
        var btn = new Button { Text = File.Exists(Exe) ? "Atualizar e abrir" : "Instalar", Bounds = new Rectangle(250, 240, 166, 40), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(88, 101, 242), ForeColor = Color.White, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
        btn.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Cancelar", Bounds = new Rectangle(140, 240, 100, 40), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(42, 42, 47), ForeColor = Color.White };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (s, e) => f.Close();
        btn.Click += (s, e) =>
        {
            btn.Enabled = false; cancel.Enabled = false; btn.Text = "Instalando...";
            string err = Install(desk.Checked, auto.Checked);
            if (err != null) { MessageBox.Show("Não deu pra instalar:\n\n" + err, "Concorde", MessageBoxButtons.OK, MessageBoxIcon.Error); btn.Enabled = cancel.Enabled = true; btn.Text = "Tentar de novo"; return; }
            if (WebMode)
                MessageBox.Show("O Windows deste PC bloqueia programas novos (Controle Inteligente de Aplicativos), então instalei o Concorde pelo Edge.\n\n" +
                    "Call, tela e chat funcionam normal. Só o som de um programa / \"Som do PC inteiro\" na live e a cópia da conta no PC precisam do Concorde.exe.\n\n" +
                    "Entre com seu usuário e senha pra pegar sua conta.", "Concorde", MessageBoxButtons.OK, MessageBoxIcon.Information);
            f.Close();
        };
        f.Controls.AddRange(new Control[] { logo, title, sub, desk, auto, note, priv, btn, cancel });
        f.AcceptButton = btn;
        Application.Run(f);
    }

    static string Install(bool desktopShortcut, bool autostart)
    {
        try
        {
            CloseRunning();
            Directory.CreateDirectory(Dir);
            // o instalador é o próprio Concorde.exe: copia a si mesmo
            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Application.ExecutablePath, Exe + ".novo", true);
                if (File.Exists(Exe)) File.Delete(Exe);
                File.Move(Exe + ".novo", Exe);
            }
            try { File.Delete(Exe + ".old"); } catch { }

            // atalhos
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Concorde.lnk");
            Shortcut(startMenu);
            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Concorde.lnk");
            if (desktopShortcut) Shortcut(desk);

            // "Aplicativos instalados" do Windows
            using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
            {
                k.SetValue("DisplayName", "Concorde");
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("DisplayVersion", "1." + Config.LauncherVersion);
                k.SetValue("Publisher", "Família Advoliseiros");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Exe + "\" --desinstalar");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
            }
            // abrir com o Windows: aponta pro lugar novo (troca o atalho antigo de onde estava o .exe solto)
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                bool had = k.GetValue("Concorde") != null || k.GetValue("Programa call dos liseiro") != null || k.GetValue("TelaCall") != null;
                foreach (var n in new[] { "Programa call dos liseiro", "TelaCall" }) k.DeleteValue(n, false);
                if (autostart || had) k.SetValue("Concorde", "\"" + Exe + "\" --startup");
                else k.DeleteValue("Concorde", false);
            }
            try
            {
                if (ForceWeb) throw new System.ComponentModel.Win32Exception(4551);
                Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Dir });
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                // o Windows bloqueou o .exe (Controle Inteligente de Aplicativos / política de aplicativos):
                // instala a versão que abre pelo Edge, que é da Microsoft e não é bloqueado
                string web = InstallWeb(desktopShortcut, autostart, startMenu, desk);
                if (web != null) return e.Message + "\n\n" + web;
                WebMode = true;
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    public static bool WebMode, ForceWeb;   // /navegador = instala direto pelo Edge

    static string InstallWeb(bool desktopShortcut, bool autostart, string startMenu, string desk)
    {
        string browser = FindBrowser();
        if (browser == null) return "E não achei o Edge nem o Chrome pra abrir pelo navegador.";
        if (Config.UpdateUrl == "") return "E o link do site não está configurado.";
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TelaCall\NavegadorWeb");
        string args = "--app=" + Config.UpdateUrl + " --user-data-dir=\"" + data + "\" --no-first-run --no-default-browser-check --window-size=1280,800" +
            " --autoplay-policy=no-user-gesture-required";
        string ico = Path.Combine(Dir, "Concorde.ico");
        try
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Concorde.ico"))
            using (var o = File.Create(ico)) s.CopyTo(o);
        }
        catch { ico = Exe; }
        if (ico != Exe) try { File.Delete(Exe); } catch { }   // não roda neste PC, só ocupa espaço

        Shortcut(startMenu, browser, args, ico);
        if (desktopShortcut) Shortcut(desk, browser, args, ico);
        string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var k = Registry.CurrentUser.CreateSubKey(run))
        {
            if (autostart || k.GetValue("Concorde") != null) k.SetValue("Concorde", "\"" + browser + "\" " + args);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayIcon", ico);
            k.SetValue("DisplayVersion", "1." + Config.LauncherVersion + " (navegador)");
            k.SetValue("EstimatedSize", 1, RegistryValueKind.DWord);
            // sem o .exe, o "Desinstalar" apaga tudo pelo próprio Windows
            k.SetValue("UninstallString", "cmd.exe /c rd /s /q \"" + Dir + "\" & del /q \"" + startMenu + "\" \"" + desk + "\"" +
                " & reg delete \"HKCU\\" + run + "\" /v Concorde /f & reg delete \"HKCU\\" + UninstKey + "\" /f");
        }
        Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false });
        return null;
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

    static void Shortcut(string path) { Shortcut(path, Exe, "", Exe + ",0"); }
    static void Shortcut(string path, string target, string args, string icon)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = target;
        lnk.Arguments = args;
        lnk.WorkingDirectory = Dir;
        lnk.IconLocation = icon;
        lnk.Description = "Concorde - call e tela com os amigos";
        lnk.Save();
    }

    // fecha o Concorde aberto antes de trocar o arquivo.
    // A janela fecha "com calma" (igual clicar no X) pro navegador terminar de gravar amigos e mensagens;
    // fechar à força no meio da gravação pode apagar os dados.
    static void CloseRunning()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"TelaCall\Navegador");
        var browsers = new System.Collections.Generic.List<Process>();
        try
        {
            using (var q = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
            foreach (ManagementObject p in q.Get())
            {
                var cmd = p["CommandLine"] as string;
                if (cmd != null && cmd.IndexOf(data, StringComparison.OrdinalIgnoreCase) >= 0)
                    try { browsers.Add(Process.GetProcessById((int)(uint)p["ProcessId"])); } catch { }
            }
        }
        catch { }
        foreach (var b in browsers) try { if (b.MainWindowHandle != IntPtr.Zero) b.CloseMainWindow(); } catch { }
        var until = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < until && browsers.Exists(b => { try { return !b.HasExited; } catch { return false; } })) Thread.Sleep(250);
        foreach (var b in browsers) try { if (!b.HasExited) b.Kill(); } catch { }   // só se não fechou sozinho
        int self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("Concorde")) { if (p.Id == self) continue; try { p.Kill(); p.WaitForExit(3000); } catch { } } // o Concorde.exe em si não guarda dados
        Thread.Sleep(800);
    }
}
