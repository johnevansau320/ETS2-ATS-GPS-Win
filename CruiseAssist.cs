using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ets2Gps;

/// <summary>
/// Keeps the truck's cruise control on the current speed limit. The telemetry only reports the cruise setting, so the
/// setting is changed the way a driver does it: a tap of the game's "cruise control faster / slower" key. Those keys are
/// read from the player's own key bindings (see GameControls; Numpad + and Numpad - when the file cannot be found). Keys are only sent while Euro Truck Simulator 2 is the window in
/// front, so nothing ever types into another program, and only while cruise control is on.
/// </summary>
public sealed class CruiseAssist
{
    // the keys come from the player's bindings (scan codes: games read the keyboard that way)
    GameKey? keyFaster, keySlower;
    long lastRead = long.MinValue;
    string? shownProblem;

    /// <summary>The keys in use, e.g. "Numpad + / Numpad -", or "none".</summary>
    public string KeysText { get; private set; } = "";
    /// <summary>Set when the game has no keyboard key for faster / slower: the speed limit cannot be followed.</summary>
    public string? Problem { get; private set; }
    /// <summary>Raised once when the keys are missing (shown to the user).</summary>
    public event Action<string>? Notice;

    readonly Func<bool> enabled;
    float step = 1f;                 // km/h one key tap changes the setting by (learned from the first taps)
    long lastPress, limitSince;
    int lastDir, burst, stuck;
    float pressBefore, seenLimit;
    long pausedUntil;

    public CruiseAssist(Func<bool> enabled) { this.enabled = enabled; }

    /// <summary>Called with every telemetry update.</summary>
    public void Update(TruckState s, long nowMs)
    {
        if (!enabled() || !s.Connected || s.Paused || !s.CruiseOn || s.SpeedLimitKmh <= 0) { lastDir = 0; burst = 0; seenLimit = 0; return; }
        ReadKeys(nowMs);
        if (Problem != null) { if (shownProblem != Problem) { shownProblem = Problem; Notice?.Invoke(Problem); } return; }
        if (nowMs < pausedUntil || nowMs - lastPress < 280) return;
        // a changed limit must hold for a moment before the cruise control follows it (the reading flickers at junctions)
        if (s.SpeedLimitKmh != (int)seenLimit) { seenLimit = s.SpeedLimitKmh; limitSince = nowMs; burst = 0; stuck = 0; return; }
        if (nowMs - limitSince < 1500) return;
        // what did the last tap do?
        if (lastDir != 0)
        {
            float d = MathF.Abs(s.CruiseKmh - pressBefore);
            if (d >= 0.5f && d <= 12f) { step = d; stuck = 0; }
            else if (d < 0.5f && ++stuck >= 2) { pausedUntil = nowMs + 10000; stuck = 0; lastDir = 0; return; }   // the key did nothing (other binding, or at the top speed)
            lastDir = 0;
        }
        float diff = s.SpeedLimitKmh - s.CruiseKmh;
        if (MathF.Abs(diff) < MathF.Max(0.6f, step * 0.5f)) { burst = 0; return; }
        if (burst >= 60) { pausedUntil = nowMs + 10000; burst = 0; return; }
        if (!GameInFront()) return;
        int dir = diff > 0 ? 1 : -1;
        Tap(dir > 0 ? keyFaster!.Value : keySlower!.Value, dir > 0 ? "+" : "-");
        lastDir = dir; lastPress = nowMs; pressBefore = s.CruiseKmh; burst++;
    }

    /// <summary>Reads the bindings now and then, so a change made in the game's controls (or another profile) is picked up while the app runs.</summary>
    void ReadKeys(long nowMs)
    {
        if (nowMs - lastRead < 20000 && lastRead != long.MinValue) return;
        lastRead = nowMs;
        var r = GameControls.Read();
        keyFaster = r.Faster; keySlower = r.Slower;
        Problem = r.Problem;
        KeysText = r.Describe();
        if (Problem == null) shownProblem = null;
    }

    // ------------------------------------------------------------------------ native --

    static readonly string? DryRun = Environment.GetEnvironmentVariable("ETS2_CRUISE_DRYRUN");   // test only: log the taps to this file instead of pressing keys

    /// <summary>Is the game the window in front (key presses go to the window in front)?</summary>
    internal static bool GameInFront()
    {
        if (DryRun != null) return true;
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return Games.All.Any(g => p.ProcessName.Equals(g.Process, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>Presses and releases a key of the game (tag: what for, in the test log).</summary>
    internal static void Tap(GameKey key, string tag)
    {
        if (DryRun != null) { File.AppendAllText(DryRun, tag + " " + key.Name + " 0x" + key.Scan.ToString("X2") + (key.Extended ? "e" : "") + " " + Environment.TickCount64 + Environment.NewLine); return; }
        uint ext = key.Extended ? 0x0001u : 0u;                                                                  // KEYEVENTF_EXTENDEDKEY
        var down = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = key.Scan, dwFlags = 0x0008 | ext } };      // KEYEVENTF_SCANCODE
        var up = new INPUT { type = 1, ki = new KEYBDINPUT { wScan = key.Scan, dwFlags = 0x0008 | 0x0002 | ext } };   // ... | KEYEVENTF_KEYUP
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(45);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] MOUSEINPUT mi;   // makes the union as big as the largest member, which SendInput checks
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
