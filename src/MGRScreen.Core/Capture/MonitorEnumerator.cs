using System.Runtime.InteropServices;

namespace MGRScreen.Core.Capture;

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int Width, int Height, bool IsPrimary);

/// <summary>
/// Lista os monitores conectados — usado apenas para exibir informação (quantos monitores,
/// resolução) na UI do Host. A seleção efetiva de qual tela compartilhar acontece através do
/// picker nativo do Windows (<see cref="ScreenCaptureService"/> usa GraphicsCapturePicker), que
/// é a forma suportada e mais simples de deixar o usuário escolher monitor/janela sem termos
/// que reimplementar a lógica de CreateForMonitor via COM interop nós mesmos.
/// </summary>
public static class MonitorEnumerator
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    private const uint MONITORINFOF_PRIMARY = 0x1;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                result.Add(new MonitorInfo(
                    hMonitor,
                    info.DeviceName,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top,
                    (info.Flags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }
}
