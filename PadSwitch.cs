// PadSwitch : bascule entre les fenêtres avec le bouton PS (DualSense, DualSense Edge, DualShock 4) ou Xbox.
// Manettes Sony lues en Raw Input, manettes Xbox en XInput : lecture partagée, rien n'est injecté dans la manette.
// Visuel façon Alt+Tab avec miniatures DWM, mis à l'échelle de chaque écran.
// C# 5 / .NET Framework 4.8 : compilé par build.bat avec le csc fourni par Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("PadSwitch")]
[assembly: System.Reflection.AssemblyDescription("Switch windows with a game controller's PS or Xbox button")]
[assembly: System.Reflection.AssemblyCompany("elcst")]
[assembly: System.Reflection.AssemblyProduct("PadSwitch")]
[assembly: System.Reflection.AssemblyCopyright("MIT License - github.com/elcst/PadSwitch")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
#if DEV   // outils de développement (captures d'écran, écoute) : absents de l'exe publié, compilés avec « build.bat dev »
        if (args.Length >= 2 && args[0] == "--lang")   // forcer la langue (captures du README) : --lang en --snapshot …
        {
            Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(args[1]);
            var rest = new string[args.Length - 2]; Array.Copy(args, 2, rest, 0, rest.Length); args = rest;
        }
#endif
        N.SetProcessDpiAwarenessContext((IntPtr)(-4));   // PerMonitorV2 : pixels réels, l'échelle est gérée par Switcher.Scale
        if (args.Length == 1 && args[0] == "--selftest") { Environment.Exit(Pad.SelfTest() ? 0 : 1); }
#if DEV
        if (args.Length >= 2 && args[0] == "--snapshot") { Switcher.Snapshot(args[1], args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0, args.Length > 3 ? args[3] : null); return; }
        if (args.Length == 1 && args[0] == "--listen")   // test passif : affiche ce que la manette envoie pendant 8 s, ne bascule rien
        {
            var l = new Switcher { Listen = true };
            var t = new System.Windows.Forms.Timer { Interval = 8000 };
            t.Tick += (s, e) => { Console.WriteLine("rapports recus : " + l.Reports); Application.Exit(); };
            t.Start(); Application.Run(); return;
        }
        if (args.Length == 2 && args[0] == "--demo") { Switcher.Demo(args[1]); return; }
#endif
        bool created;
        using (var m = new Mutex(true, "PadSwitch.SingleInstance", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Settings.Load();
            var sw = new Switcher();
            var tray = new Tray(sw);
            Application.Run(new ApplicationContext());
            GC.KeepAlive(tray);
        }
    }
}

// Interface en français si Windows l'est, en anglais sinon.
static class L
{
    public static string T(string fr, string en) { return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? fr : en; }
}

// ---------------------------------------------------------------- Réglages (HKCU)
static class Settings
{
    const string Key = @"Software\PadSwitch", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool TapLast = true;   // appui court = fenêtre précédente
    public static int HoldMs = 300;      // maintien avant d'ouvrir le sélecteur

    public static void Load()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(Key))
        {
            if (k == null) { Save(); return; }   // premier lancement : rien d'autre, le démarrage avec Windows s'active dans le menu
            TapLast = (int)k.GetValue("TapLast", 1) != 0;
            HoldMs = (int)k.GetValue("HoldMs", 300);
        }
    }
    public static void Save()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("TapLast", TapLast ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("HoldMs", HoldMs, RegistryValueKind.DWord);
        }
    }
    public static bool Startup
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("PadSwitch") != null; }
    }
    public static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue("PadSwitch", "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue("PadSwitch", false);
        }
    }
}

// ---------------------------------------------------------------- Manette
// État décodé, commun à toutes les manettes : bouton central (PS / Xbox), validation (Croix / A), annulation (Rond / B),
// direction du stick gauche (-1 aucune, 0 haut, 1 droite, 2 bas, 3 gauche). La croix directionnelle est ignorée exprès :
// dans Rocket League elle envoie des messages rapides.
struct PadState { public bool Ps, Cross, Circle; public int Dir; }

static class Pad
{
    public const int DualSense = 1, DualShock4 = 2;
    public static int KindOf(int vid, int pid)
    {
        if (vid != 0x054C) return 0;
        if (pid == 0x0CE6 || pid == 0x0DF2) return DualSense;                    // DualSense, DualSense Edge
        if (pid == 0x05C4 || pid == 0x09CC || pid == 0x0BA0) return DualShock4;  // DS4 v1, v2, adaptateur sans fil
        return 0;
    }

