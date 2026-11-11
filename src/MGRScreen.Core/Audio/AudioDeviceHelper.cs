using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MGRScreen.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault);

public sealed record AudioAppInfo(uint ProcessId, string Name);

/// <summary>
/// Lista dispositivos de reprodução (render) disponíveis para loopback capture. O usuário
/// escolhe aqui "CABLE Input (VB-Audio Virtual Cable)" se quiser excluir o Discord do stream
/// (ver docs/ARCHITECTURE.md, seção 3), ou o dispositivo de saída padrão normal caso contrário.
/// </summary>
public static class AudioDeviceHelper
{
    public static IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        return devices
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, d.ID == defaultDevice.ID))
            .ToList();
    }

    /// <summary>O enumerator NÃO é descartado de propósito: o MMDevice retornado continua sendo
    /// usado pela captura depois que este método retorna, e descartar o enumerator antes disso
    /// é um jeito fácil de acabar com um dispositivo inválido (e áudio mudo) na mão.</summary>
    public static MMDevice GetDeviceById(string id)
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }

    public static MMDevice GetDefaultRenderDevice()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>
    /// Apps que estão com sessão de áudio ativa (ou seja, que podem estar tocando algo). Serve
    /// pro modo "compartilhar só o áudio deste app".
    /// </summary>
    public static IReadOnlyList<AudioAppInfo> GetAppsWithAudio()
    {
        var result = new Dictionary<uint, AudioAppInfo>();
        var ownPid = (uint)Environment.ProcessId;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                if (sessions is null)
                {
                    continue;
                }

                for (var i = 0; i < sessions.Count; i++)
                {
                    var pid = sessions[i].GetProcessID;
                    if (pid == 0 || pid == ownPid || result.ContainsKey(pid))
                    {
                        continue;
                    }

                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                        result[pid] = new AudioAppInfo(pid, process.ProcessName);
                    }
                    catch (ArgumentException)
                    {
                        // processo já morreu entre a enumeração e a consulta
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }

        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
