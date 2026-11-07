using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Mendeteksi aplikasi yang sedang memakai mikrofon / kamera (seperti titik hijau/oranye di iPhone).
/// Windows mencatatnya di registry CapabilityAccessManager: "LastUsedTimeStop = 0" artinya masih dipakai.
/// Tanpa polling: thread kecil menunggu sampai registry itu berubah.
/// </summary>
public sealed class PrivacyMonitor
{
    const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    readonly Dispatcher _dispatcher;
    Thread? _thread;
    ManualResetEvent? _stop;

    public IReadOnlyList<string> MicApps { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> CameraApps { get; private set; } = Array.Empty<string>();
    /// <summary>Aplikasi yang sedang merekam / menangkap layar (OBS, Snipping Tool, dll.).</summary>
    public IReadOnlyList<string> CaptureApps { get; private set; } = Array.Empty<string>();
    /// <summary>Sejak kapan tiap aplikasi mulai memakai mikrofon / perekam layar.</summary>
    public IReadOnlyDictionary<string, DateTime> MicSince { get; private set; } = new Dictionary<string, DateTime>();
    public IReadOnlyDictionary<string, DateTime> CaptureSince { get; private set; } = new Dictionary<string, DateTime>();
    public bool MicInUse => MicApps.Count > 0;
    public bool CameraInUse => CameraApps.Count > 0;
    public bool CaptureInUse => CaptureApps.Count > 0;

    /// <summary>Daftar pemakai mikrofon / kamera berubah (di thread UI).</summary>
    public event Action? Changed;

    public PrivacyMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool Running => _thread != null;

    public void Start()
    {
        if (_thread != null) return;
        _stop = new ManualResetEvent(false);
        _thread = new Thread(Loop, 128 * 1024) { IsBackground = true, Name = "PrivacyMonitor" };
        _thread.Start(_stop);
    }

    public void Stop()
    {
        if (_thread == null) return;
        _stop?.Set();
        _thread = null;
        _stop = null;
        Publish(Array.Empty<string>(), Array.Empty<string>());
    }

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter, SafeWaitHandle evt, bool async);