    // DualSense — USB 0x01 (64 o) : LX=1, boutons=8 (carré 4, croix 5, rond 6), PS=10 bit 0 ; Bluetooth 0x31 : décalé d'un octet.
    // DualShock 4 — USB 0x01 : LX=1, boutons=5, PS=7 ; Bluetooth 0x11 : décalé de deux octets.
    // Les deux en Bluetooth « simple » (0x01 court, avant qu'un jeu active le mode complet) : comme la DS4 en USB.
    public static bool Parse(byte[] b, int n, int kind, out PadState s)
    {
        s = new PadState { Dir = -1 };
        int stick, btn, ps;
        if (kind == DualSense && n >= 64 && b[0] == 0x01) { stick = 1; btn = 8; ps = 10; }
        else if (kind == DualSense && n >= 12 && b[0] == 0x31) { stick = 2; btn = 9; ps = 11; }
        else if (kind == DualShock4 && n >= 10 && b[0] == 0x11) { stick = 3; btn = 7; ps = 9; }
        else if (n >= 10 && b[0] == 0x01) { stick = 1; btn = 5; ps = 7; }
        else return false;
        s.Ps = (b[ps] & 1) != 0;
        s.Cross = (b[btn] & 0x20) != 0;
        s.Circle = (b[btn] & 0x40) != 0;
        int x = b[stick] - 128, y = b[stick + 1] - 128;
        const int dead = 80;   // il faut pousser franchement le stick
        if (Math.Max(Math.Abs(x), Math.Abs(y)) > dead) s.Dir = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? 1 : 3) : (y > 0 ? 2 : 0);
        return true;
    }

    // XInput : Guide 0x0400 (visible seulement via XInputGetStateEx), A 0x1000, B 0x2000 ; Y du stick positif vers le haut.
    public static PadState FromXInput(ushort buttons, short lx, short ly)
    {
        var s = new PadState { Ps = (buttons & 0x0400) != 0, Cross = (buttons & 0x1000) != 0, Circle = (buttons & 0x2000) != 0, Dir = -1 };
        const int dead = 20000;
        int x = lx, y = ly;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) > dead) s.Dir = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? 1 : 3) : (y > 0 ? 0 : 2);
        return s;
    }

    public static bool SelfTest()
    {
        PadState s;
        var usb = new byte[64]; usb[0] = 1; usb[1] = usb[2] = 128; usb[8] = 0x08 | 0x20; usb[10] = 1;
        bool ok = Parse(usb, 64, DualSense, out s) && s.Ps && s.Cross && !s.Circle && s.Dir == -1;
        usb[8] = 0x02 | 0x40; usb[10] = 0;   // croix directionnelle à droite : ignorée
        ok &= Parse(usb, 64, DualSense, out s) && !s.Ps && s.Circle && s.Dir == -1;
        usb[8] = 0x08; usb[1] = 10;
        ok &= Parse(usb, 64, DualSense, out s) && s.Dir == 3;
        usb[1] = 128; usb[2] = 250;
        ok &= Parse(usb, 64, DualSense, out s) && s.Dir == 2;
        var bt = new byte[78]; bt[0] = 0x31; bt[2] = 128; bt[3] = 250; bt[9] = 0x08; bt[11] = 1;
        ok &= Parse(bt, 78, DualSense, out s) && s.Ps && s.Dir == 2;
        var bts = new byte[10]; bts[0] = 1; bts[1] = bts[2] = 128; bts[5] = 0x08 | 0x20; bts[7] = 1;
        ok &= Parse(bts, 10, DualSense, out s) && s.Ps && s.Cross && s.Dir == -1;
        var ds4 = new byte[64]; ds4[0] = 1; ds4[1] = 250; ds4[2] = 128; ds4[5] = 0x08 | 0x40; ds4[7] = 1;
        ok &= Parse(ds4, 64, DualShock4, out s) && s.Ps && s.Circle && !s.Cross && s.Dir == 1;
        var ds4bt = new byte[78]; ds4bt[0] = 0x11; ds4bt[3] = 128; ds4bt[4] = 5; ds4bt[7] = 0x08 | 0x20; ds4bt[9] = 1;
        ok &= Parse(ds4bt, 78, DualShock4, out s) && s.Ps && s.Cross && s.Dir == 0;
        s = FromXInput(0x0400 | 0x1000, 0, -30000);
        ok &= s.Ps && s.Cross && !s.Circle && s.Dir == 2;
        ok &= KindOf(0x054C, 0x0DF2) == DualSense && KindOf(0x054C, 0x09CC) == DualShock4 && KindOf(0x045E, 0x0B13) == 0;
        Console.WriteLine(ok ? "selftest ok" : "selftest FAILED");
        return ok;
    }
}

// ---------------------------------------------------------------- Fenêtres
class Win
{
    public IntPtr H; public string Title; public Bitmap Icon; public bool Minimized;
#pragma warning disable 649
    public IntPtr Thumb; public Rectangle ThumbRect; public Bitmap Shot;   // Shot : uniquement pour --snapshot (version dev)
#pragma warning restore 649
}

