// Lockdown - input locker. Sits in the tray; when locked it dims every screen,
// swallows keyboard and mouse input, keeps the PC awake, and unlocks on the
// right key combination or when the registered USB key is plugged in.
//
//   Lockdown.exe               start (or, if already running, open settings)
//   Lockdown.exe --background  start hidden in the tray (used by autostart)
//   Lockdown.exe --restore     undo policy changes left behind by a crash/reboot
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
#if TEST
    public const string Id = "LockdownTest"; // tests never touch the real settings
#else
    public const string Id = "Lockdown";
#endif
    public const string AppKey = @"Software\" + Id;

    [STAThread]
    static int Main(string[] args)
    {
        // Any bug must end unlocked, never stuck: on an unhandled error put the
        // policies back and exit. Hooks and overlays die with the process.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (sender, e) => Fail(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (sender, e) => Fail(e.ExceptionObject);

        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "--restore") { Policies.Restore(); return 0; }

        bool created;
        using (Mutex mutex = new Mutex(true, Id + ".SingleInstance", out created))
        {
            if (!created)
            {
                // Already running: ask that instance to open its settings window.
                Native.PostMessage(new IntPtr(0xFFFF), Native.ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
            Policies.Restore(); // undo anything a killed run left behind
            Application.Run(new Agent(mode == "--background"));
            return 0;
        }
    }

    static void Fail(object error)
    {
        try { Policies.Restore(); } catch { }
        try { if (DeviceGuard.Active != null) DeviceGuard.Active.Restore(); } catch { }
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), DateTime.Now + "\r\n" + error + "\r\n\r\n");
        }
        catch { }
        Environment.Exit(1);
    }
}

static class Settings
{
    public static bool BlockMouseMove = true;
    public static bool BlockMouseButtons = true;
    public static bool AllowMediaKeys = false;
    public static bool KeepAwake = true;
    public static bool KeepDisplayOn = true;
    public static bool Harden = true;
    public static bool ShowHint = true;
    public static bool ShowDots = false;
    public static bool LockOnStart = false;
    public static bool UsbUnlock = true;  // plugging the USB key in unlocks
    public static bool UsbLock = false;   // pulling the USB key out locks
    public static bool BlockDevices = false; // newly plugged USB devices are stopped while locked

    // A blocked stick can never be read, so blocking devices turns USB unlock off.
    public static bool UsbUnlockActive
    {
        get { return UsbUnlock && !BlockDevices; }
    }
    public static int Dim = 60;           // percent
    public static int IdleMinutes = 0;    // 0 = off
    public static int HotkeyMods = 3;     // MOD_ALT 1 | MOD_CONTROL 2 | MOD_SHIFT 4
    public static int HotkeyVk = 0x4B;    // K; 0 = no hotkey

    static int Int(RegistryKey key, string name, int fallback)
    {
        object value = key.GetValue(name);
        return value is int ? (int)value : fallback;
    }

    static bool Bool(RegistryKey key, string name, bool fallback)
    {
        return Int(key, name, fallback ? 1 : 0) != 0;
    }

    public static void Load()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.AppKey))
        {
            if (key == null) return;
            BlockMouseMove = Bool(key, "BlockMouseMove", BlockMouseMove);
            BlockMouseButtons = Bool(key, "BlockMouseButtons", BlockMouseButtons);
            AllowMediaKeys = Bool(key, "AllowMediaKeys", AllowMediaKeys);
            KeepAwake = Bool(key, "KeepAwake", KeepAwake);
            KeepDisplayOn = Bool(key, "KeepDisplayOn", KeepDisplayOn);
            Harden = Bool(key, "Harden", Harden);
            ShowHint = Bool(key, "ShowHint", ShowHint);
            ShowDots = Bool(key, "ShowDots", ShowDots);
            LockOnStart = Bool(key, "LockOnStart", LockOnStart);
            UsbUnlock = Bool(key, "UsbUnlock", UsbUnlock);
            UsbLock = Bool(key, "UsbLock", UsbLock);
            BlockDevices = Bool(key, "BlockDevices", BlockDevices);
            Dim = Math.Max(10, Math.Min(90, Int(key, "Dim", Dim)));
            IdleMinutes = Math.Max(0, Int(key, "IdleMinutes", IdleMinutes));
            HotkeyMods = Int(key, "HotkeyMods", HotkeyMods);
            HotkeyVk = Int(key, "HotkeyVk", HotkeyVk);
        }
    }

    public static void Save()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Program.AppKey))
        {
            key.SetValue("BlockMouseMove", BlockMouseMove ? 1 : 0);
            key.SetValue("BlockMouseButtons", BlockMouseButtons ? 1 : 0);
            key.SetValue("AllowMediaKeys", AllowMediaKeys ? 1 : 0);
            key.SetValue("KeepAwake", KeepAwake ? 1 : 0);
            key.SetValue("KeepDisplayOn", KeepDisplayOn ? 1 : 0);
            key.SetValue("Harden", Harden ? 1 : 0);
            key.SetValue("ShowHint", ShowHint ? 1 : 0);
            key.SetValue("ShowDots", ShowDots ? 1 : 0);
            key.SetValue("LockOnStart", LockOnStart ? 1 : 0);
            key.SetValue("UsbUnlock", UsbUnlock ? 1 : 0);
            key.SetValue("UsbLock", UsbLock ? 1 : 0);
            key.SetValue("BlockDevices", BlockDevices ? 1 : 0);
            key.SetValue("Dim", Dim);
            key.SetValue("IdleMinutes", IdleMinutes);
            key.SetValue("HotkeyMods", HotkeyMods);
            key.SetValue("HotkeyVk", HotkeyVk);
        }
    }
}

