using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Records the sound of one program (3DS Capture plays the 3DS audio it receives over USB through the speakers) with
/// Windows' per-process loopback (Windows 10 2004+), and hands it to FFmpeg as 48 kHz 16-bit stereo through a named pipe.
/// Silence is filled in whenever the program plays nothing, so the audio stays in step with the video.
/// </summary>
public sealed class ProcessAudioCapture : IDisposable
{
    public const int Rate = 48000, Channels = 2, BytesPerFrame = 4;
    public string PipePath => @"\\.\pipe\" + _pipeName;
    /// <summary>FFmpeg input arguments that read this capture.</summary>
    public string FfmpegInput => $"-f s16le -ar {Rate} -ac {Channels} {RecordingService.InputQueue(1024)}-i \"{PipePath}\"";
    public string? Error { get; private set; }

    private readonly string _processName;
    private readonly string _pipeName = "ShinyForge_audio_" + Guid.NewGuid().ToString("N")[..12];
    private NamedPipeServerStream? _pipe;
    private Thread? _thread;
    private volatile bool _stop;

    public ProcessAudioCapture(string processName) => _processName = processName;

    /// <summary>Creates the pipe (FFmpeg can open it right after) and starts capturing in the background.</summary>
    public void Start()
    {
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);
        _thread = new Thread(Run) { IsBackground = true, Name = "process audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _pipe!.WaitForConnection();
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            var pid = Process.GetProcessesByName(_processName).FirstOrDefault()?.Id;
            if (pid is { } id)
            {
                try { (client, capture) = Activate((uint)id); }
                catch (Exception e) { Error = "couldn't capture the program's sound: " + e.Message; AppPaths.Log("process audio: " + Error); }
            }
            else
            {
                Error = _processName + " isn't running - recording silence";
                AppPaths.Log("process audio: " + Error);
            }

            var clock = Stopwatch.StartNew();
            long written = 0;
            var silence = new byte[Rate / 10 * BytesPerFrame];
            var buf = new byte[Rate * BytesPerFrame];
            while (!_stop)
            {
                Thread.Sleep(10);
                if (capture != null)
                {
                    while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                    {
                        if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) break;
                        int bytes = (int)frames * BytesPerFrame;
                        if (bytes > buf.Length) buf = new byte[bytes];
                        if ((flags & 0x2) != 0 || data == IntPtr.Zero) Array.Clear(buf, 0, bytes); // AUDCLNT_BUFFERFLAGS_SILENT
                        else Marshal.Copy(data, buf, 0, bytes);
                        capture.ReleaseBuffer(frames);
                        _pipe.Write(buf, 0, bytes);
                        written += frames;
                    }
                }
                // nothing playing: keep the stream going in real time (a little behind, so real sound never piles up)
                long due = (long)(clock.Elapsed.TotalSeconds * Rate) - Rate / 10;
                while (written < due)
                {
                    int n = (int)Math.Min(due - written, silence.Length / BytesPerFrame);
                    _pipe.Write(silence, 0, n * BytesPerFrame);
                    written += n;
                }
            }
            try { client?.Stop(); } catch { }
        }
        catch (IOException) { /* FFmpeg closed the pipe (recording stopped) */ }
        catch (ObjectDisposedException) { }
        catch (Exception e) { Error = e.Message; AppPaths.Log("process audio: " + e); }
    }

    public void Dispose()
    {
        _stop = true;
        try { _pipe?.Dispose(); } catch { }
        _thread?.Join(2000);
    }

    // ------------------------------------------------------------------ WASAPI process loopback
    private static (IAudioClient, IAudioCaptureClient) Activate(uint pid)
    {
        var p = new ActivationParams { ActivationType = 1, TargetProcessId = pid, ProcessLoopbackMode = 0 }; // include the process tree
        var blob = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        var prop = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(p, blob, false);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(prop, i, 0);
            Marshal.WriteInt16(prop, 0, 65);                         // VT_BLOB
            Marshal.WriteInt32(prop, 8, Marshal.SizeOf<ActivationParams>());
            Marshal.WriteIntPtr(prop, 16, blob);
            var handler = new Completion();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, prop, handler, out _));
            if (!handler.Done.Wait(5000)) throw new TimeoutException("Windows didn't answer the audio request");
            handler.Operation!.GetActivateResult(out var hr, out var unk);
            Check(hr);
            var client = (IAudioClient)unk;
            var fmt = new WaveFormat { Tag = 1, Channels = Channels, SampleRate = Rate, AvgBytesPerSec = Rate * BytesPerFrame, BlockAlign = BytesPerFrame, Bits = 16 };
            var fp = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
            try
            {
                Marshal.StructureToPtr(fmt, fp, false);
                // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 200 ms buffer (read by polling; the event just has to exist)
                Check(client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000, 2_000_000, 0, fp, IntPtr.Zero));
            }
            finally { Marshal.FreeHGlobal(fp); }
            Check(client.SetEventHandle(Signal.SafeWaitHandle.DangerousGetHandle()));
            var cid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref cid, out var cap));
            Check(client.Start());
            return (client, (IAudioCaptureClient)cap);
        }
        finally { Marshal.FreeHGlobal(blob); Marshal.FreeHGlobal(prop); }
    }

    private static readonly AutoResetEvent Signal = new(false);

    private static void Check(int hr){ if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat { public short Tag; public short Channels; public int SampleRate; public int AvgBytesPerSec; public short BlockAlign; public short Bits; public short Size; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public IActivateAudioInterfaceAsyncOperation? Operation;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) { Operation = op; Done.Set(); }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