static class WinList
{
    // Même règles qu'Alt+Tab : visible, sans propriétaire, pas « outil », pas masquée par DWM (applis UWP en veille, autres bureaux).
    public static List<Win> List(IntPtr self)
    {
        var list = new List<Win>();
        N.EnumWindows((h, l) =>
        {
            if (h == self || !N.IsWindowVisible(h) || N.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            long ex = N.GetWindowLongPtr(h, -20).ToInt64();
            if ((ex & 0x80 /*TOOLWINDOW*/) != 0 && (ex & 0x40000 /*APPWINDOW*/) == 0) return true;
            if ((ex & 0x08000000 /*NOACTIVATE*/) != 0) return true;
            // Overlays (stats de jeu, enregistrement…) : toujours au premier plan et sans barre de titre, même quand ils deviennent cliquables.
            if ((ex & 0x8 /*TOPMOST*/) != 0 && (N.GetWindowLongPtr(h, -16).ToInt64() & 0xC00000 /*CAPTION*/) != 0xC00000) return true;
            if ((ex & 0x80020) == 0x80020) return true;   // LAYERED + TRANSPARENT : overlay traversé par la souris
            int cloaked; N.DwmGetWindowAttribute(h, 14, out cloaked, 4);
            if (cloaked != 0) return true;
            var sb = new StringBuilder(256);
            if (N.GetWindowText(h, sb, 256) == 0) return true;
            var cls = new StringBuilder(64); N.GetClassName(h, cls, 64);
            if (cls.ToString() == "Progman") return true;
            list.Add(new Win { H = h, Title = sb.ToString(), Icon = IconOf(h), Minimized = N.IsIconic(h) });
            return list.Count < 18;
        }, IntPtr.Zero);
        return list;
    }

    static Bitmap IconOf(IntPtr h)
    {
        IntPtr hi;
        foreach (int kind in new[] { 1 /*BIG*/, 2 /*SMALL2*/ })
            if (N.SendMessageTimeout(h, 0x7F /*WM_GETICON*/, (IntPtr)kind, IntPtr.Zero, 2 /*ABORTIFHUNG*/, 40, out hi) != IntPtr.Zero && hi != IntPtr.Zero)
                return Icon.FromHandle(hi).ToBitmap();
        hi = N.GetClassLongPtr(h, -14 /*GCLP_HICON*/);
        if (hi != IntPtr.Zero) return Icon.FromHandle(hi).ToBitmap();
        try
        {
            uint pid; N.GetWindowThreadProcessId(h, out pid);
            using (var p = Process.GetProcessById((int)pid)) return Icon.ExtractAssociatedIcon(p.MainModule.FileName).ToBitmap();
        }
        catch { return null; }
    }

    // Windows refuse SetForegroundWindow à un processus en arrière-plan : on s'accroche au fil de la fenêtre active
    // le temps de l'appel, puis, en dernier recours, SwitchToThisWindow (ce qu'utilise Alt+Tab). Aucune touche simulée.
    public static void Activate(IntPtr h)
    {
        if (N.IsIconic(h)) N.ShowWindow(h, 9 /*SW_RESTORE*/);
        IntPtr fg = N.GetForegroundWindow();
        uint dummy, fgThread = N.GetWindowThreadProcessId(fg, out dummy), me = N.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && N.AttachThreadInput(me, fgThread, true);
        N.BringWindowToTop(h);
        N.SetForegroundWindow(h);
        if (attached) N.AttachThreadInput(me, fgThread, false);
        if (N.GetForegroundWindow() != h)
        {
            N.SwitchToThisWindow(h, true);
        }
    }
}

// ---------------------------------------------------------------- Sélecteur
class Switcher : Form
{
    // Palette et mesures. Tout est peint à la main ; les miniatures sont composées par DWM par-dessus.
    static readonly Color Bg = Color.FromArgb(0x11, 0x11, 0x11), Card = Color.FromArgb(0x1C, 0x1C, 0x1C),
        Edge = Color.FromArgb(0x34, 0x34, 0x34), Text1 = Color.FromArgb(0xF2, 0xF2, 0xF2), Text2 = Color.FromArgb(0x8C, 0x8C, 0x8C),
        Faint = Color.FromArgb(0x5E, 0x5E, 0x5E), Slot = Color.FromArgb(0x18, 0x18, 0x18);
    // Mesures pensées à 100 % ; Scale() les multiplie pour l'écran visé (125 %, 150 %…).
    int TW, TH, CP, Gap, PM, Foot, TitleH, CW, CH, Rise; float K;
    Font FTitle, FFoot, FGlyph;
    void Scale(float k)
    {
        if (k == K) return;
        K = k;
        TW = S(232); TH = S(140); CP = S(10); Gap = S(8); PM = S(18); Foot = S(40); TitleH = S(30); Rise = S(6);
        CW = TW + 2 * CP; CH = CP + TH + TitleH;
        if (FTitle != null) { FTitle.Dispose(); FFoot.Dispose(); FGlyph.Dispose(); }
        FTitle = new Font(TitleFamily, 13f * k, GraphicsUnit.Pixel);
        FFoot = new Font(TextFamily, 12f * k, GraphicsUnit.Pixel);
        FGlyph = new Font(TitleFamily, 8.5f * k, GraphicsUnit.Pixel);
    }
    int S(float v) { return (int)Math.Round(v * K); }
    static float DpiScale(IntPtr h)
    {
        uint x, y;
        return N.GetDpiForMonitor(N.MonitorFromWindow(h, 2 /*NEAREST*/), 0 /*EFFECTIVE*/, out x, out y) == 0 ? x / 96f : 1f;
    }
    // Échelle de l'écran, réduite si le panneau dépasse 90 % de la hauteur (beaucoup de fenêtres sur un petit écran).
    void Fit(Rectangle scr, float k)
    {
        Scale(k); LayoutCards(scr.Width);
        int h = PanelSize().Height;
        if (h > scr.Height * 0.9) { Scale(k * (float)(scr.Height * 0.9 / h)); LayoutCards(scr.Width); }
    }

    static readonly PrivateFontCollection Fonts = new PrivateFontCollection();
    static FontFamily TitleFamily, TextFamily;

    List<Win> wins = new List<Win>();
    int sel, cols;
    RectangleF hl, hlFrom, hlTo;                          // cadre de sélection animé
    readonly System.Windows.Forms.Timer hold = new System.Windows.Forms.Timer();
    // Animations calées sur l'écran (DwmFlush) : apparition fondue + légère montée, sortie fondue, glissement du cadre.
    readonly Stopwatch clock = Stopwatch.StartNew();
    double openAt = -1, closeAt = -1, moveAt = -1; bool pumping; Point basePos;
    const double OpenMs = 140, CloseMs = 90, MoveMs = 120;