// A combination is the sequence of key presses made before Enter. Each press
// records the key plus which of Shift/Ctrl/Alt/Win were held, so "K",
// "Shift+K" and "Ctrl+Alt+K" are three different steps. Esc clears,
// Backspace removes the last step. Independent of keyboard layout and CapsLock.
static class Combo
{
    public const int MinLength = 6;
    const int MaxLength = 64;

    // Bit for a modifier key (shift 1, ctrl 2, alt 4, win 8), or 0.
    public static int ModBit(int vk)
    {
        if (vk == 0x10 || vk == 0xA0 || vk == 0xA1) return 1;
        if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) return 2;
        if (vk == 0x12 || vk == 0xA4 || vk == 0xA5) return 4;
        if (vk == 0x5B || vk == 0x5C) return 8;
        return 0;
    }

    // Feeds one key press into the buffer. Returns true when Enter was pressed.
    public static bool Feed(List<int> keys, int vk, int mods)
    {
        if (ModBit(vk) != 0 || vk == 0x14) return false; // modifiers, CapsLock
        if (vk == 0x0D) return true;
        if (vk == 0x1B) keys.Clear();
        else if (vk == 0x08) { if (keys.Count > 0) keys.RemoveAt(keys.Count - 1); }
        else if (keys.Count < MaxLength) keys.Add(vk | (mods << 16));
        return false;
    }
}

static class Secret
{
    static byte[] Derive(List<int> keys, byte[] salt)
    {
        byte[] data = new byte[keys.Count * 4];
        Buffer.BlockCopy(keys.ToArray(), 0, data, 0, data.Length);
        using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(data, salt, 100000))
        {
            return kdf.GetBytes(32);
        }
    }

    public static bool Exists()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.AppKey))
        {
            return key != null && key.GetValue("Hash") is byte[] && key.GetValue("Salt") is byte[];
        }
    }

    public static void Save(List<int> keys)
    {
        byte[] salt = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Program.AppKey))
        {
            key.SetValue("Salt", salt, RegistryValueKind.Binary);
            key.SetValue("Hash", Derive(keys, salt), RegistryValueKind.Binary);
        }
    }

    public static bool Check(List<int> keys)
    {
        if (keys.Count == 0) return false;
        byte[] salt, hash;
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.AppKey))
        {
            if (key == null) return false;
            salt = key.GetValue("Salt") as byte[];
            hash = key.GetValue("Hash") as byte[];
        }
        if (salt == null || hash == null) return false;
        return Bytes.Same(Derive(keys, salt), hash);
    }
}

static class Bytes
{
    public static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

// A normal USB stick used as a key. Registering writes a random secret to a
// hidden file on the stick; the registry keeps only its hash plus the stick's
// volume serial number. The stick counts as present when both match, so
// copying the file to another stick is not enough.
static class UsbKey
{
    const string FileName = "lockdown.key";

    public static List<string> Drives()
    {
        List<string> roots = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Removable && drive.IsReady) roots.Add(drive.Name);
        }
        return roots;
    }

    static int Serial(string root)
    {
        uint serial, maxLength, flags;
        if (!Native.GetVolumeInformation(root, null, 0, out serial, out maxLength, out flags, null, 0)) return 0;
        return unchecked((int)serial);
    }

    public static bool Registered()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.AppKey))
        {
            return key != null && key.GetValue("UsbHash") is byte[];
        }
    }

    public static void Register(string root)
    {
        byte[] secret = new byte[32];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(secret);
        string file = Path.Combine(root, FileName);
        if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllBytes(file, secret);
        File.SetAttributes(file, FileAttributes.Hidden);
        using (SHA256 sha = SHA256.Create())
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Program.AppKey))
        {
            key.SetValue("UsbHash", sha.ComputeHash(secret), RegistryValueKind.Binary);
            key.SetValue("UsbSerial", Serial(root), RegistryValueKind.DWord);
        }
    }

    public static void Forget()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Program.AppKey))
        {
            key.DeleteValue("UsbHash", false);
            key.DeleteValue("UsbSerial", false);
        }
    }

    public static bool Present()
    {
        byte[] hash;
        object serial;
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Program.AppKey))
        {
            if (key == null) return false;
            hash = key.GetValue("UsbHash") as byte[];
            serial = key.GetValue("UsbSerial");
        }
        if (hash == null || !(serial is int)) return false;

        try
        {
            foreach (string root in Drives())
            {
                if (Serial(root) != (int)serial) continue;
                FileInfo file = new FileInfo(Path.Combine(root, FileName));
                if (!file.Exists || file.Length != 32) continue;
                using (SHA256 sha = SHA256.Create())
                {
                    if (Bytes.Same(sha.ComputeHash(File.ReadAllBytes(file.FullName)), hash)) return true;
                }
            }
        }
        catch (IOException) { }                 // stick pulled mid-read
        catch (UnauthorizedAccessException) { }
        return false;
    }
}

// While locked, disables Win+L and removes the escape routes on the
// Ctrl+Alt+Del screen (which no program can block): Task Manager, Lock,
// Change password, Sign out, Switch user. These keys are admin-only, which is
// why the app is elevated. Previous values are saved and put back on unlock.
static class Policies
{
    const string SavedKey = Program.AppKey + @"\Saved";
    const string SystemKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    // { hive ("U" = HKCU, "M" = HKLM), key path, value name }
    static readonly string[][] Items =
    {
        new[] { "U", SystemKey, "DisableTaskMgr" },
        new[] { "U", SystemKey, "DisableLockWorkstation" },
        new[] { "U", SystemKey, "DisableChangePassword" },
        new[] { "U", ExplorerKey, "NoLogoff" },
        new[] { "M", SystemKey, "HideFastUserSwitching" },
    };

    static RegistryKey Hive(string id)
    {
        return id == "M" ? Registry.LocalMachine : Registry.CurrentUser;
    }

