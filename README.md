# Lockdown

Locks a Windows PC's keyboard and mouse without locking the session.

Win+L signs you out of your own desktop: some tasks pause, and anyone can hammer
the password box until the account locks. Lockdown leaves everything running.
It dims the screens so you can still read them, swallows all input, keeps the PC
awake, and lets go only when you type the right key combination.

## What it does

- Dims every screen. The content stays visible underneath.
- Swallows keyboard and mouse input, including Alt+Tab, the Windows key and Alt+F4.
- Disables Win+L and strips Task Manager, Sign out and Switch user from the
  Ctrl+Alt+Del screen while locked. Everything is put back on unlock.
- Unlocks on a key combination: a sequence of key presses where held modifiers
  count, so `K`, `Shift+K` and `Ctrl+Alt+K` are three different steps.
  No lockout, no limit on tries. A wrong attempt shows nothing at all, so a
  stranger cannot tell whether the keyboard is even being read. To unlock:
  Esc to clear, type the combination, Enter.
- Optional USB key: a normal USB stick that unlocks when plugged in, and can
  lock the PC when pulled out. See below.
- Optional: stop USB devices that are plugged in while locked.
- Lives in the tray. Lock with a hotkey, after idle time, or at startup.

## Download

Get `Lockdown.exe` from the [latest release](https://github.com/Loseless02/Lockdown/releases/latest)
and run it. No installer, nothing else needed.

The exe is not code-signed, so Windows SmartScreen may warn on first run:
choose "More info", then "Run anyway". It also asks for administrator rights;
the reason is under Build below.

## USB key

Any ordinary USB stick can act as a key.

1. Plug the stick in and open the settings window.
2. Under "USB key", pick its drive and press "Make this drive my key".
   This writes one small hidden file, `lockdown.key`. Nothing else on the
   stick is touched.
3. Choose what it does:
   - "Unlock when the USB key is plugged in"
   - "Lock when the USB key is pulled out". With both on, the PC is usable
     only while the stick is in.

The combination keeps working, so a lost stick does not lock you out. Lockdown
checks the file together with the stick's volume serial number, so copying the
file to another stick is not enough. "Forget key" unregisters it.

USB unlock is switched off while "Block newly plugged USB devices" is on,
since a blocked stick can never be read.

## Build

Needs nothing but Windows. The compiler ships with .NET Framework.

```
build.cmd
```

This produces `Lockdown.exe`, about 40 KB. It asks for administrator rights,
because the Win+L and Ctrl+Alt+Del policies are admin-only.

`test.cmd` builds and runs the automated checks. It uses its own registry key
and locks the screen for about three seconds.

## Limits

- Ctrl+Alt+Del itself cannot be blocked by any normal program. Lockdown empties
  that screen instead. The power button still works.
- The USB key is a convenience, not a security token. Someone who clones the
  whole stick gets in.
- Blocking USB devices covers the USB bus only, not Bluetooth or Thunderbolt.
- If the app crashes or hangs, it unlocks. That is deliberate: a broken lock
  should never cost you a reboot and your unsaved work.
- Settings live in `HKCU\Software\Lockdown`.