    PadState last; bool psDown, consumed;                 // consumed : PS déjà utilisé (sélecteur validé/annulé)
    int lastDir = -1; DateTime nextRepeat;
    public DateTime LastReport = DateTime.MinValue;
    readonly Dictionary<IntPtr, int> kinds = new Dictionary<IntPtr, int>();         // handle Raw Input -> type de manette
    readonly Dictionary<IntPtr, PadState> sources = new Dictionary<IntPtr, PadState>(); // dernier état de chaque manette
    bool xbox;                                             // dernière manette utilisée : Xbox (pied en A / B)
    byte[] buf = new byte[1024];

    static Switcher()
    {
        string family = "Segoe UI";
        foreach (var f in new[] { "Geist-Regular.ttf", "Geist-Medium.ttf" })
        {
            using (var s = typeof(Switcher).Assembly.GetManifestResourceStream(f))
            {
                if (s == null) continue;
                var data = new byte[s.Length]; s.Read(data, 0, data.Length);
                IntPtr p = Marshal.AllocCoTaskMem(data.Length);   // doit rester allouée tant que la police sert
                Marshal.Copy(data, 0, p, data.Length);
                Fonts.AddMemoryFont(p, data.Length);
            }
        }
        TitleFamily = Find("Geist Medium") ?? new FontFamily(family);
        TextFamily = Find("Geist") ?? new FontFamily(family);
    }
    static FontFamily Find(string n) { foreach (var f in Fonts.Families) if (f.Name == n) return f; return null; }

    public Switcher()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Bg; DoubleBuffered = true; Text = "PadSwitch";
        Scale(1f);
        hold.Tick += (s, e) => { hold.Stop(); if (psDown && !consumed) Open(); };
        CreateHandle();
        new Thread(PaceLoop) { IsBackground = true }.Start();
        new Thread(XInputLoop) { IsBackground = true }.Start();
        var rid = new N.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 5, dwFlags = 0x100 /*INPUTSINK*/ | 0x2000 /*DEVNOTIFY*/, hwndTarget = Handle };
        N.RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf(rid));
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08 /*TOPMOST*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x80000 /*LAYERED*/; return cp; }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, border = 0x00303030; // COLORREF BGR
        N.DwmSetWindowAttribute(Handle, 33 /*CORNER_PREFERENCE*/, ref round, 4);
        N.DwmSetWindowAttribute(Handle, 34 /*BORDER_COLOR*/, ref border, 4);
    }

    // ---- entrée manette
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x00FF /*WM_INPUT*/) OnRawInput(m.LParam);
        else if (m.Msg == 0x00FE /*WM_INPUT_DEVICE_CHANGE*/ && (int)m.WParam == 2 /*REMOVAL*/) Drop(m.LParam);
        base.WndProc(ref m);
    }

    void OnRawInput(IntPtr lp)
    {
        int hdr = Marshal.SizeOf(typeof(N.RAWINPUTHEADER)); uint size = 0;
        N.GetRawInputData(lp, 0x10000003 /*RID_INPUT*/, IntPtr.Zero, ref size, (uint)hdr);
        if (size == 0 || size > buf.Length) return;
        var gh = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            IntPtr p = gh.AddrOfPinnedObject();
            if (N.GetRawInputData(lp, 0x10000003, p, ref size, (uint)hdr) != size) return;
            var h = (N.RAWINPUTHEADER)Marshal.PtrToStructure(p, typeof(N.RAWINPUTHEADER));
            int kind = h.dwType == 2 /*HID*/ ? KindOf(h.hDevice) : 0;
            if (kind == 0) return;
            int each = BitConverter.ToInt32(buf, hdr), count = BitConverter.ToInt32(buf, hdr + 4);
            var report = new byte[each];
            for (int i = 0; i < count; i++)
            {
                Buffer.BlockCopy(buf, hdr + 8 + i * each, report, 0, each);
                PadState s; if (Pad.Parse(report, each, kind, out s)) Feed(h.hDevice, s, false);
            }
        }
        finally { gh.Free(); }
    }

    // Identifiants lus dans RID_DEVICE_INFO (le nom du périphérique ne les porte pas sous la même forme en Bluetooth).
    // Chaque (re)branchement donne un nouveau handle : la manette est reprise sans rien faire de plus.
    int KindOf(IntPtr dev)
    {
        int k;
        if (kinds.TryGetValue(dev, out k)) return k;
        var info = new byte[32]; BitConverter.GetBytes(32).CopyTo(info, 0); uint n = 32;
        k = N.GetRawInputDeviceInfo(dev, 0x2000000B /*DEVICEINFO*/, info, ref n) > 0 && BitConverter.ToInt32(info, 4) == 2
            ? Pad.KindOf(BitConverter.ToInt32(info, 8), BitConverter.ToInt32(info, 12)) : 0;
        kinds[dev] = k;
        return k;
    }

    // Manettes Xbox : XInput interrogé ~60 fois/s (les emplacements vides seulement toutes les 2 s, l'appel est lent).
    void XInputLoop()
    {
        var packet = new uint[4]; var on = new bool[4]; var probe = new DateTime[4];
        while (true)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!on[i] && DateTime.Now < probe[i]) continue;
                N.XINPUT_STATE st; bool ok;
                try { ok = N.XInputGetStateEx(i, out st) == 0; }
                catch (Exception) { return; }   // XInput absent : manettes Sony seulement
                var key = (IntPtr)(-1 - i);
                if (!ok)
                {
                    if (on[i]) { on[i] = false; Post(() => Drop(key)); }
                    probe[i] = DateTime.Now.AddSeconds(2);
                    continue;
                }
                if (!on[i] || st.dwPacketNumber != packet[i])
                {
                    var s = Pad.FromXInput(st.wButtons, st.sThumbLX, st.sThumbLY);
                    Post(() => Feed(key, s, true));
                }
                on[i] = true; packet[i] = st.dwPacketNumber;
            }
            Thread.Sleep(8);
        }
    }
    void Post(MethodInvoker a) { try { BeginInvoke(a); } catch (InvalidOperationException) { } }

    // Plusieurs manettes (ou la même vue deux fois via DS4Windows / Steam) : leurs états sont fusionnés.
    void Feed(IntPtr key, PadState s, bool isXbox)
    {
        LastReport = DateTime.Now;
        if (s.Ps || s.Cross || s.Circle || s.Dir >= 0) xbox = isXbox;
        sources[key] = s; Merge();
    }
    void Drop(IntPtr key) { if (sources.Remove(key)) Merge(); }
    void Merge()
    {
        var m = new PadState { Dir = -1 };
        foreach (var s in sources.Values) { m.Ps |= s.Ps; m.Cross |= s.Cross; m.Circle |= s.Circle; if (m.Dir < 0) m.Dir = s.Dir; }
        OnPad(m);
    }

