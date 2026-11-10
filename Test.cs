// Automated checks. Built by test.cmd into a separate exe that uses its own
// registry key (Software\LockdownTest), so real settings are never touched.
// The lock test dims the screens and blocks input for about 3 seconds.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Test
{
    static int failures;

    static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (!ok) failures++;
    }

    static void Pump(int milliseconds)
    {
        int end = Environment.TickCount + milliseconds;
        while (Environment.TickCount < end) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static void Type(KeyHook hook, params int[] keys)
    {
        foreach (int vk in keys) { hook.Inject(vk, true); hook.Inject(vk, false); }
    }

    static KeyHook HookOf(object owner, string field)
    {
        return (KeyHook)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);
    }

    [STAThread]
    static int Main()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Program.AppKey, false);
        const int A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, Enter = 0x0D, Shift = 0xA0;

        // Key handling
        List<int> got = new List<int>();
        int enters = 0;
        KeyHook hook = new KeyHook((vk, mods, pressed) => { if (pressed && Combo.Feed(got, vk, mods)) enters++; return true; });
        hook.Enabled = false; // do not swallow real typing during the test
        hook.Inject(A, true); hook.Inject(A, true); hook.Inject(A, true); hook.Inject(A, false);
        Check(got.Count == 1, "held key (auto-repeat) counts once");
        hook.Inject(Shift, true); Type(hook, A); hook.Inject(Shift, false); Type(hook, A);
        Check(got.Count == 3 && got[1] == (A | (1 << 16)) && got[2] == A, "Shift+A differs from A");
        Type(hook, 0x08);
        Check(got.Count == 2, "Backspace removes one");
        Type(hook, 0x1B);
        Check(got.Count == 0, "Esc clears");
        Type(hook, Enter);
        Check(enters == 1, "Enter submits");
        hook.Dispose();

        // Hash round trip
        List<int> combo = new List<int> { A, B, C, D, E, F };
        Secret.Save(combo);
        Check(Secret.Check(combo), "saved combination verifies");
        Check(!Secret.Check(new List<int> { A, B, C, D, E, E }), "wrong combination rejected");
        Registry.CurrentUser.DeleteSubKeyTree(Program.AppKey, false);

        // Capture dialog
        CaptureForm capture = new CaptureForm();
        capture.Show();
        Pump(200);
        KeyHook captureHook = HookOf(capture, "hook");
        Type(captureHook, A, B, C, D, Enter);
        Pump(200);
        Check(!Secret.Exists(), "capture: too short is refused");
        Type(captureHook, A, B, C, D, E, F, Enter);
        Pump(200);
        Type(captureHook, A, B, C, D, E, A, Enter);
        Pump(500);
        Check(!Secret.Exists(), "capture: mismatch is refused");
        // First pass with a key held long enough to auto-repeat, second pass normal.
        Type(captureHook, A, B, C);
        captureHook.Inject(D, true); captureHook.Inject(D, true); captureHook.Inject(D, true); captureHook.Inject(D, false);
        Type(captureHook, E, F, Enter);
        Pump(200);
        Type(captureHook, A, B, C, D, E, F, Enter);
        Pump(800);
        Check(Secret.Exists() && Secret.Check(combo), "capture: type twice saves the combination");
        Check(capture.DialogResult == DialogResult.OK, "capture: dialog finishes with OK");
        capture.Dispose();

        // USB key (no stick registered in the test key)
        Check(!UsbKey.Registered() && !UsbKey.Present(), "usb: nothing registered means not present");

        // Device list (read only; nothing is stopped here)
        Check(DeviceGuard.Present().Count > 0, "devices: connected USB devices can be listed");

        // Real lock, about 3 seconds
        Settings.Harden = false;
        bool finished = false;
        Locker locker = new Locker(10);
        locker.Finished += () => finished = true;
        Pump(700);
        KeyHook lockHook = HookOf(locker, "keyHook");
        Type(lockHook, A, B, C, D, E, E, Enter);
        Pump(900);
        Check(!finished, "lock: wrong combination stays locked");
        Type(lockHook, A, B, 0x1B);
        Pump(100);
        Type(lockHook, A, B, C, D, E, F, Enter);
        Pump(1500);
        Check(finished, "lock: right combination unlocks");

        Registry.CurrentUser.DeleteSubKeyTree(Program.AppKey, false);
        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures;
    }
}
