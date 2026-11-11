using System.Runtime.InteropServices;

namespace MGRScreen.Core.Audio;

/// <summary>
/// Captura o áudio do sistema <b>excluindo</b> (ou incluindo) uma árvore de processos, usando a
/// "Application Loopback API" do Windows (Windows 10 build 20348+ / Windows 11).
///
/// É isto que permite compartilhar o áudio do sistema SEM a voz do Discord: pedimos ao Windows o
/// mix de tudo, exceto a árvore de processos do Discord. Não precisa de cabo de áudio virtual
/// nem de configurar nada no Windows.
///
/// IMPORTANTE — apartamento COM: TODO o ciclo de vida dos objetos COM (ativação, Initialize,
/// GetService e o loop de leitura) acontece numa única thread MTA dedicada, criada aqui dentro.
/// Os objetos do WASAPI não são "agile": se forem criados na thread de UI (STA) e usados numa
/// thread de captura diferente, o QueryInterface entre apartamentos falha com E_NOINTERFACE e o
/// processo morre. Isso não aparece em teste de console (cuja thread principal já é MTA) — só no
/// app WPF de verdade.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    /// <summary>Blocos de PCM 16-bit (48 kHz, estéreo).</summary>
    public event Action<byte[]>? DataAvailable;

    /// <summary>Disparado se a captura morrer DEPOIS de ter iniciado — senão o áudio pararia em
    /// silêncio, sem ninguém ficar sabendo.</summary>
    public event Action<Exception>? Failed;

    public const int SampleRateHz = 48_000;
    public const int ChannelCount = 2;

    public float PeakLevel { get; private set; }

    private CancellationTokenSource? _cts;
    private Thread? _captureThread;

    /// <summary>
    /// Começa a capturar. Com <paramref name="exclude"/> = true, tudo que a árvore de processos
    /// de <paramref name="targetProcessId"/> tocar fica DE FORA da captura. Lança se a
    /// inicialização falhar (API indisponível, formato recusado, etc).
    /// </summary>
    public void Start(uint targetProcessId, bool exclude)
    {
        _cts = new CancellationTokenSource();

        var startup = new StartupState();
        var token = _cts.Token;

        _captureThread = new Thread(() => ThreadMain(targetProcessId, exclude, startup, token))
        {
            IsBackground = true,
            Name = "MGRScreen process loopback capture",
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();

        if (!startup.Ready.Wait(TimeSpan.FromSeconds(8)))
        {
            _cts.Cancel();
            throw new TimeoutException("O Windows não respondeu ao pedido de captura de áudio por processo.");
        }

        if (startup.Error is not null)
        {
            _cts.Cancel();
            throw startup.Error;
        }
    }

    private sealed class StartupState
    {
        public readonly ManualResetEventSlim Ready = new(false);
        public Exception? Error;
    }

    private void ThreadMain(uint targetProcessId, bool exclude, StartupState startup, CancellationToken token)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        var eventHandle = IntPtr.Zero;
        var signaled = false;

        try
        {
            audioClient = ActivateProcessLoopbackClient(targetProcessId, exclude);
            InitializeClient(audioClient);

            eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(eventHandle));

            var captureClientIid = IidAudioCaptureClient;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureClientIid, out var captureService));
            captureClient = (IAudioCaptureClient)captureService;

            Marshal.ThrowExceptionForHR(audioClient.Start());

            startup.Ready.Set();
            signaled = true;

            CaptureLoop(captureClient, eventHandle, token);
        }
        catch (Exception ex)
        {
            if (signaled)
            {
                // Já estava capturando: quem chamou precisa saber que o áudio parou.
                if (!token.IsCancellationRequested)
                {
                    Failed?.Invoke(ex);
                }
            }
            else
            {
                startup.Error = ex;
            }
        }
        finally
        {
            if (!signaled)
            {
                startup.Ready.Set();
            }

            try
            {
                audioClient?.Stop();
            }
            catch
            {
            }

            if (captureClient is not null)
            {
                Marshal.ReleaseComObject(captureClient);
            }

            if (audioClient is not null)
            {
                Marshal.ReleaseComObject(audioClient);
            }

            if (eventHandle != IntPtr.Zero)
            {
                CloseHandle(eventHandle);
            }

            PeakLevel = 0;
        }
    }

    private static IAudioClient ActivateProcessLoopbackClient(uint targetProcessId, bool exclude)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = targetProcessId,
            ProcessLoopbackMode = exclude ? LoopbackModeExcludeTargetProcessTree : LoopbackModeIncludeTargetProcessTree,
        };

        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());

        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var propVariant = new PropVariantBlob
            {
                VarType = VtBlob,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);

            var audioClientIid = IidAudioClient;
            var handler = new ActivationHandler();

            ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientIid,
                propVariantPtr,
                handler,
                out var operation);

            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("O Windows não concluiu a ativação da captura por processo.");
            }

            operation.GetActivateResult(out var activateResult, out var activatedInterface);
            Marshal.ThrowExceptionForHR(activateResult);

            return (IAudioClient)activatedInterface;
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void InitializeClient(IAudioClient audioClient)
    {
        // A "device" virtual de process loopback não tem mix format — o formato é escolhido por
        // nós. Pedimos direto PCM 16-bit, que é exatamente o que trafega na nossa rede.
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = ChannelCount,
            SamplesPerSec = SampleRateHz,
            BitsPerSample = 16,
            BlockAlign = (short)(ChannelCount * 16 / 8),
            AvgBytesPerSec = SampleRateHz * ChannelCount * 16 / 8,
            Size = 0,
        };

        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);

            Marshal.ThrowExceptionForHR(audioClient.Initialize(
                shareMode: 0, // AUDCLNT_SHAREMODE_SHARED
                streamFlags: AudclntStreamflagsLoopback | AudclntStreamflagsEventcallback,
                hnsBufferDuration: 2_000_000, // 200 ms
                hnsPeriodicity: 0,
                format: formatPtr,
                audioSessionGuid: IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }
    }

    private void CaptureLoop(IAudioCaptureClient captureClient, IntPtr eventHandle, CancellationToken token)
    {
        const int bytesPerFrame = ChannelCount * 2;

        while (!token.IsCancellationRequested)
        {
            if (WaitForSingleObject(eventHandle, 200) != 0)
            {
                continue; // timeout: só volta a esperar
            }

            while (!token.IsCancellationRequested)
            {
                if (captureClient.GetNextPacketSize(out var packetFrames) != 0 || packetFrames == 0)
                {
                    break;
                }

                if (captureClient.GetBuffer(out var dataPtr, out var framesAvailable, out var flags, out _, out _) != 0)
                {
                    break;
                }

                var byteCount = (int)framesAvailable * bytesPerFrame;
                var buffer = new byte[byteCount];

                if ((flags & BufferFlagsSilent) == 0 && dataPtr != IntPtr.Zero)
                {
                    Marshal.Copy(dataPtr, buffer, 0, byteCount);
                    PeakLevel = Pcm16Peak(buffer);
                }
                else
                {
                    PeakLevel = 0;
                }

                captureClient.ReleaseBuffer(framesAvailable);

                if (byteCount > 0)
                {
                    DataAvailable?.Invoke(buffer);
                }
            }
        }
    }

    private static float Pcm16Peak(byte[] pcm16)
    {
        var peak = 0f;
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var value = Math.Abs(BitConverter.ToInt16(pcm16, i) / (float)short.MaxValue);
            if (value > peak)
            {
                peak = value;
            }
        }
        return peak;
    }

    public void Stop()
    {
        _cts?.Cancel();

        if (_captureThread is not null && _captureThread.IsAlive)
        {
            _captureThread.Join(2000);
        }

        _captureThread = null;
        _cts?.Dispose();
        _cts = null;
        PeakLevel = 0;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ interop

    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const uint ActivationTypeProcessLoopback = 1;
    private const uint LoopbackModeIncludeTargetProcessTree = 0;
    private const uint LoopbackModeExcludeTargetProcessTree = 1;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventcallback = 0x00040000;
    private const uint BufferFlagsSilent = 0x2;
    private const short WaveFormatPcm = 1;
    private const ushort VtBlob = 65;

    private static Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public uint Padding;
        public IntPtr BlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marcador: o Windows exige que o handler seja "agile" pra poder chamá-lo de
    /// qualquer apartamento COM.</summary>
    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Completed = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