    public static void Apply()
    {
        try
        {
            using (RegistryKey saved = Registry.CurrentUser.CreateSubKey(SavedKey))
            {
                foreach (string[] item in Items)
                {
                    using (RegistryKey key = Hive(item[0]).CreateSubKey(item[1]))
                    {
                        object old = key.GetValue(item[2]);
                        // "absent" marks a value that did not exist before.
                        if (old is int) saved.SetValue(item[2], (int)old, RegistryValueKind.DWord);
                        else saved.SetValue(item[2], "absent", RegistryValueKind.String);
                        key.SetValue(item[2], 1, RegistryValueKind.DWord);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }

    public static void Restore()
    {
        try
        {
            using (RegistryKey saved = Registry.CurrentUser.OpenSubKey(SavedKey))
            {
                if (saved == null) return;
                foreach (string[] item in Items)
                {
                    object old = saved.GetValue(item[2]);
                    if (old == null) continue; // never touched
                    using (RegistryKey key = Hive(item[0]).CreateSubKey(item[1]))
                    {
                        if (old is int) key.SetValue(item[2], (int)old, RegistryValueKind.DWord);
                        else key.DeleteValue(item[2], false);
                    }
                }
            }
            Registry.CurrentUser.DeleteSubKey(SavedKey, false);
        }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
    }
}

// While locked, stops USB devices that get plugged in. Devices connected when
// the lock began keep working until they are unplugged; anything that appears
// afterwards (new, or the same device plugged back in) is stopped before it
// can be used. Nothing is written to disk: a stopped device works again after
// unlock, after being re-plugged, or after a reboot.
//
// One exception: a USB keyboard that was connected when the lock began may
// reconnect. Otherwise a loose cable would leave no way to type the
// combination. A foreign keyboard gains nothing: its keys are swallowed.
sealed class DeviceGuard
{
    public static DeviceGuard Active; // for the crash handler

    const string KeyboardClass = "{4D36E96B-E325-11CE-BFC1-08002BE10318}";

    readonly HashSet<string> allowed;
    readonly HashSet<string> keyboards = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile bool stop;

    public DeviceGuard()
    {
        Native.EnablePrivilege("SeLoadDriverPrivilege"); // needed to stop devices
        allowed = Present();

        // Every USB node above a connected keyboard.
        foreach (string id in List(KeyboardClass, 0x200 | 0x100)) // FILTER_CLASS | FILTER_PRESENT
        {
            uint node;
            if (Native.CM_Locate_DevNodeW(out node, id, 0) != 0) continue;
            StringBuilder name = new StringBuilder(260);
            while (Native.CM_Get_Parent(out node, node, 0) == 0
                && Native.CM_Get_Device_IDW(node, name, name.Capacity, 0) == 0)
            {
                if (name.ToString().StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) keyboards.Add(name.ToString());
            }
        }

        Active = this;
        // Own thread: stopping a device can take seconds, and the UI thread
        // must stay free to answer the input hooks.
        Thread thread = new Thread(Run);
        thread.IsBackground = true;
        thread.Start();
    }

    // Instance IDs of all connected devices on the USB bus.
    public static HashSet<string> Present()
    {
        return List("USB", 0x1 | 0x100); // FILTER_ENUMERATOR | FILTER_PRESENT
    }

    static HashSet<string> List(string filter, uint flags)
    {
        HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint length;
        if (Native.CM_Get_Device_ID_List_SizeW(out length, filter, flags) != 0 || length == 0) return ids;
        char[] buffer = new char[length + 256]; // slack: the list can grow between the two calls
        if (Native.CM_Get_Device_ID_ListW(filter, buffer, (uint)buffer.Length, flags) != 0) return ids;
        foreach (string id in new string(buffer).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)) ids.Add(id);
        return ids;
    }

    public void Poke()
    {
        wake.Set();
    }

    public void Stop()
    {
        stop = true;
        wake.Set(); // the thread restores the stopped devices, then ends
    }

    void Run()
    {
        while (!stop)
        {
            Scan();
            wake.WaitOne(1000);
        }
        Restore();
        if (Active == this) Active = null;
    }

    void Scan()
    {
        HashSet<string> present = Present();
        // A device that left loses its pass, except the keyboards.
        allowed.RemoveWhere(id => !present.Contains(id) && !keyboards.Contains(id));
        stopped.RemoveWhere(id => !present.Contains(id));

        foreach (string id in present)
        {
            if (allowed.Contains(id)) continue;
            uint node, status, problem;
            if (Native.CM_Locate_DevNodeW(out node, id, 0) != 0) continue;
            if (Native.CM_Get_DevNode_Status(out status, out problem, node, 0) != 0) continue;
            if (stopped.Contains(id) && (status & 0x8) == 0) continue; // DN_STARTED: it came back to life

            int veto;
            StringBuilder vetoName = new StringBuilder(260);
            // 1 | 2 = CM_REMOVE_UI_NOT_OK | CM_REMOVE_NO_RESTART. If something
            // vetoes, try a plain eject; if that fails too, the next scan retries.
            if (Native.CM_Query_And_Remove_SubTreeW(node, out veto, vetoName, 260, 1 | 2) == 0
                || Native.CM_Request_Device_EjectW(node, out veto, vetoName, 260, 0) == 0)
            {
                stopped.Add(id);
            }
        }
    }

    public void Restore()
    {
        foreach (string id in stopped)
        {
            uint node, parent;
            if (Native.CM_Locate_DevNodeW(out node, id, 0) != 0) continue; // unplugged meanwhile
            Native.CM_Setup_DevNode(node, 4); // CM_SETUP_DEVNODE_RESET: clear "no restart"
            if (Native.CM_Get_Parent(out parent, node, 0) == 0) Native.CM_Reenumerate_DevNode(parent, 0);
        }
    }
}

// Start with Windows through a logon scheduled task. A task (unlike the Run
// registry key) can start an elevated program without a UAC prompt.
static class Autostart
{
    const string TaskName = "Lockdown";

    static int Run(string arguments)
    {
        ProcessStartInfo info = new ProcessStartInfo("schtasks.exe", arguments);
        info.CreateNoWindow = true;
        info.UseShellExecute = false;
        using (Process process = Process.Start(info))
        {
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    public static bool Enabled()
    {
        return Run("/query /tn " + TaskName) == 0;
    }

    public static bool Set(bool on)
    {
        if (!on) return Run("/delete /tn " + TaskName + " /f") == 0;

        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(Application.ExecutablePath);
        // No time limit and no battery restriction: the defaults would kill the
        // task after 3 days and skip it on battery.
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId>" +
            "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority></Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + exe + "</Command>" +
            "<Arguments>--background</Arguments></Exec></Actions>\r\n" +
            "</Task>\r\n";

        string file = Path.Combine(Path.GetTempPath(), "lockdown-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try { return Run("/create /tn " + TaskName + " /xml \"" + file + "\" /f") == 0; }
        finally { File.Delete(file); }
    }
}

// Low-level keyboard hook that tracks held modifiers and filters key repeat.
// (The OS key state cannot be used: it does not see keys this hook swallows.)
sealed class KeyHook : IDisposable
{
    // pressed: a fresh key press (not auto-repeat, not a release).
    // Return true to swallow the key.
    public delegate bool Handler(int vk, int mods, bool pressed);

    public bool Enabled = true;

    readonly Native.HookProc proc; // field keeps the delegate alive
    readonly Handler handler;
    readonly IntPtr hook;
    readonly HashSet<int> held = new HashSet<int>();
    int mods;

    public KeyHook(Handler handler)
    {
        this.handler = handler;
        proc = Proc;
        hook = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle(null), 0); // WH_KEYBOARD_LL
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Enabled)
        {
            int message = wParam.ToInt32();
            bool down = message == 0x0100 || message == 0x0104; // WM_KEYDOWN, WM_SYSKEYDOWN
            if (Inject(Marshal.ReadInt32(lParam), down)) return (IntPtr)1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Also called directly by windows for keys that reached them normally,
    // which only happens if Windows dropped the hook. Typing then still works.
    public bool Inject(int vk, bool down)
    {
        int bit = Combo.ModBit(vk);
        if (bit != 0) { if (down) mods |= bit; else mods &= ~bit; }
        bool pressed = down && held.Add(vk);
        if (!down) held.Remove(vk);
        return handler(vk, mods, pressed);
    }

    public void Reset()
    {
        held.Clear();
        mods = 0;
    }

    public void Dispose()
    {
        Native.UnhookWindowsHookEx(hook);
    }
}

sealed class Overlay : Form
{
    public KeyHook Fallback;

    readonly string hint;
    readonly int hintBottom;
    readonly bool clickThrough;
    string notice;
    int dots;

    // hint == null: no text on this screen.
    public Overlay(Screen screen, string hint, bool clickThrough, double opacity)
    {
        this.hint = hint;
        this.clickThrough = clickThrough;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        // Just above the taskbar.
        hintBottom = screen.WorkingArea.Bottom - screen.Bounds.Top - screen.Bounds.Height / 40;
        BackColor = Color.Black;
        ForeColor = Color.White;
        Opacity = opacity;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    public int Dots
    {
        set { if (dots != value) { dots = value; Invalidate(); } }
    }

    // Short message shown in place of the hint, or null.
    public string Notice
    {
        set { if (notice != value) { notice = value; Invalidate(); } }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x80;                     // WS_EX_TOOLWINDOW: hidden from Alt+Tab
            if (clickThrough) cp.ExStyle |= 0x20;   // WS_EX_TRANSPARENT: mouse passes through
            return cp;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Fallback != null) Fallback.Inject((int)(keyData & Keys.KeyCode), true);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (Fallback != null) Fallback.Inject(e.KeyValue, false);
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        string text = notice ?? hint;
        if (notice == null && dots > 0) text = new string('●', dots) + (hint == null ? "" : "\n" + hint);
        if (string.IsNullOrEmpty(text)) return;
        using (Font font = new Font("Segoe UI", Height / 60f, GraphicsUnit.Pixel))
        using (Brush brush = new SolidBrush(ForeColor))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Far;
            e.Graphics.DrawString(text, font, brush, new RectangleF(0, 0, Width, hintBottom), format);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

// One lock session: created to lock, raises Finished after unlocking.
sealed class Locker
{
    public event Action Finished;

    readonly List<Overlay> overlays = new List<Overlay>();
    readonly List<Rectangle> bounds = new List<Rectangle>();
    readonly List<int> typed = new List<int>();
    readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer guardTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer timeoutTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer noticeTimer = new System.Windows.Forms.Timer();
    readonly KeyHook keyHook;
    readonly DeviceGuard devices;
    readonly Native.HookProc mouseProc; // field keeps the delegate alive
    readonly IntPtr mouseHook;
    readonly SynchronizationContext ui;
    Overlay main;
    double opacity;
    bool unlocking;
    volatile int heartbeat;
    volatile bool done;

    public Locker(int timeoutSeconds)
    {
        keyHook = new KeyHook(OnKey);
        BuildOverlays();
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        if (Settings.Harden) Policies.Apply();

        // ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
        uint awake = (Settings.KeepAwake ? 1u : 0u) | (Settings.KeepDisplayOn ? 2u : 0u);
        if (awake != 0) Native.SetThreadExecutionState(0x80000000 | awake);

        if (Settings.BlockMouseMove || Settings.BlockMouseButtons)
        {
            mouseProc = MouseProc;
            mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL
        }

        fadeTimer.Interval = 15;
        fadeTimer.Tick += delegate { Fade(); };
        fadeTimer.Start();

        guardTimer.Interval = 500;
        guardTimer.Tick += delegate { Guard(); };
        guardTimer.Start();

        noticeTimer.Interval = 1200;
        noticeTimer.Tick += delegate { noticeTimer.Stop(); main.Notice = null; };

        if (Settings.BlockDevices) devices = new DeviceGuard();

        if (timeoutSeconds > 0)
        {
            timeoutTimer.Interval = timeoutSeconds * 1000;
            timeoutTimer.Tick += delegate { Unlock(); };
            timeoutTimer.Start();
        }

        Thread watchdog = new Thread(Watchdog);
        watchdog.IsBackground = true;
        watchdog.Start();
    }

    // One overlay per screen. Called again when monitors are added, removed or
    // resized while locked, so no screen is ever left uncovered.
    void BuildOverlays()
    {
        foreach (Overlay old in overlays) old.Dispose();
        overlays.Clear();
        bounds.Clear();
        main = null;
        foreach (Screen screen in Screen.AllScreens)
        {
            string hint = screen.Primary && Settings.ShowHint ? "enter the right combination" : null;
            Overlay overlay = new Overlay(screen, hint, !Settings.BlockMouseButtons, opacity);
            overlay.Fallback = keyHook;
            overlays.Add(overlay);
            bounds.Add(screen.Bounds);
            if (screen.Primary) main = overlay;
            overlay.Show();
        }
        if (main == null) main = overlays[0];
        main.Dots = Settings.ShowDots ? typed.Count : 0;
        main.Activate();
    }

    // Hook callbacks run on the UI thread and must return fast: Windows drops
    // slow low-level hooks. So the hash check runs on a worker thread.
    bool OnKey(int vk, int mods, bool pressed)
    {
        if (Settings.AllowMediaKeys && vk >= 0xAD && vk <= 0xB3) return false; // volume, media
        if (pressed)
        {
            if (Combo.Feed(typed, vk, mods))
            {
                List<int> attempt = new List<int>(typed);
                typed.Clear();
                Verify(attempt);
            }
            if (vk == 0x1B) // Esc: confirm that the typed keys are gone
            {
                main.Notice = "cleared";
                noticeTimer.Stop();
                noticeTimer.Start();
            }
            if (Settings.ShowDots) main.Dots = typed.Count;
        }
        return true;
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            bool isMove = wParam.ToInt32() == 0x0200; // WM_MOUSEMOVE
            if (isMove ? Settings.BlockMouseMove : Settings.BlockMouseButtons) return (IntPtr)1;
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void Verify(List<int> attempt)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            bool ok = Secret.Check(attempt);
            // A wrong attempt shows nothing at all: no flash, no message. A
            // stranger cannot even tell whether the keyboard is being read.
            if (ok) ui.Post(delegate { Unlock(); }, null);
        });
    }

    void Fade()
    {
        double target = unlocking ? 0 : Settings.Dim / 100.0;
        opacity = unlocking ? Math.Max(target, opacity - 0.06) : Math.Min(target, opacity + 0.03);
        foreach (Overlay overlay in overlays) overlay.Opacity = opacity;
        if (opacity != target) return;
        fadeTimer.Stop();
        if (!unlocking) return;

        done = true;
        foreach (Overlay overlay in overlays) overlay.Dispose();
        fadeTimer.Dispose();
        guardTimer.Dispose();
        timeoutTimer.Dispose();
        noticeTimer.Dispose();
        if (Finished != null) Finished();
    }

    // Keep overlays covering their screens, on top, and holding keyboard focus.
    void Guard()
    {
        heartbeat++;

        Screen[] screens = Screen.AllScreens;
        bool changed = screens.Length != overlays.Count;
        for (int i = 0; !changed && i < screens.Length; i++) changed = screens[i].Bounds != bounds[i];
        if (changed) BuildOverlays();

        for (int i = 0; i < overlays.Count; i++)
        {
            Rectangle r = bounds[i];
            Native.SetWindowPos(overlays[i].Handle, new IntPtr(-1), r.X, r.Y, r.Width, r.Height, 0x0010); // HWND_TOPMOST, SWP_NOACTIVATE
        }
        // Not when the mouse is allowed: stealing focus would break clicking.
        if (Settings.BlockMouseButtons) Native.SetForegroundWindow(main.Handle);
    }

    // If the UI thread ever hangs, nobody could type the combination and the
    // only way out would be a hard reboot. Instead: give up and unlock.
    // Counts missed beats rather than clock time, so PC sleep cannot trip it.
    void Watchdog()
    {
        int seen = heartbeat;
        int misses = 0;
        while (!done)
        {
            Thread.Sleep(2000);
            if (heartbeat != seen) { seen = heartbeat; misses = 0; }
            else if (++misses >= 8 && !done)
            {
                try { Policies.Restore(); } catch { }
                Process.GetCurrentProcess().Kill();
            }
        }
    }

    public void DevicesChanged()
    {
        if (devices != null) devices.Poke();
    }

    public void Unlock()
    {
        if (unlocking) return;
        unlocking = true;
        if (devices != null) devices.Stop();
        keyHook.Dispose();
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        Policies.Restore();
        Native.SetThreadExecutionState(0x80000000); // ES_CONTINUOUS: back to normal

        // Modifiers held when the lock began (e.g. the hotkey's Ctrl+Alt) had
        // their release swallowed, so Windows still thinks they are down.
        int[] modifiers = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
        foreach (int vk in modifiers)
        {
            if ((Native.GetAsyncKeyState(vk) & 0x8000) != 0)
                Native.keybd_event((byte)vk, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
        }

        guardTimer.Stop();
        timeoutTimer.Stop();
        fadeTimer.Start();
    }
}

// The background part: tray icon, lock hotkey, idle auto-lock, USB key.
sealed class Agent : ApplicationContext
{
    sealed class MessageWindow : NativeWindow
    {
        readonly Agent agent;

        public MessageWindow(Agent agent)
        {
            this.agent = agent;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) agent.Lock(0); // WM_HOTKEY
            else if (m.Msg == (int)Native.ShowSettingsMessage) agent.ShowSettings();
            else if (m.Msg == 0x0219) // WM_DEVICECHANGE: a drive arrived (0x8000) or left (0x8004)
            {
                int what = m.WParam.ToInt32();
                if (what == 0x8000 || what == 0x8004) agent.DriveChanged();
                // 0x0007 = DBT_DEVNODES_CHANGED: some device was added or removed
                if (what == 0x0007 && agent.locker != null) agent.locker.DevicesChanged();
            }
            base.WndProc(ref m);
        }
    }

    readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer usbTimer = new System.Windows.Forms.Timer();
    readonly MessageWindow window;
    internal Locker locker;
    SettingsForm settingsForm;
    bool usbPresent;
    int lastUnlockTick = Environment.TickCount;

    public Agent(bool background)
    {
        Settings.Load();
        window = new MessageWindow(this);

        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("Lock now", null, delegate { Lock(0); });
        menu.Items.Add("Settings...", null, delegate { ShowSettings(); });
        menu.Items.Add("Exit", null, delegate { tray.Visible = false; ExitThread(); });
        tray.Icon = SystemIcons.Shield;
        tray.Text = "Lockdown";
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowSettings(); };
        tray.Visible = true;

        idleTimer.Interval = 15000;
        idleTimer.Tick += delegate { CheckIdle(); };
        usbTimer.Interval = 3000;
        usbTimer.Tick += delegate { CheckUsb(); if (locker == null) usbTimer.Stop(); };
        ApplySettings();
        usbPresent = UsbKey.Present();

        if (!background) ShowSettings();
        else if (Settings.LockOnStart && !(Settings.UsbUnlockActive && usbPresent)) Lock(0);

        // Hand startup memory back to Windows; the idle agent needs very little.
        Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
    }

    public void ApplySettings()
    {
        Native.UnregisterHotKey(window.Handle, 1);
        if (Settings.HotkeyVk != 0) // 0x4000 = MOD_NOREPEAT
            Native.RegisterHotKey(window.Handle, 1, (uint)(Settings.HotkeyMods | 0x4000), (uint)Settings.HotkeyVk);
        idleTimer.Enabled = Settings.IdleMinutes > 0;
    }

    public void ShowSettings()
    {
        if (locker != null) return;
        if (settingsForm == null)
        {
            settingsForm = new SettingsForm(this);
            settingsForm.FormClosed += delegate { settingsForm = null; };
            settingsForm.Show();
        }
        settingsForm.Activate();
    }

    public void Lock(int timeoutSeconds)
    {
        if (locker != null) return;
        if (!Secret.Exists()) { ShowSettings(); return; }
        if (settingsForm != null) settingsForm.Close();
        tray.Visible = false; // nothing to click while locked
        usbPresent = UsbKey.Present();
        locker = new Locker(timeoutSeconds);
        locker.Finished += delegate
        {
            locker = null;
            lastUnlockTick = Environment.TickCount;
            tray.Visible = true;
        };
        // A just-inserted stick may not be readable yet when its arrival
        // message comes, so also poll while locked.
        if (Settings.UsbUnlockActive && UsbKey.Registered()) usbTimer.Start();
    }

    void DriveChanged()
    {
        CheckUsb();
        usbTimer.Start(); // one more look in 3 s, once the drive has settled
    }

    // Acts only when the key changes state: unlocks when it appears, locks when
    // it disappears. Locking by hotkey with the key plugged in stays locked.
    public void CheckUsb()
    {
        bool now = UsbKey.Present();
        if (now && !usbPresent && locker != null && Settings.UsbUnlockActive) locker.Unlock();
        if (!now && usbPresent && locker == null && Settings.UsbLock) { usbPresent = now; Lock(0); }
        usbPresent = now;
    }

    void CheckIdle()
    {
        if (locker != null) return;
        Native.LASTINPUTINFO info = new Native.LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
        if (!Native.GetLastInputInfo(ref info)) return;
        int now = Environment.TickCount;
        // Count from the last unlock too, or a long lock would re-lock at once.
        int idle = Math.Min(now - (int)info.dwTime, now - lastUnlockTick);
        if (idle >= Settings.IdleMinutes * 60000) Lock(0);
    }
}

sealed class SettingsForm : Form
{
    readonly Agent agent;
    readonly Label comboStatus = new Label();
    readonly Label usbStatus = new Label();
    readonly ComboBox usbDrive = new ComboBox();
    readonly CheckBox mouseMove, mouseButtons, media, awake, display, harden, blockDevices, hint, dots;
    readonly CheckBox usbUnlock, usbLock, autostart, lockOnStart;
    readonly TrackBar dim = new TrackBar();
    readonly TextBox hotkey = new TextBox();
    readonly NumericUpDown idle = new NumericUpDown();
    int hotkeyMods = Settings.HotkeyMods;
    int hotkeyVk = Settings.HotkeyVk;
    int y = 12;

    public SettingsForm(Agent agent)
    {
        this.agent = agent;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "Lockdown settings (1.0.1)";
        Icon = SystemIcons.Shield;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        Header("Combination");
        comboStatus.SetBounds(16, y + 5, 200, 18);
        Controls.Add(comboStatus);
        AddButton("Change combination...", 230, y, 150).Click += delegate
        {
            using (CaptureForm capture = new CaptureForm()) capture.ShowDialog(this);
            UpdateStatus();
        };
        y += 34;

        Header("While locked");
        mouseMove = Check("Lock mouse movement", Settings.BlockMouseMove);
        mouseButtons = Check("Lock mouse clicks and wheel (off = keyboard-only lock)", Settings.BlockMouseButtons);
        media = Check("Allow volume and media keys", Settings.AllowMediaKeys);
        awake = Check("Keep PC awake (no sleep)", Settings.KeepAwake);
        display = Check("Keep screens on", Settings.KeepDisplayOn);
        harden = Check("Disable Win+L, Task Manager, sign out and switch user", Settings.Harden);
        blockDevices = Check("Block newly plugged USB devices (turns USB key unlock off)", Settings.BlockDevices);
        hint = Check("Show hint text at the bottom", Settings.ShowHint);
        dots = Check("Show a dot for each key typed", Settings.ShowDots);
        Caption("Screen dimming", y + 6);
        dim.SetBounds(130, y, 250, 30);
        dim.AutoSize = false;
        dim.Minimum = 10;
        dim.Maximum = 90;
        dim.TickFrequency = 10;
        dim.Value = Settings.Dim;
        Controls.Add(dim);
        y += 38;

        Header("USB key");
        usbStatus.SetBounds(16, y, 364, 18);
        Controls.Add(usbStatus);
        y += 22;
        usbDrive.SetBounds(16, y + 2, 70, 21);
        usbDrive.DropDownStyle = ComboBoxStyle.DropDownList;
        usbDrive.DropDown += delegate { FillDrives(); };
        Controls.Add(usbDrive);
        FillDrives();
        AddButton("Make this drive my key", 96, y, 170).Click += delegate { RegisterUsb(); };
        AddButton("Forget key", 276, y, 104).Click += delegate { UsbKey.Forget(); agent.CheckUsb(); UpdateStatus(); };
        y += 34;
        usbUnlock = Check("Unlock when the USB key is plugged in", Settings.UsbUnlock);
        usbLock = Check("Lock when the USB key is pulled out", Settings.UsbLock);
        usbUnlock.Enabled = !blockDevices.Checked;
        blockDevices.CheckedChanged += delegate { usbUnlock.Enabled = !blockDevices.Checked; };
        y += 6;

        Header("Background");
        autostart = Check("Start with Windows (hidden in the tray)", Autostart.Enabled());
        lockOnStart = Check("Lock as soon as it starts with Windows", Settings.LockOnStart);
        Caption("Lock hotkey (Backspace = none)", y + 3);
        hotkey.SetBounds(230, y, 150, 20);
        hotkey.ReadOnly = true;
        hotkey.KeyDown += HotkeyKeyDown;
        Controls.Add(hotkey);
        ShowHotkey();
        y += 28;
        Caption("Auto-lock after idle minutes (0 = off)", y + 3);
        idle.SetBounds(230, y, 70, 20);
        idle.Maximum = 600;
        idle.Value = Math.Min(600, Settings.IdleMinutes);
        Controls.Add(idle);
        y += 38;

        AddButton("Save", 16, y, 80).Click += delegate { Save(); };
        AddButton("Lock now", 104, y, 86).Click += delegate { if (Save()) agent.Lock(0); };
        AddButton("Test lock (15 s)", 198, y, 104).Click += delegate { if (Save()) agent.Lock(15); };
        AddButton("Close", 310, y, 70).Click += delegate { Close(); };
        y += 38;

        UpdateStatus();
        ClientSize = new Size(396, y);
        ResumeLayout(false);
        PerformLayout();
    }

    void Header(string text)
    {
        Label label = new Label();
        label.Text = text;
        label.Font = new Font(Font, FontStyle.Bold);
        label.SetBounds(12, y, 370, 18);
        Controls.Add(label);
        y += 22;
    }

    void Caption(string text, int top)
    {
        Label label = new Label();
        label.Text = text;
        label.SetBounds(16, top, 210, 18);
        Controls.Add(label);
    }

    CheckBox Check(string text, bool value)
    {
        CheckBox box = new CheckBox();
        box.Text = text;
        box.Checked = value;
        box.SetBounds(16, y, 370, 20);
        Controls.Add(box);
        y += 22;
        return box;
    }

    Button AddButton(string text, int x, int top, int width)
    {
        Button button = new Button();
        button.Text = text;
        button.SetBounds(x, top, width, 26);
        Controls.Add(button);
        return button;
    }

    void UpdateStatus()
    {
        comboStatus.Text = Secret.Exists() ? "Combination is set." : "No combination yet.";
        if (!UsbKey.Registered()) usbStatus.Text = "No USB key registered. Plug in a USB stick and pick its drive.";
        else usbStatus.Text = UsbKey.Present() ? "USB key registered and plugged in." : "USB key registered, not plugged in.";
    }

    void FillDrives()
    {
        object selected = usbDrive.SelectedItem;
        usbDrive.Items.Clear();
        foreach (string root in UsbKey.Drives()) usbDrive.Items.Add(root);
        if (selected != null && usbDrive.Items.Contains(selected)) usbDrive.SelectedItem = selected;
        else if (usbDrive.Items.Count > 0) usbDrive.SelectedIndex = 0;
    }

    void RegisterUsb()
    {
        string root = usbDrive.SelectedItem as string;
        if (root == null)
        {
            MessageBox.Show(this, "No USB stick found. Plug one in, then open the drive list.", "Lockdown");
            return;
        }
        try { UsbKey.Register(root); }
        catch (IOException error) { MessageBox.Show(this, "Could not write to " + root + "\n" + error.Message, "Lockdown"); }
        catch (UnauthorizedAccessException error) { MessageBox.Show(this, "Could not write to " + root + "\n" + error.Message, "Lockdown"); }
        agent.CheckUsb();
        UpdateStatus();
    }

    void ShowHotkey()
    {
        if (hotkeyVk == 0) { hotkey.Text = "None"; return; }
        hotkey.Text = ((hotkeyMods & 2) != 0 ? "Ctrl + " : "") + ((hotkeyMods & 1) != 0 ? "Alt + " : "")
            + ((hotkeyMods & 4) != 0 ? "Shift + " : "") + (Keys)hotkeyVk;
    }

    void HotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        Keys key = e.KeyCode;
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu) return;
        if (key == Keys.Back || key == Keys.Delete) hotkeyVk = 0;
        else if (e.Control || e.Alt) // a bare key as a global hotkey would eat normal typing
        {
            hotkeyMods = (e.Alt ? 1 : 0) | (e.Control ? 2 : 0) | (e.Shift ? 4 : 0);
            hotkeyVk = (int)key;
        }
        ShowHotkey();
    }

    bool Save()
    {
        Settings.BlockMouseMove = mouseMove.Checked;
        Settings.BlockMouseButtons = mouseButtons.Checked;
        Settings.AllowMediaKeys = media.Checked;
        Settings.KeepAwake = awake.Checked;
        Settings.KeepDisplayOn = display.Checked;
        Settings.Harden = harden.Checked;
        Settings.ShowHint = hint.Checked;
        Settings.ShowDots = dots.Checked;
        Settings.LockOnStart = lockOnStart.Checked;
        Settings.UsbUnlock = usbUnlock.Checked;
        Settings.UsbLock = usbLock.Checked;
        Settings.BlockDevices = blockDevices.Checked;
        Settings.Dim = dim.Value;
        Settings.IdleMinutes = (int)idle.Value;
        Settings.HotkeyMods = hotkeyMods;
        Settings.HotkeyVk = hotkeyVk;
        Settings.Save();
        agent.ApplySettings();

        if (autostart.Checked != Autostart.Enabled() && !Autostart.Set(autostart.Checked))
        {
            MessageBox.Show(this, "Could not change the startup task.", "Lockdown");
            return false;
        }
        if (!Secret.Exists())
        {
            MessageBox.Show(this, "Set a combination first.", "Lockdown");
            return false;
        }
        return true;
    }
}

// Records a new combination. Uses the same key handling as the lock screen,
// so what is recorded here is exactly what the lock screen will see.
sealed class CaptureForm : Form
{
    readonly Label prompt = new Label();
    readonly Label dots = new Label();
    readonly List<int> keys = new List<int>();
    readonly KeyHook hook;
    List<int> first;

