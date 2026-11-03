// Captura o som de UM programa (e dos processos filhos dele) pelo Windows (WASAPI "process loopback"),
// igual o Discord faz: na transmissão vai só o som do filme/jogo, sem as vozes da call.
// Precisa do Windows 11 (ou Windows 10 com a atualização de 2022 em diante).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class AppAudio
{
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    // o Windows exige que quem recebe o aviso seja "ágil" (pode ser chamado de qualquer thread)
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    class Done : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Ev = new ManualResetEvent(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Ev.Set(); }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public const int Rate = 48000, Channels = 2;

    public static bool Supported
    {
        get
        {
            // Environment.OSVersion mente (diz 6.2) sem manifesto: lê o número real do Windows no registro
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.Parse((string)k.GetValue("CurrentBuildNumber")) >= 20348;
            }
            catch { return false; }
        }
    }

    // Abre a captura do programa "pid" (e filhos). onData recebe blocos de float estéreo intercalado.
    // Roda até "stop" ser sinalizado. Devolve uma mensagem de erro, ou null se terminou normal.
    public static string Capture(int pid, Action<float[], int> onData, WaitHandle stop)
    {
        IntPtr prm = IntPtr.Zero, pv = IntPtr.Zero, fmt = IntPtr.Zero;
        IAudioClient client = null;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS: tipo 1 = process loopback, modo 0 = incluir o processo e os filhos
            prm = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(prm, 0, 1);
            Marshal.WriteInt32(prm, 4, pid);
            Marshal.WriteInt32(prm, 8, 0);
            // PROPVARIANT com VT_BLOB apontando pros parâmetros
            pv = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 24 : 16);
            for (int i = 0; i < (IntPtr.Size == 8 ? 24 : 16); i++) Marshal.WriteByte(pv, i, 0);
            Marshal.WriteInt16(pv, 0, 65); // VT_BLOB
            Marshal.WriteInt32(pv, 8, 12);
            Marshal.WriteIntPtr(pv, IntPtr.Size == 8 ? 16 : 12, prm);

            var done = new Done();
            IActivateAudioInterfaceAsyncOperation op;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, pv, done, out op);
            if (!done.Ev.WaitOne(5000)) return "o Windows não respondeu";
            int hr; object iface;
            op.GetActivateResult(out hr, out iface);
            if (hr != 0 || iface == null) return "não deu pra abrir o som do programa (0x" + hr.ToString("X8") + ")";
            client = (IAudioClient)iface;

            // formato pedido: float 32 bits, 48 kHz, estéreo
            fmt = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(fmt, 0, 3);            // WAVE_FORMAT_IEEE_FLOAT
            Marshal.WriteInt16(fmt, 2, Channels);
            Marshal.WriteInt32(fmt, 4, Rate);
            Marshal.WriteInt32(fmt, 8, Rate * Channels * 4);
            Marshal.WriteInt16(fmt, 12, Channels * 4);
            Marshal.WriteInt16(fmt, 14, 32);
            Marshal.WriteInt16(fmt, 16, 0);
            const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERT = 0x80000000, SRC_DEFAULT = 0x08000000;
            hr = client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERT | SRC_DEFAULT, 2000000, 0, fmt, IntPtr.Zero);
            if (hr != 0) return "falhou ao preparar o som (0x" + hr.ToString("X8") + ")";
            var ev = new AutoResetEvent(false);
            client.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            object svc;
            hr = client.GetService(IID_IAudioCaptureClient, out svc);
            if (hr != 0) return "falhou ao abrir a captura (0x" + hr.ToString("X8") + ")";
            var cap = (IAudioCaptureClient)svc;
            hr = client.Start();
            if (hr != 0) return "falhou ao começar (0x" + hr.ToString("X8") + ")";

            var waits = new WaitHandle[] { stop, ev };
            float[] buf = new float[Rate * Channels];
            while (true)
            {
                int w = WaitHandle.WaitAny(waits, 100);
                if (w == 0) break;
                uint frames;
                while (cap.GetNextPacketSize(out frames) == 0 && frames > 0)
                {
                    IntPtr data; uint flags; ulong p1, p2;
                    if (cap.GetBuffer(out data, out frames, out flags, out p1, out p2) != 0) break;
                    int n = (int)frames * Channels;
                    if (n > buf.Length) buf = new float[n];
                    if ((flags & 2) != 0) Array.Clear(buf, 0, n);   // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buf, 0, n);
                    cap.ReleaseBuffer(frames);
                    onData(buf, n);
                }
            }
            client.Stop();
            return null;
        }
        catch (Exception e) { return e.Message; }
        finally
        {
            if (client != null) Marshal.ReleaseComObject(client);
            if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            if (pv != IntPtr.Zero) Marshal.FreeHGlobal(pv);
            if (prm != IntPtr.Zero) Marshal.FreeHGlobal(prm);
        }
    }

    // ---------- "Som do PC inteiro" sem vozes ----------
    // Pega o som de cada programa que está tocando (um por um, igual acima) e junta tudo,
    // pulando o Discord e outros apps de voz, o próprio Concorde (vozes da call) e os apps de áudio do fone
    // (que repassam o som de todo mundo, vozes inclusive).
    static readonly System.Text.RegularExpressions.Regex Voice = new System.Text.RegularExpressions.Regex(
        @"^(discord|discordptb|discordcanary|discorddevelopment|vesktop|webcord|legcord|armcord|teamspeak|ts3client_win(64|32)|ts5client.*|skype|ms-teams|teams|zoom|whatsapp|mumble|guilded|concorde|" +
        @"quantumengine|ngenuity.*|hyperx.*|voicemeeter.*|steelseries.*|nahimic.*|sonicstudio.*|razer.*|sonar.*|audiodg|voicemod.*|clownfish.*|voiceai.*|krisp.*|nvidia broadcast|obs.*)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int Item(int i, out IMMDevice dev);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int ctx, IntPtr prm, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr g, int flags, out IntPtr c);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr g, int flags, out IntPtr v);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int n);
        [PreserveSig] int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s);
    }
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr s);
        [PreserveSig] int SetDisplayName(IntPtr s, IntPtr g);
        [PreserveSig] int GetIconPath(out IntPtr s);
        [PreserveSig] int SetIconPath(IntPtr s, IntPtr g);
        [PreserveSig] int GetGroupingParam(out Guid g);
        [PreserveSig] int SetGroupingParam(IntPtr g, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(out IntPtr s);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr s);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    // programas com som aberto agora (em qualquer saída de áudio)
    static HashSet<int> AudioPids()
    {
        var set = new HashSet<int>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDeviceCollection devs;
            if (en.EnumAudioEndpoints(0, 1, out devs) != 0) return set; // render, ativos
            int nd; devs.GetCount(out nd);
            for (int d = 0; d < nd; d++)
            {
                IMMDevice dev; if (devs.Item(d, out dev) != 0) continue;
                object o; if (dev.Activate(new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), 23, IntPtr.Zero, out o) != 0) continue;
                IAudioSessionEnumerator se; if (((IAudioSessionManager2)o).GetSessionEnumerator(out se) != 0) continue;
                int ns; se.GetCount(out ns);
                for (int i = 0; i < ns; i++)
                {
                    object s; if (se.GetSession(i, out s) != 0) continue;
                    var c = s as IAudioSessionControl2; if (c == null) continue;
                    int st; uint pid;
                    if (c.GetState(out st) == 0 && st != 2 && c.GetProcessId(out pid) == 0 && pid != 0) set.Add((int)pid); // 2 = sessão encerrada
                }
            }
        }
        catch { }
        return set;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    class Proc { public int Parent; public string Name; }
    static Dictionary<int, Proc> Processes()
    {
        var d = new Dictionary<int, Proc>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return d;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
            if (Process32FirstW(snap, ref e))
                do
                {
                    string n = e.szExeFile ?? "";
                    if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                    d[(int)e.th32ProcessID] = new Proc { Parent = (int)e.th32ParentProcessID, Name = n };
                } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return d;
    }

    // navegador do Concorde (as vozes da call tocam nele)
    static HashSet<int> ConcordeBrowsers(string dataDir)
    {
        var set = new HashSet<int>();
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
            foreach (System.Management.ManagementObject p in q.Get())
            {
                var cmd = p["CommandLine"] as string;
                if (cmd != null && dataDir != null && cmd.IndexOf(dataDir, StringComparison.OrdinalIgnoreCase) >= 0) set.Add((int)(uint)p["ProcessId"]);
            }
        }
        catch { }
        return set;
    }

    // processo "proibido": app de voz, o próprio Concorde, ou filho do navegador do Concorde
    static bool Blocked(int pid, Dictionary<int, Proc> procs, HashSet<int> browsers, int selfPid)
    {
        for (int i = 0, p = pid; i < 64 && p > 0; i++)
        {
            Proc x; if (!procs.TryGetValue(p, out x)) break;
            if (p == selfPid || browsers.Contains(p) || Voice.IsMatch(x.Name)) return true;
            if (x.Parent == p) break;
            p = x.Parent;
        }
        return false;
    }

    // quais programas entram no "som do PC inteiro" agora: pid -> nome
    public static Dictionary<int, string> PcSources(int selfPid, string dataDir) { return PcSources(selfPid, ConcordeBrowsers(dataDir)); }
    static Dictionary<int, string> PcSources(int selfPid, HashSet<int> browsers)
    {
        var procs = Processes();
        var pick = new Dictionary<int, string>();
        // filhos de cada processo (pra saber se a "árvore" de um programa tem algum app de voz dentro)
        var kids = new Dictionary<int, List<int>>();
        foreach (var kv in procs) { List<int> l; if (!kids.TryGetValue(kv.Value.Parent, out l)) kids[kv.Value.Parent] = l = new List<int>(); l.Add(kv.Key); }
        foreach (int pid in AudioPids())
        {
            Proc x; if (!procs.TryGetValue(pid, out x)) continue;
            if (Blocked(pid, procs, browsers, selfPid)) continue;
            // a captura leva o programa e os filhos: se um filho for app de voz, não dá pra pegar esse programa
            bool bad = false; var stack = new Stack<int>(); stack.Push(pid); int guard = 0;
            while (stack.Count > 0 && !bad && guard++ < 2000)
            {
                int p = stack.Pop(); List<int> l;
                if (p != pid && (p == selfPid || browsers.Contains(p) || Voice.IsMatch(procs[p].Name))) bad = true;
                if (kids.TryGetValue(p, out l)) foreach (int k in l) if (k != p) stack.Push(k);
            }
            if (!bad) pick[pid] = x.Name;
        }
        // se um programa escolhido é pai de outro escolhido, o do pai já leva o som do filho (não duplica)
        var final = new Dictionary<int, string>();
        foreach (var kv in pick)
        {
            bool covered = false;
            for (int i = 0, p = procs[kv.Key].Parent; i < 64 && p > 0 && !covered; i++)
            {
                if (pick.ContainsKey(p) && p != kv.Key) covered = true;
                Proc x; if (!procs.TryGetValue(p, out x) || x.Parent == p) break;
                p = x.Parent;
            }
            if (!covered) final[kv.Key] = kv.Value;
        }
        return final;
    }

    class Src
    {
        public readonly float[] Buf = new float[Rate * Channels]; // 1 s
        public int R, W, N; public bool Primed;
        public readonly ManualResetEvent Stop = new ManualResetEvent(false);
        public volatile bool Dead;
    }

    // junta o som de todos os programas escolhidos e entrega em blocos (float estéreo 48 kHz) até "stop"
    public static string CapturePc(int selfPid, string dataDir, Action<float[], int> onData, WaitHandle stop)
    {
        var srcs = new Dictionary<int, Src>();
        var browsers = ConcordeBrowsers(dataDir);
        DateTime nextScan = DateTime.MinValue, nextBrowsers = DateTime.Now.AddSeconds(15);
        var sw = Stopwatch.StartNew(); long sent = 0;
        var mix = new float[Rate * Channels];
        const int Prime = Rate * Channels * 30 / 1000, MaxKeep = Rate * Channels * 200 / 1000;
        try
        {
            while (!stop.WaitOne(10))
            {
                if (DateTime.Now >= nextScan)
                {
                    nextScan = DateTime.Now.AddSeconds(1.5);
                    if (DateTime.Now >= nextBrowsers) { browsers = ConcordeBrowsers(dataDir); nextBrowsers = DateTime.Now.AddSeconds(15); }
                    var want = PcSources(selfPid, browsers);
                    foreach (var pid in new List<int>(srcs.Keys))
                        if (!want.ContainsKey(pid) || srcs[pid].Dead) { srcs[pid].Stop.Set(); srcs.Remove(pid); }
                    foreach (var kv in want)
                    {
                        if (srcs.ContainsKey(kv.Key)) continue;
                        var s = new Src(); int pid = kv.Key;
                        var t = new Thread(() =>
                        {
                            Capture(pid, (b, n) =>
                            {
                                lock (s)
                                {
                                    for (int i = 0; i < n; i++) { s.Buf[s.W] = b[i]; s.W = (s.W + 1) % s.Buf.Length; }
                                    s.N = Math.Min(s.N + n, s.Buf.Length);
                                    if (s.N > MaxKeep) { int drop = s.N - Prime; s.R = (s.R + drop) % s.Buf.Length; s.N -= drop; } // atrasou: volta pra perto do agora
                                }
                            }, s.Stop);
                            s.Dead = true;
                        });
                        t.IsBackground = true; t.SetApartmentState(ApartmentState.MTA); t.Start();
                        srcs[pid] = s;
                    }
                }
                long due = sw.ElapsedMilliseconds * Rate / 1000 - sent;
                if (due <= 0) continue;
                if (due > Rate / 4) { sent += due - Rate / 100; due = Rate / 100; } // ficou parado: não manda o atraso todo
                int len = (int)due * Channels;
                Array.Clear(mix, 0, len);
                foreach (var s in srcs.Values)
                    lock (s)
                    {
                        if (!s.Primed) { if (s.N < Prime) continue; s.Primed = true; }
                        int take = Math.Min(len, s.N);
                        for (int i = 0; i < take; i++) { mix[i] += s.Buf[s.R]; s.R = (s.R + 1) % s.Buf.Length; }
                        s.N -= take;
                        if (take < len) s.Primed = false; // acabou o que tinha: junta de novo antes de tocar
                    }
                for (int i = 0; i < len; i++) { float v = mix[i]; mix[i] = v > 1f ? 1f : v < -1f ? -1f : v; }
                onData(mix, len);
                sent += due;
            }
            return null;
        }
        catch (Exception e) { return e.Message; }
        finally { foreach (var s in srcs.Values) s.Stop.Set(); }
    }

    // ---------- lista de programas com janela (pra escolher de qual vai o som) ----------
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    public class App { public int Pid; public string Name, Title; }

    // um item por programa (pelo processo principal), com o título da janela mais "importante"
    public static List<App> ListApps(int skipPid, string skipDataDir)
    {
        var byPid = new Dictionary<uint, App>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(h); if (len == 0) return true;
            var sb = new StringBuilder(len + 1); GetWindowText(h, sb, sb.Capacity);
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == skipPid || byPid.ContainsKey(pid)) return true;
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { return true; }
            string t = sb.ToString();
            if (t == "Program Manager" || name == "TextInputHost" || name == "ApplicationFrameHost" || name == "explorer") return true;
            byPid[pid] = new App { Pid = (int)pid, Name = name, Title = t };
            return true;
        }, IntPtr.Zero);
        var list = new List<App>(byPid.Values);
        // tira a janela do próprio Concorde (o navegador com a pasta de dados dele)
        if (skipDataDir != null)
        {
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'"))
                foreach (System.Management.ManagementObject p in q.Get())
                {
                    var cmd = p["CommandLine"] as string;
                    if (cmd != null && cmd.IndexOf(skipDataDir, StringComparison.OrdinalIgnoreCase) >= 0)
                        list.RemoveAll(a => a.Pid == (int)(uint)p["ProcessId"]);
                }
            }
            catch { }
        }
        return list;
    }
}
