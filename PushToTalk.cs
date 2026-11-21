using System.Runtime.InteropServices;

namespace Ets2Gps;

/// <summary>
/// The push-to-talk button for voice control: a keyboard key, a mouse side button, or a button on a steering wheel, joystick or button box.
/// The button is only watched (its state is read about 40 times a second), never pressed, blocked or changed, so the game still sees it too:
/// pick one that is not bound to anything in the game. No keyboard hook is installed.
/// </summary>
public sealed class PushToTalk : IDisposable
{
    /// <summary>After the button is let go, results still count for this long: the recognizer only reports a command once the speech has ended.</summary>
    public const int GraceMs = 2000;

    // a binding: "key:<virtual key>" or "joy:<button 1-32>:<device name>"
    string binding = "";
    volatile bool down;
    long lastDownMs = -1_000_000;
    readonly Thread thread;
    volatile bool stop;

    /// <summary>Raised on a background thread when the button goes down (true) or up (false).</summary>
    public event Action<bool>? Changed;

    public PushToTalk()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "push-to-talk" };
        thread.Start();
    }

    public string Binding { get => binding; set { binding = value ?? ""; down = false; } }
    public bool IsDown => down;

    /// <summary>Is the button held now, or was it let go less than <see cref="GraceMs"/> ago?</summary>
    public bool Open => down || Environment.TickCount64 - Interlocked.Read(ref lastDownMs) < GraceMs;

    void Run()
    {
        while (!stop)
        {
            bool now = binding.Length > 0 && IsPressed(binding);
            if (now) Interlocked.Exchange(ref lastDownMs, Environment.TickCount64);
            if (now != down) { down = now; try { Changed?.Invoke(now); } catch { } }
            Thread.Sleep(25);
        }
    }

    public void Dispose() => stop = true;

    // ------------------------------------------------------------------------------------------ reading the buttons

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, System.Text.StringBuilder s, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    struct JOYINFOEX { public int dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public int wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax, wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }
    [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int joyGetDevCapsW(IntPtr id, ref JOYCAPSW caps, int size);
    const int JOY_RETURNBUTTONS = 0x80, MaxJoy = 16;

    /// <summary>
    /// The buttons held on each game controller: id, hardware id ("346E:0024", vendor and product), name, button bits. Controllers that are not
    /// connected are left out. Windows gives many controllers the same name ("Microsoft PC-joystick driver"), so they are told apart by hardware id.
    /// </summary>
    static List<(int Id, string Hw, string Name, uint Buttons)> Controllers()
    {
        var list = new List<(int, string, string, uint)>();
        for (int id = 0; id < MaxJoy; id++)
        {
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(id, ref info) != 0) continue;
            var (hw, name) = Identify(id);
            list.Add((id, hw, name, (uint)info.dwButtons));
        }
        return list;
    }

    static readonly Dictionary<int, (string Hw, string Name)> idents = new();
    static (string Hw, string Name) Identify(int id)
    {
        lock (idents)
        {
            if (idents.TryGetValue(id, out var v)) return v;
            var caps = new JOYCAPSW();
            if (joyGetDevCapsW((IntPtr)id, ref caps, Marshal.SizeOf<JOYCAPSW>()) != 0) return idents[id] = ("", "Game controller " + (id + 1));
            string hw = $"{caps.wMid:X4}:{caps.wPid:X4}";
            string name = OemName(caps.wMid, caps.wPid) ?? (string.IsNullOrWhiteSpace(caps.szPname) ? "Game controller " + (id + 1) : caps.szPname.Trim());
            return idents[id] = (hw, name);
        }
    }

    /// <summary>The controller's own name as the "Set up USB game controllers" panel shows it ("SIMSONN Pro X Ultra"), from the registry.</summary>
    static string? OemName(ushort vid, ushort pid)
    {
        string key = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try { if (root.OpenSubKey(key)?.GetValue("OEMName") is string n && n.Trim().Length > 0) return n.Trim(); }
            catch { /* not readable */ }
        }
        return null;
    }

    // controller ids are re-read every few seconds, so a wheel plugged in later (or given another id) is found again
    static List<(int Id, string Hw, string Name, uint Buttons)> joyCache = new();
    static long joyCacheMs = -1_000_000;

    /// <summary>A controller binding: "joy:&lt;button&gt;:&lt;vendor&gt;:&lt;product&gt;:&lt;name&gt;" (1.13.0 test builds wrote "joy:&lt;button&gt;:&lt;name&gt;").</summary>
    static bool ParseJoy(string b, out int btn, out string hw, out string name)
    {
        btn = 0; hw = ""; name = "";
        var p = b.Split(':', 5);
        if (p.Length < 3 || p[0] != "joy" || !int.TryParse(p[1], out btn) || btn < 1 || btn > 32) return false;
        if (p.Length == 5) { hw = p[2] + ":" + p[3]; name = p[4]; } else name = p[2];
        return true;
    }

    static string JoyBinding(int btn, string hw, string name) => hw.Length > 0 ? $"joy:{btn}:{hw}:{name}" : $"joy:{btn}:{name}";

    static bool IsPressed(string b)
    {
        if (b.StartsWith("key:", StringComparison.Ordinal))
            return int.TryParse(b.AsSpan(4), out int vk) && (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (!ParseJoy(b, out int btn, out string hw, out string name)) return false;
        long now = Environment.TickCount64;
        if (now - joyCacheMs > 3000) { lock (idents) idents.Clear(); joyCache = Controllers(); joyCacheMs = now; }
        bool pressed = false;
        foreach (var c in joyCache)
        {
            if (hw.Length > 0 ? c.Hw != hw : c.Name != name) continue;
            var info = new JOYINFOEX { dwSize = Marshal.SizeOf<JOYINFOEX>(), dwFlags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(c.Id, ref info) != 0) { joyCacheMs = -1_000_000; continue; }
            pressed |= ((uint)info.dwButtons & (1u << (btn - 1))) != 0;   // two identical controllers: either one
        }
        return pressed;
    }

    /// <summary>What the binding is called on screen ("F9", "Mouse button 4", "SIMSONN Pro X Ultra button 20").</summary>
    public static string Describe(string b)
    {
        if (b.StartsWith("key:", StringComparison.Ordinal) && int.TryParse(b.AsSpan(4), out int vk)) return KeyName(vk);
        if (ParseJoy(b, out int btn, out _, out string name)) return $"{name} button {btn}";
        return "not set";
    }

    /// <summary>Test: the controllers Windows reports, one per line.</summary>
    public static string ListControllers() => string.Join(Environment.NewLine, Controllers().Select(c => $"id {c.Id}  {c.Hw}  {c.Name}  buttons held {c.Buttons:X8}"));

    static string KeyName(int vk)
    {
        switch (vk)
        {
            case 0x04: return "Middle mouse button";
            case 0x05: return "Mouse button 4";
            case 0x06: return "Mouse button 5";
            case >= 0x7C and <= 0x87: return "F" + (vk - 0x6F);   // F13 - F24 (macro keys) have no name in Windows
        }
        uint sc = MapVirtualKey((uint)vk, 0);
        bool ext = vk is (>= 0x21 and <= 0x2E) or 0x6F or 0x90 or 0xA3 or 0xA5 or 0x5B or 0x5C;   // Page Up..Delete, arrows, keypad /, Num Lock, right Ctrl / Alt, Windows keys
        var s = new System.Text.StringBuilder(64);
        if (sc != 0 && GetKeyNameText((int)(sc << 16) | (ext ? 1 << 24 : 0), s, s.Capacity) > 0) return s.ToString();
        return "Key " + vk;
    }

    /// <summary>
    /// Waits for the next key or controller button to be pressed and returns its binding, or null when Esc is pressed or nothing happens in time.
    /// The left and right mouse buttons are not offered (they are needed to click), nor plain Shift / Ctrl / Alt / Windows (they are used with other keys).
    /// </summary>
    public static async Task<string?> CaptureAsync(int timeoutMs = 10000)
    {
        static bool Skip(int vk) => vk is 0x01 or 0x02 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA4 or 0xFF;
        return await Task.Run(() =>
        {
            // whatever is held already (the mouse button that clicked "Set") must be let go first
            var held = new HashSet<string>();
            for (int vk = 1; vk < 256; vk++) if ((GetAsyncKeyState(vk) & 0x8000) != 0) held.Add("key:" + vk);
            lock (idents) idents.Clear();
            foreach (var c in Controllers()) for (int i = 0; i < 32; i++) if ((c.Buttons & (1u << i)) != 0) held.Add(JoyBinding(i + 1, c.Hw, c.Name));
            long end = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < end)
            {
                Thread.Sleep(20);
                for (int vk = 1; vk < 256; vk++)
                {
                    if (Skip(vk)) continue;
                    string k = "key:" + vk;
                    bool on = (GetAsyncKeyState(vk) & 0x8000) != 0;
                    if (!on) { held.Remove(k); continue; }
                    if (held.Contains(k)) continue;
                    if (vk == 0x1B) return null;   // Esc cancels
                    return k;
                }
                foreach (var c in Controllers())
                    for (int i = 0; i < 32; i++)
                    {
                        string k = JoyBinding(i + 1, c.Hw, c.Name);
                        if ((c.Buttons & (1u << i)) == 0) { held.Remove(k); continue; }
                        if (!held.Contains(k)) return k;
                    }
            }
            return null;
        });
    }
}
