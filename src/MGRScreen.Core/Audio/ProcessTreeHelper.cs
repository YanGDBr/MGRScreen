using System.Runtime.InteropServices;

namespace MGRScreen.Core.Audio;

/// <summary>
/// Mapa de "quem é filho de quem" entre os processos. Necessário porque a captura por processo
/// pega a <b>árvore</b> inteira: se capturássemos o processo pai de um navegador e também um dos
/// seus processos filhos, o áudio do filho entraria duas vezes (dobrando o volume e distorcendo).
/// </summary>
internal static class ProcessTreeHelper
{
    public static Dictionary<uint, uint> BuildParentMap()
    {
        var map = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);

        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return map;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };

            if (Process32FirstW(snapshot, ref entry))
            {
                do
                {
                    map[entry.ProcessId] = entry.ParentProcessId;
                }
                while (Process32NextW(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    /// <summary>True se <paramref name="pid"/> é o próprio, ou descendente, de algum PID do conjunto.</summary>
    public static bool HasAncestorIn(uint pid, IReadOnlySet<uint> ancestors, Dictionary<uint, uint> parentMap)
    {
        var current = pid;
        var guard = 0;

        while (current != 0 && guard++ < 64)
        {
            if (ancestors.Contains(current))
            {
                return true;
            }

            if (!parentMap.TryGetValue(current, out var parent) || parent == current)
            {
                return false;
            }

            current = parent;
        }

        return false;
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