    public CaptureForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        Text = "New combination";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 180);
        KeyPreview = true;

        prompt.SetBounds(16, 14, 368, 84);
        dots.SetBounds(16, 100, 368, 30);
        dots.Font = new Font(Font.FontFamily, 14);
        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.TabStop = false;
        cancel.SetBounds(294, 140, 90, 26);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        Controls.Add(prompt);
        Controls.Add(dots);
        Controls.Add(cancel);
        ResumeLayout(false);

        ShowPrompt("Type the new combination, then press Enter.");
        hook = new KeyHook(OnKey);
        hook.Enabled = false; // only while this window is active
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        hook.Enabled = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        hook.Enabled = false;
        hook.Reset();
    }

    // Keys only arrive here if the hook did not take them. Handling them the
    // same way means Enter can never fall through to the Cancel button.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        hook.Inject((int)(keyData & Keys.KeyCode), true);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        hook.Inject(e.KeyValue, false);
        e.Handled = true;
    }

    void ShowPrompt(string text)
    {
        prompt.Text = text + "\n\nAt least " + Combo.MinLength + " key presses. Holding Shift, Ctrl, Alt or Win "
            + "makes a different step: K, Shift+K and Ctrl+Alt+K all differ.\nEsc clears, Backspace removes one.";
        ShowDots();
    }

    void ShowDots()
    {
        dots.Text = keys.Count == 0 ? "" : new string('●', keys.Count) + "  (" + keys.Count + ")";
    }

    bool OnKey(int vk, int mods, bool pressed)
    {
        if (pressed)
        {
            if (Combo.Feed(keys, vk, mods))
            {
                // Out of the hook callback: saving hashes for a while, and
                // Windows drops hooks that answer slowly.
                List<int> entry = new List<int>(keys);
                keys.Clear();
                BeginInvoke((MethodInvoker)delegate { Submit(entry); });
            }
            ShowDots();
        }
        return true;
    }

    void Submit(List<int> entry)
    {
        if (entry.Count < Combo.MinLength)
        {
            first = null;
            ShowPrompt("Too short: " + entry.Count + " of at least " + Combo.MinLength + " key presses. Start again.");
        }
        else if (first == null)
        {
            first = entry;
            ShowPrompt("Got " + entry.Count + " key presses. Now repeat it, then press Enter.");
        }
        else if (Same(first, entry))
        {
            Secret.Save(entry);
            DialogResult = DialogResult.OK;
        }
        else
        {
            string why = first.Count != entry.Count
                ? "first time " + first.Count + " key presses, second time " + entry.Count
                : "same length, different keys";
            first = null;
            ShowPrompt("Did not match (" + why + "). Start again.");
        }
    }

    static bool Same(List<int> a, List<int> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && hook != null) hook.Dispose();
        base.Dispose(disposing);
    }
}

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    public static readonly uint ShowSettingsMessage = RegisterWindowMessage(Program.Id + ".ShowSettings");

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetVolumeInformation(string root, StringBuilder name, int nameSize,
        out uint serial, out uint maxComponentLength, out uint flags, StringBuilder fileSystem, int fileSystemSize);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    // Device tree (cfgmgr32). All return 0 (CR_SUCCESS) on success.
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Query_And_Remove_SubTreeW(uint node, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Request_Device_EjectW(uint node, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Setup_DevNode(uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Reenumerate_DevNode(uint node, uint flags);

    // Privileges are present in an admin token but switched off by default.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValue(string system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static void EnablePrivilege(string name)
    {
        IntPtr token;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x8, out token)) return; // ADJUST | QUERY
        TOKEN_PRIVILEGES state = new TOKEN_PRIVILEGES();
        state.Count = 1;
        state.Attributes = 2; // SE_PRIVILEGE_ENABLED
        if (LookupPrivilegeValue(null, name, out state.Luid)) AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        CloseHandle(token);
    }
}
