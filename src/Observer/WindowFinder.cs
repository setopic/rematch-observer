using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RematchObserver;

public readonly record struct WindowInfo(nint Handle, string Title, string ProcessName, int ProcessId, int Width, int Height)
{
    public override string ToString() => $"{ProcessName} ({ProcessId})  {Width}x{Height}  {Title}";
}

/// <summary>
/// 撮る相手を 1 つに決める。
///
/// ⚠ **デスクトップ全体は撮らない**（ADR-0067）。撮れるのはウィンドウだけで、
/// そのウィンドウを間違えないための道具がここにある。
/// </summary>
public static partial class WindowFinder
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc callback, nint param);
    private delegate bool EnumWindowsProc(nint hwnd, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] text, int count);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>見えているウィンドウを並べる。**選ぶのは人か設定である。**</summary>
    public static List<WindowInfo> List()
    {
        var found = new List<WindowInfo>();
        nint shell = GetShellWindow();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !IsWindowVisible(hwnd)) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var buf = new char[len + 1];
            int n = GetWindowText(hwnd, buf, buf.Length);
            if (n == 0) return true;
            if (!GetClientRect(hwnd, out var r)) return true;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { return true; }
            found.Add(new WindowInfo(hwnd, new string(buf, 0, n), proc, (int)pid, w, h));
            return true;
        }, 0);
        return found;
    }

    /// <summary>
    /// 設定に合う 1 枚を選ぶ。**プロセス名を先に見る。**
    /// タイトルは翻訳や配信ソフトの都合で変わるが、プロセス名は変わりにくい。
    /// </summary>
    public static WindowInfo? Find(string? processName, string? titleContains)
    {
        var all = List();
        IEnumerable<WindowInfo> q = all;
        if (!string.IsNullOrWhiteSpace(processName))
            q = q.Where(w => w.ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(titleContains))
            q = q.Where(w => w.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));
        // 同じプロセスに複数あるなら、いちばん大きいものがゲーム本体である見込みが高い
        return q.OrderByDescending(w => (long)w.Width * w.Height).Cast<WindowInfo?>().FirstOrDefault();
    }

    public static string Describe(IEnumerable<WindowInfo> windows)
    {
        var sb = new StringBuilder();
        foreach (var w in windows) sb.AppendLine("  " + w);
        return sb.Length == 0 ? "  （見つかりません）\n" : sb.ToString();
    }
}