#pragma warning disable 649   // Listen n'est utilisé que dans la version de développement
    public bool Listen; public int Reports;
#pragma warning restore 649
    void OnPad(PadState s)
    {
        Reports++;
        if (Listen)
        {
            if (s.Ps != last.Ps || s.Cross != last.Cross || s.Circle != last.Circle || s.Dir != last.Dir)
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " PS=" + s.Ps + " X=" + s.Cross + " O=" + s.Circle + " dir=" + s.Dir);
            last = s; return;
        }
        int dir = lastDir >= 0 && s.Dir >= 0 ? lastDir : s.Dir;   // un geste = une direction, même si le stick tourne en route
        if (s.Ps && !last.Ps) { psDown = true; consumed = false; hold.Interval = Settings.HoldMs; hold.Start(); }
        if (!s.Ps && last.Ps)
        {
            psDown = false; hold.Stop();
            if (IsOpen) Commit();
            else if (!consumed && Settings.TapLast) SwitchToPrevious();
        }
        if (IsOpen)
        {
            if (s.Cross && !last.Cross) { consumed = true; Commit(); }
            else if (s.Circle && !last.Circle) { consumed = true; BeginClose(); }
            else if (dir != lastDir) { if (dir >= 0) MoveSel(dir); nextRepeat = DateTime.Now.AddMilliseconds(320); }
            else if (dir >= 0 && DateTime.Now >= nextRepeat) { MoveSel(dir); nextRepeat = DateTime.Now.AddMilliseconds(110); }
        }
        lastDir = dir; last = s;
    }

    void SwitchToPrevious()
    {
        var l = WinList.List(Handle);
        int i = DefaultIndex(l);
        if (i < l.Count) WinList.Activate(l[i].H);
    }
    // Comme Alt+Tab : on part de la fenêtre d'avant si la fenêtre active est en tête de liste (sinon, bureau : la première).
    static int DefaultIndex(List<Win> l) { return l.Count > 1 && l[0].H == N.GetForegroundWindow() ? 1 : 0; }

    // ---- sélecteur
    bool IsOpen { get { return Visible && closeAt < 0; } }

    public void Open()
    {
        if (Visible) FinishClose();
        wins = WinList.List(Handle);
        if (wins.Count == 0) return;
        sel = Math.Min(DefaultIndex(wins), wins.Count - 1);
        nextRepeat = DateTime.Now.AddMilliseconds(320);   // stick déjà poussé à l'ouverture : pas de saut immédiat
        var fg = N.GetForegroundWindow();
        var scr = Screen.FromHandle(fg).Bounds;
        Fit(scr, DpiScale(fg));
        Size sz = PanelSize();
        basePos = new Point(scr.X + (scr.Width - sz.Width) / 2, scr.Y + (scr.Height - sz.Height) / 2);
        Bounds = new Rectangle(basePos.X, basePos.Y + Rise, sz.Width, sz.Height);
        hl = hlTo = CardRect(sel);
        SetAlpha(0);
        N.SetWindowPos(Handle, (IntPtr)(-1) /*TOPMOST*/, Left, Top, Width, Height, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
        Visible = true;
        foreach (var w in wins)
            if (!w.Minimized && N.DwmRegisterThumbnail(Handle, w.H, out w.Thumb) == 0) PlaceThumb(w, false);
        UpdateOpacity();
        Invalidate(); Update();
        openAt = clock.Elapsed.TotalMilliseconds; Pump();
    }

    void SetAlpha(double a) { N.SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(255 * Math.Max(0, Math.Min(1, a))), 2 /*LWA_ALPHA*/); }
    static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (1 - t) * (1 - t) * (1 - t); }   // ease-out cubique

    // Une image par rafraîchissement d'écran, tant qu'une animation tourne.
    // Le fil d'animation attend le rafraîchissement d'écran puis demande une image ; entre deux images le fil principal
    // reste libre pour lire la manette (avant, les images s'enchaînaient et les rapports s'empilaient : défilement sans fin).
    readonly AutoResetEvent wantFrame = new AutoResetEvent(false);
    void Pump() { if (pumping) return; pumping = true; wantFrame.Set(); }
    void PaceLoop()
    {
        while (true)
        {
            wantFrame.WaitOne(); N.DwmFlush();
            try { BeginInvoke((MethodInvoker)Frame); } catch (InvalidOperationException) { return; }
        }
    }
    void Frame()
    {
        pumping = false;
        if (!Visible) return;
        double now = clock.Elapsed.TotalMilliseconds; bool more = false;
        if (closeAt >= 0)
        {
            double e = Ease((now - closeAt) / CloseMs);
            if (e >= 1) { FinishClose(); return; }
            SetAlpha(1 - e); more = true;
        }
        else if (openAt >= 0)
        {
            double e = Ease((now - openAt) / OpenMs);
            SetAlpha(e);
            N.SetWindowPos(Handle, IntPtr.Zero, basePos.X, basePos.Y + (int)Math.Round((1 - e) * Rise), 0, 0, 0x1 /*NOSIZE*/ | 0x4 /*NOZORDER*/ | 0x10 /*NOACTIVATE*/);
            if (e < 1) more = true; else openAt = -1;
        }
        if (moveAt >= 0)
        {
            double e = Ease((now - moveAt) / MoveMs);
            var old = hl;
            hl = new RectangleF(hlFrom.X + (hlTo.X - hlFrom.X) * (float)e, hlFrom.Y + (hlTo.Y - hlFrom.Y) * (float)e, hlTo.Width, hlTo.Height);
            var trail = Rectangle.Ceiling(RectangleF.Union(old, hl)); trail.Inflate(2, 2); Invalidate(trail);   // l'espace entre les cartes aussi
            // la couleur des titres suit le cadre : on repeint les cartes entières qu'il touche
            for (int i = 0; i < wins.Count; i++)
            {
                var c = CardRect(i);
                if (c.IntersectsWith(Rectangle.Ceiling(old)) || c.IntersectsWith(Rectangle.Ceiling(hl))) { c.Inflate(2, 2); Invalidate(c); }
            }
            UpdateOpacity(); Update();
            if (e < 1) more = true; else moveAt = -1;
        }
        if (more) Pump();
    }

    // Part de la carte i couverte par le cadre (0..1) : sert à fondre titre et miniature pendant le glissement.
    float Lit(int i)
    {
        var c = CardRect(i); var r = RectangleF.Intersect(hl, c);
        return r.IsEmpty ? 0 : Math.Min(1, r.Width * r.Height / (c.Width * c.Height));
    }
    static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    void LayoutCards(int screenW)
    {
        int max = Math.Max(1, Math.Min(6, (int)(screenW * 0.86 - 2 * PM + Gap) / (CW + Gap)));
        cols = Math.Min(wins.Count, max);
    }
    Size PanelSize()
    {
        int rows = (wins.Count + cols - 1) / cols;
        return new Size(2 * PM + cols * CW + (cols - 1) * Gap, PM + rows * CH + (rows - 1) * Gap + Foot);
    }
    Rectangle CardRect(int i)
    {
        return new Rectangle(PM + (i % cols) * (CW + Gap), PM + (i / cols) * (CH + Gap), CW, CH);
    }
    Rectangle SlotRect(int i) { var c = CardRect(i); return new Rectangle(c.X + CP, c.Y + CP, TW, TH); }

    // Miniature à l'échelle, centrée dans sa case, alignée en bas pour que les titres tombent sur la même ligne.
    void PlaceThumb(Win w, bool snapshot)
    {
        var slot = SlotRect(wins.IndexOf(w));
        Size src;
        if (snapshot) src = w.Shot.Size; else N.DwmQueryThumbnailSourceSize(w.Thumb, out src);
        if (src.Width <= 0 || src.Height <= 0) return;
        float k = Math.Min((float)TW / src.Width, (float)TH / src.Height);
        int tw = (int)(src.Width * k), th = (int)(src.Height * k);
        w.ThumbRect = new Rectangle(slot.X + (TW - tw) / 2, slot.Bottom - th, tw, th);
    }

    void UpdateOpacity()
    {
        for (int i = 0; i < wins.Count; i++)
        {
            var w = wins[i]; if (w.Thumb == IntPtr.Zero) continue;
            var p = new N.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 | 0x4 | 0x8, // RECTDESTINATION | OPACITY | VISIBLE
                rcDestination = new N.RECT { L = w.ThumbRect.Left, T = w.ThumbRect.Top, R = w.ThumbRect.Right, B = w.ThumbRect.Bottom },
                opacity = (byte)(150 + 105 * Lit(i)), fVisible = 1
            };
            N.DwmUpdateThumbnailProperties(w.Thumb, ref p);
        }
    }

    void MoveSel(int dir)
    {
        int n = wins.Count, s = sel;
        if (dir == 1) s = Math.Min(sel + 1, n - 1);   // pas de retour au début : le stick maintenu s'arrête au bout
        else if (dir == 3) s = Math.Max(sel - 1, 0);
        else if (dir == 2 && sel + cols < n) s = sel + cols;
        else if (dir == 0 && sel - cols >= 0) s = sel - cols;
        if (s == sel) return;
        sel = s; hlFrom = hl; hlTo = CardRect(sel); moveAt = clock.Elapsed.TotalMilliseconds; Pump();
    }

    // La fenêtre cible passe devant tout de suite ; le sélecteur s'efface ensuite par-dessus.
    void Commit() { var h = wins.Count > sel ? wins[sel].H : IntPtr.Zero; if (h != IntPtr.Zero) WinList.Activate(h); BeginClose(); }
    void BeginClose() { if (!IsOpen) return; closeAt = clock.Elapsed.TotalMilliseconds; Pump(); }
    void FinishClose()
    {
        openAt = closeAt = moveAt = -1;
        foreach (var w in wins) if (w.Thumb != IntPtr.Zero) { N.DwmUnregisterThumbnail(w.Thumb); w.Thumb = IntPtr.Zero; }
        Visible = false;
    }

    // Ouvert depuis la zone de notification : la souris marche aussi (clic sur une carte = bascule, ailleurs = annule).
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!IsOpen) return;
        for (int i = 0; i < wins.Count; i++) if (CardRect(i).Contains(e.Location)) { sel = i; Commit(); return; }
        BeginClose();
    }

    // ---- dessin
    protected override void OnPaint(PaintEventArgs e) { Draw(e.Graphics); }

    void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;   // icônes 32 px réduites à 16 px sans crénelage
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Bg);
        using (var p = Rounded(hl, S(8))) using (var b = new SolidBrush(Card)) using (var pen = new Pen(Edge)) { g.FillPath(b, p); g.DrawPath(pen, p); }
        var fmt = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        for (int i = 0; i < wins.Count; i++)
        {
            var w = wins[i]; var c = CardRect(i); var slot = SlotRect(i);
            if (w.Thumb == IntPtr.Zero && w.Shot == null)
            {
                // fenêtre réduite : pas d'image, on montre l'icône en grand dans une case
                using (var p = Rounded(slot, S(6))) using (var b = new SolidBrush(Slot)) g.FillPath(b, p);
                if (w.Icon != null) g.DrawImage(w.Icon, new Rectangle(slot.X + TW / 2 - S(16), slot.Y + TH / 2 - S(16), S(32), S(32)));
            }
            if (w.Shot != null) g.DrawImage(w.Shot, w.ThumbRect);   // --snapshot uniquement, DWM fait ce travail en direct
            float lit = Lit(i);
            if (w.Shot != null) using (var b = new SolidBrush(Color.FromArgb((int)(105 * (1 - lit)), Bg))) g.FillRectangle(b, w.ThumbRect);
            int ty = slot.Bottom + S(4), th = TitleH - S(4);
            if (w.Icon != null) g.DrawImage(w.Icon, new Rectangle(c.X + CP, ty + (th - S(16)) / 2, S(16), S(16)));
            using (var b = new SolidBrush(Mix(Text2, Text1, lit)))
                g.DrawString(w.Title, FTitle, b, new RectangleF(c.X + CP + S(24), ty, TW - S(24), th), fmt);
        }
        // pied : rappel des commandes, avec les symboles de la manette dessinés au trait
        int fy = Height - Foot / 2 - S(2), x = PM + S(2);
        x = Hint(g, x, fy, 0, L.T("Basculer", "Switch")); x = Hint(g, x + S(18), fy, 1, L.T("Annuler", "Cancel")); Hint(g, x + S(18), fy, 2, L.T("Choisir", "Select"));
    }

    int Hint(Graphics g, int x, int cy, int glyph, string label)
    {
        float u = K;
        using (var pen = new Pen(Text2, 1.3f * u))
        {
            var r = new RectangleF(x, cy - 7 * u, 14 * u, 14 * u);
            if (xbox && glyph < 2)
            {   // A / B dans un cercle
                g.DrawEllipse(pen, r.X + 0.5f * u, r.Y + 0.5f * u, 13 * u, 13 * u);
                using (var b = new SolidBrush(Text2))
                using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(glyph == 0 ? "A" : "B", FGlyph, b, new RectangleF(r.X, r.Y + 0.5f * u, r.Width, r.Height), f);
            }
            else if (glyph == 0) { g.DrawLine(pen, r.X + 3 * u, r.Y + 3 * u, r.Right - 3 * u, r.Bottom - 3 * u); g.DrawLine(pen, r.Right - 3 * u, r.Y + 3 * u, r.X + 3 * u, r.Bottom - 3 * u); }
            else if (glyph == 1) g.DrawEllipse(pen, r.X + 2 * u, r.Y + 2 * u, 10 * u, 10 * u);
            else
            {   // stick gauche : cercle avec le champignon décalé
                g.DrawEllipse(pen, r.X + u, r.Y + u, 12 * u, 12 * u);
                using (var b = new SolidBrush(Text2)) g.FillEllipse(b, r.X + 5 * u, r.Y + 3 * u, 5 * u, 5 * u);
            }
        }
        using (var b = new SolidBrush(Faint))
        {
            g.DrawString(label, FFoot, b, x + S(20), cy - FFoot.Height / 2f - 0.5f * u);
            return x + S(20) + (int)g.MeasureString(label, FFoot).Width;
        }
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

#if DEV
    // Démo à l'écran (~1 s) : ouvre le sélecteur, capture, avance d'une carte, capture, referme.
    public static void Demo(string path)
    {
        var s = new Switcher(); s.Open();
        var t = new System.Windows.Forms.Timer { Interval = 450 }; int step = 0;
        t.Tick += (o, e) =>
        {
            if (step == 0) { s.Grab(path.Replace(".png", "-1.png")); s.MoveSel(1); }
            else if (step == 1) { s.Grab(path.Replace(".png", "-2.png")); s.BeginClose(); }
            else { t.Stop(); Application.Exit(); }
            step++;
        };
        t.Start(); Application.Run();
    }
    static string ProcessName(IntPtr h)
    {
        uint pid; N.GetWindowThreadProcessId(h, out pid);
        try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant(); } catch { return ""; }
    }
    void Grab(string p)
    {
        using (var b = new Bitmap(Width, Height))
        {
            using (var g = Graphics.FromImage(b)) g.CopyFromScreen(Location, Point.Empty, Size);
            b.Save(p);
        }
    }

    // Rendu hors écran (rien ne s'affiche) avec de vraies captures des fenêtres, pour montrer le visuel.
    // scale 0 : celle de l'écran principal ; only : noms de processus séparés par des virgules (captures pour le README)
    public static void Snapshot(string path, float scale, string only)
    {
        var s = new Switcher();
        s.wins = WinList.List(IntPtr.Zero);
        if (only != null) s.wins.RemoveAll(w => w.Minimized || Array.IndexOf(only.ToLowerInvariant().Split(','), ProcessName(w.H)) < 0);
        s.sel = Math.Min(DefaultIndex(s.wins), s.wins.Count - 1);
        s.Fit(Screen.PrimaryScreen.Bounds, scale > 0 ? scale : DpiScale(IntPtr.Zero));
        var sz = s.PanelSize(); s.Size = sz;
        foreach (var w in s.wins)
        {
            if (w.Minimized) continue;
            N.RECT r; N.GetWindowRect(w.H, out r);
            if (r.R - r.L <= 0 || r.B - r.T <= 0) continue;
            w.Shot = new Bitmap(r.R - r.L, r.B - r.T);
            using (var g = Graphics.FromImage(w.Shot)) { IntPtr dc = g.GetHdc(); N.PrintWindow(w.H, dc, 2 /*RENDERFULLCONTENT*/); g.ReleaseHdc(dc); }
            s.PlaceThumb(w, true);
        }
        s.hl = s.CardRect(s.sel);
        using (var bmp = new Bitmap(sz.Width, sz.Height))
        {
            using (var g = Graphics.FromImage(bmp)) { s.Height = sz.Height; s.Draw(g); }
            bmp.Save(path);
        }
    }