    void Loop(object? state)
    {
        var stop = (ManualResetEvent)state!;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(StoreKey);
            if (root == null) return;
            // sebagian versi Windows mencatat pemakaian di HKLM, bukan HKCU
            using var machine = Registry.LocalMachine.OpenSubKey(StoreKey);
            using var changed = new AutoResetEvent(false);
            while (!stop.WaitOne(0))
            {
                // daftar dulu, baru baca: perubahan di antara keduanya tidak terlewat
                // 0x1 = subkey baru/hapus, 0x4 = nilai berubah
                int rc = RegNotifyChangeKeyValue(root.Handle, true, 0x1 | 0x4, changed.SafeWaitHandle, true);
                if (rc != 0) Log("RegNotifyChangeKeyValue gagal: " + rc);
                Scan(root, machine);
                // diagnosa: tiap 30 detik (10x pertama) catat pemakaian terakhir yang tercatat Windows
                if (LogFolder != null && _beats < 10 && Environment.TickCount64 - _lastBeat > 30000)
                {
                    _beats++;
                    _lastBeat = Environment.TickCount64;
                    Log("detak: " + Newest(root) + " | HKLM: " + (machine != null ? Newest(machine) : "-") + " | mic aktif (audio): " + string.Join(", ", _audioSeen.Keys));
                }
                // mikrofon dibaca dari sesi rekam audio Windows (tidak memberi kabar), jadi periksa tiap 2 detik
                if (WaitHandle.WaitAny(new WaitHandle[] { changed, stop }, 2000) == 1) return;
                Thread.Sleep(150); // perubahan biasanya datang beruntun
            }
        }
        catch (Exception ex) { Log("error: " + ex); }
    }

    /// <summary>Folder catatan diagnosa (diisi App). Hanya beberapa baris pertama tiap sesi.</summary>
    public static string? LogFolder;
    static int _logLines;

    static void Log(string line)
    {
        if (LogFolder == null || _logLines > 60) return;
        _logLines++;
        try { File.AppendAllText(Path.Combine(LogFolder, "privacy.log"), $"[{DateTime.Now:HH:mm:ss}] {line}\r\n"); } catch { }
    }

    static void Merge(Dictionary<string, long> into, Dictionary<string, long> from)
    {
        foreach (var (k, v) in from)
            if (!into.TryGetValue(k, out var old) || v < old) into[k] = v;
    }

    Dictionary<string, long> InUseBoth(RegistryKey root, RegistryKey? machine, string capability)
    {
        var map = InUse(root, capability);
        if (machine != null) Merge(map, InUse(machine, capability));
        return map;
    }

    // ---------- mikrofon: sesi rekam audio yang sedang aktif (WASAPI) ----------

    MMDeviceEnumerator? _audio;
    readonly Dictionary<string, long> _audioSeen = new(StringComparer.OrdinalIgnoreCase);
    static readonly int OwnPid = Environment.ProcessId;

    /// <summary>Aplikasi yang sedang merekam dari mikrofon mana pun → sejak kapan (FILETIME).</summary>
    Dictionary<string, long> MicFromAudio()
    {
        var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _audio ??= new MMDeviceEnumerator();
            foreach (var dev in _audio.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    var sm = dev.AudioSessionManager;
                    sm.RefreshSessions();
                    var sessions = sm.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        try
                        {
                            if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                            int pid = (int)s.GetProcessID;
                            if (pid == 0 || pid == OwnPid) continue;
                            string? path = Native.ProcessPath(pid);
                            string name = path != null ? Path.GetFileNameWithoutExtension(path) : "";
                            if (name.Length == 0) continue;
                            now.Add(char.ToUpper(name[0]) + name[1..]);
                        }
                        catch { }
                    }
                }
                catch { }
                finally { dev.Dispose(); }
            }
        }
        catch (Exception ex) { Log("audio: " + ex.Message); _audio = null; }
        foreach (var k in _audioSeen.Keys.Where(k => !now.Contains(k)).ToList()) _audioSeen.Remove(k);
        foreach (var k in now) if (!_audioSeen.ContainsKey(k)) _audioSeen[k] = DateTime.Now.ToFileTime();
        return new Dictionary<string, long>(_audioSeen, StringComparer.OrdinalIgnoreCase);
    }

    void Scan(RegistryKey root, RegistryKey? machine)
    {
        var st = Settings.Current;
        // mikrofon: dari sesi rekam audio (catatan registry sudah tidak diperbarui Windows versi baru)
        var micMap = st.PrivacyDots || st.CallIndicator ? MicFromAudio() : new Dictionary<string, long>();
        // kamera & rekam layar hanya dibaca kalau pengaturannya dinyalakan (bawaan mati)
        var camMap = st.CameraDot ? InUseBoth(root, machine, "webcam") : new Dictionary<string, long>();
        // perekam layar memakai izin "tangkapan layar" Windows (Windows.Graphics.Capture)
        var capMap = st.RecordIndicator ? InUseBoth(root, machine, "graphicsCaptureProgrammatic") : new Dictionary<string, long>();
        if (st.RecordIndicator) Merge(capMap, InUseBoth(root, machine, "graphicsCaptureWithoutBorder"));
        var mic = micMap.Keys.OrderBy(x => x).ToList();
        var cam = camMap.Keys.OrderBy(x => x).ToList();
        var capture = capMap.Keys.OrderBy(x => x).ToList();
        _baseline = true; // setelah pemindaian pertama, entri baru juga dicatat
        bool same = mic.SequenceEqual(MicApps) && cam.SequenceEqual(CameraApps) && capture.SequenceEqual(CaptureApps);
        if (LogFolder != null && (!same || _logLines == 0))
            Log($"mic=[{string.Join(", ", mic)}] kamera=[{string.Join(", ", cam)}] layar=[{string.Join(", ", capture)}] " + Summary(root));
        if (same) return;
        Publish(mic, cam, capture, ToTimes(micMap), ToTimes(capMap));
    }

    static Dictionary<string, DateTime> ToTimes(Dictionary<string, long> map)
    {
        var d = new Dictionary<string, DateTime>();
        foreach (var (k, v) in map)
        {
            try { d[k] = DateTime.FromFileTime(v); } catch { d[k] = DateTime.Now; }
        }
        return d;
    }

    void Publish(IReadOnlyList<string> mic, IReadOnlyList<string> cam, IReadOnlyList<string>? capture = null,
                 IReadOnlyDictionary<string, DateTime>? micSince = null, IReadOnlyDictionary<string, DateTime>? captureSince = null)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            MicApps = mic;
            CameraApps = cam;
            CaptureApps = capture ?? Array.Empty<string>();
            MicSince = micSince ?? new Dictionary<string, DateTime>();
            CaptureSince = captureSince ?? new Dictionary<string, DateTime>();
            Changed?.Invoke();
        }));
    }

    /// <summary>Aplikasi yang sedang memakai izin ini → waktu mulai (FILETIME).</summary>
    static Dictionary<string, long> InUse(RegistryKey root, string capability)
    {
        var apps = new Dictionary<string, long>();
        using var cap = root.OpenSubKey(capability);
        if (cap == null) return apps;
        void Add(string name, long start)
        {
            if (start <= 0) return;
            if (!apps.TryGetValue(name, out var old) || start < old) apps[name] = start;
        }
        foreach (var name in cap.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var np = cap.OpenSubKey(name);
                if (np == null) continue;
                foreach (var exe in np.GetSubKeyNames())
                    Add(PrettyExe(exe), ActiveSince(np, exe));
            }
            else Add(PrettyPackage(name), ActiveSince(cap, name));
        }
        return apps;
    }

    /// <summary>Ringkasan untuk diagnosa: jumlah entri & yang LastUsedTimeStop-nya 0.</summary>
    static string Summary(RegistryKey root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var capName in new[] { "microphone", "webcam" })
        {
            using var cap = root.OpenSubKey(capName);
            if (cap == null) { sb.Append(capName + ":tidak ada "); continue; }
            int total = 0, zero = 0;
            void Count(RegistryKey parent)
            {
                foreach (var n in parent.GetSubKeyNames())
                {
                    if (n == "NonPackaged") { using var np = parent.OpenSubKey(n); if (np != null) Count(np); continue; }
                    using var k = parent.OpenSubKey(n);
                    if (k == null) continue;
                    total++;
                    object? st = k.GetValue("LastUsedTimeStart"), sp = k.GetValue("LastUsedTimeStop");
                    if (st != null && Convert.ToInt64(sp ?? 0L) == 0) zero++;
                }
            }
            Count(cap);
            sb.Append($"{capName}:{total} entri/{zero} aktif ");
        }
        return sb.ToString();
    }

    static int _beats;
    static long _lastBeat;

    /// <summary>Entri mic/kamera yang paling baru dipakai (nama, mulai, berhenti) — untuk diagnosa.</summary>
    static string Newest(RegistryKey root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var capName in new[] { "microphone", "webcam" })
        {
            string best = "-";
            long bestStart = 0, bestStop = 0;
            using var cap = root.OpenSubKey(capName);
            if (cap == null) continue;
            void Visit(RegistryKey parent)
            {
                foreach (var n in parent.GetSubKeyNames())
                {
                    if (n == "NonPackaged") { using var np = parent.OpenSubKey(n); if (np != null) Visit(np); continue; }
                    using var k = parent.OpenSubKey(n);
                    if (k == null) continue;
                    long st = Convert.ToInt64(k.GetValue("LastUsedTimeStart") ?? 0L), sp = Convert.ToInt64(k.GetValue("LastUsedTimeStop") ?? 0L);
                    if (st > bestStart) { bestStart = st; bestStop = sp; best = n; }
                }
            }
            Visit(cap);
            string Fmt(long ft) { try { return ft > 0 ? DateTime.FromFileTime(ft).ToString("dd/MM HH:mm:ss") : "0"; } catch { return ft.ToString(); } }
            sb.Append($"{capName}: {best} mulai={Fmt(bestStart)} berhenti={Fmt(bestStop)}; ");
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, long> SeenStart = new();
    static bool _baseline;

    /// <summary>Waktu mulai (FILETIME) kalau sedang dipakai, 0 kalau tidak.</summary>
    static long ActiveSince(RegistryKey parent, string sub)
    {
        try
        {
            using var k = parent.OpenSubKey(sub);
            if (k == null) return 0;
            long start = Convert.ToInt64(k.GetValue("LastUsedTimeStart") ?? 0L);
            long stop = Convert.ToInt64(k.GetValue("LastUsedTimeStop") ?? 0L);
            // diagnosa: catat nilai mentah setiap kali sebuah aplikasi mulai / selesai memakai mic/kamera
            string id = parent.Name + "\\" + sub;
            long sig = start ^ (stop << 1);
            if (SeenStart.TryGetValue(id, out var old) ? old != sig : _baseline)
                Log($"berubah: {sub} start={start} stop={stop}");
            SeenStart[id] = sig;
            return start > 0 && stop == 0 ? start : 0;
        }
        catch { return 0; }
    }

    // "C:#Program Files#Discord#Discord.exe" → "Discord"
    static string PrettyExe(string key)
    {
        string file = key.Replace('#', '\\');
        string name = Path.GetFileNameWithoutExtension(file);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : key;
    }

    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera"
    static string PrettyPackage(string key)
    {
        string name = key.Split('_')[0];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