#endif
}

// ---------------------------------------------------------------- Zone de notification
class Tray
{
    readonly NotifyIcon icon;
    public Tray(Switcher sw)
    {
        icon = new NotifyIcon { Icon = MakeIcon(), Text = "PadSwitch", Visible = true };
        var menu = new ContextMenuStrip();
        var status = new ToolStripMenuItem { Enabled = false };
        var tap = new ToolStripMenuItem(L.T("Appui court : fenêtre précédente", "Short press: previous window")) { CheckOnClick = true };
        var holdMenu = new ToolStripMenuItem(L.T("Maintien pour le sélecteur", "Hold to open the switcher"));
        foreach (int ms in new[] { 200, 300, 450 })
        {
            int v = ms; var it = new ToolStripMenuItem(ms + " ms");
            it.Click += (s, e) => { Settings.HoldMs = v; Settings.Save(); };
            holdMenu.DropDownItems.Add(it);
        }
        var start = new ToolStripMenuItem(L.T("Démarrer avec Windows", "Start with Windows")) { CheckOnClick = true };
        var preview = new ToolStripMenuItem(L.T("Afficher le sélecteur", "Show the switcher"));
        var quit = new ToolStripMenuItem(L.T("Quitter", "Quit"));
        tap.Click += (s, e) => { Settings.TapLast = tap.Checked; Settings.Save(); };
        start.Click += (s, e) => Settings.SetStartup(start.Checked);
        preview.Click += (s, e) => sw.Open();
        quit.Click += (s, e) => { icon.Visible = false; Application.Exit(); };
        menu.Items.AddRange(new ToolStripItem[] { status, new ToolStripSeparator(), tap, holdMenu, start, new ToolStripSeparator(), preview, quit });
        menu.Opening += (s, e) =>
        {
            status.Text = (DateTime.Now - sw.LastReport).TotalSeconds < 2 ? L.T("Manette détectée", "Controller detected") : L.T("Aucune manette détectée", "No controller detected");
            tap.Checked = Settings.TapLast; start.Checked = Settings.Startup;
            foreach (ToolStripMenuItem it in holdMenu.DropDownItems) it.Checked = it.Text == Settings.HoldMs + " ms";
        };
        icon.ContextMenuStrip = menu;
    }

    // Deux fenêtres décalées, dessinées au trait : pas de fichier .ico à embarquer.
    static Icon MakeIcon()
    {
        using (var b = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, 2.4f)) g.DrawRectangle(pen, 3, 9, 18, 15);
            using (var br = new SolidBrush(Color.White)) g.FillRectangle(br, 11, 3, 18, 15);
            return Icon.FromHandle(b.GetHicon());
        }
    }
}

// ---------------------------------------------------------------- Win32
static class N
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES { public uint dwFlags; public RECT rcDestination, rcSource; public byte opacity; public int fVisible, fSourceClientAreaOnly; }

    [DllImport("user32")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32")] public static extern IntPtr GetClassLongPtr(IntPtr h, int i);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32")] public static extern uint GetCurrentThreadId();
    [DllImport("user32")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
#if DEV
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
#endif
    [DllImport("user32")] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32")] public static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint header);
    [DllImport("user32", EntryPoint = "GetRawInputDeviceInfoW")] public static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, byte[] data, ref uint size);
    [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE { public uint dwPacketNumber; public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    // Ordinal 100 = XInputGetStateEx, non documentée mais stable depuis Windows 8 : la seule qui renvoie le bouton Guide.
    [DllImport("xinput1_4.dll", EntryPoint = "#100")] public static extern int XInputGetStateEx(int index, out XINPUT_STATE state);
    [DllImport("user32")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
    [DllImport("dwmapi")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out Size s);
    [DllImport("dwmapi")] public static extern int DwmFlush();
    [DllImport("user32")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
}
