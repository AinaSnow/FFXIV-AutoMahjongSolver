using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Mahjong.Plugin.Dalamud.Logging;

/// <summary>One cancellable background subscriber. Native Deucalion owns its own unload lifecycle.</summary>
internal sealed class DeucalionCapture : IDisposable
{
    internal const string LibrarySha256 = "326BE4DB261064EBB51B8C46D2940F55701D79887E2C65070AFF00DE4C8C65EF";
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task worker = Task.CompletedTask;
    private bool requested, disposed;
    private volatile bool connected;
    private string status = "Off";
    private long rejected;
    internal event Action<RawReceivedPacket>? Received;
    internal event Action<PacketReadFailure>? Rejected;
    internal bool IsEnabled => connected;
    internal string Status => Volatile.Read(ref status);
    internal long RejectedPackets => Interlocked.Read(ref rejected);
    internal Task Completion { get { lock(gate) return worker; } }

    internal DeucalionCapture(string pluginDirectory, Func<CancellationToken,Task<Stream>>? connect = null) =>
        this.connect = connect ?? (ct => ConnectNativeAsync(pluginDirectory, ct));

    internal void SetEnabled(bool value)
    {
        lock(gate)
        {
            if (disposed || value == requested) return;
            requested = value;
            if (!value)
            {
                connected = false;
                cancellation?.Cancel();
                Volatile.Write(ref status,"Off");
                return;
            }
            var previous = worker;
            var owner = new CancellationTokenSource();
            cancellation = owner;
            Volatile.Write(ref status,"Connecting to Deucalion");
            // Serialize generations; no old callbacks can publish into the next subscription.
            worker = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try { if (!owner.IsCancellationRequested) await RunAsync(owner.Token).ConfigureAwait(false); }
                finally
                {
                    lock(gate) { if (ReferenceEquals(cancellation,owner)) cancellation = null; owner.Dispose(); }
                }
            });
        }
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            using var stream = await connect(cancellation).ConfigureAwait(false);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            // Server sends HELLO before any packet. Require decoded output, not just a pipe connection.
            using var greeting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            greeting.CancelAfter(TimeSpan.FromSeconds(10));
            var first = await DeucalionWire.ReadFrameAsync(stream,greeting.Token).ConfigureAwait(false);
            string hello = DeucalionWire.Hello(first) ?? throw new InvalidDataException("Missing Deucalion greeting");
            if (!DeucalionWire.CompatibleHello(hello))
                throw new InvalidDataException("Deucalion requires version 1.5.x, RECV ON and CREATE_TARGET ON: " + hello[..Math.Min(hello.Length,240)]);
            await stream.WriteAsync(DeucalionWire.Command(0,9000,"MahjongSolver"),greeting.Token).ConfigureAwait(false);
            await stream.WriteAsync(DeucalionWire.Command(5,2),greeting.Token).ConfigureAwait(false);
            lock(gate)
            {
                cancellation.ThrowIfCancellationRequested();
                connected = true;
                Volatile.Write(ref status,"Deucalion connected (decoded Zone receive)");
            }
            var reader = ReadPacketsAsync(stream,lifetime.Token);
            var heartbeat = PingAsync(stream,lifetime.Token);
            try { await await Task.WhenAny(reader,heartbeat).ConfigureAwait(false); }
            finally
            {
                lifetime.Cancel();
                stream.Dispose();
                try { await Task.WhenAll(reader,heartbeat).ConfigureAwait(false); } catch { /* First failure is reported below. */ }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock(gate) if (!cancellation.IsCancellationRequested)
            {
                string reason = ex is InvalidDataException ? "transport-invalid-frame" : "transport-disconnected";
                Interlocked.Increment(ref rejected);
                try { Rejected?.Invoke(new PacketReadFailure(reason)); } catch { /* Keep a failed observer from faulting the lifecycle task. */ }
                Volatile.Write(ref status,$"Deucalion unavailable: {ex.GetType().Name}: {ex.Message}. Toggle capture to retry.");
            }
        }
        finally { connected = false; }
    }

    private async Task ReadPacketsAsync(Stream stream, CancellationToken cancellation)
    {
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            idle.CancelAfter(TimeSpan.FromSeconds(15));
            var frame = await DeucalionWire.ReadFrameAsync(stream,idle.Token).ConfigureAwait(false);
            var packet = DeucalionWire.Decode(frame);
            if (packet is null) continue;
            lock(gate)
            {
                cancellation.ThrowIfCancellationRequested();
                Received?.Invoke(packet);
            }
        }
    }

    private static async Task PingAsync(Stream stream, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
            await stream.WriteAsync(DeucalionWire.Command(1,0),cancellation).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectNativeAsync(string directory, CancellationToken cancellation)
    {
        string name = $"deucalion-{Environment.ProcessId}";
        var existing = await TryConnectAsync(name,300,cancellation).ConfigureAwait(false);
        if (existing is not null) return existing;
        if (!string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"ffxiv_dx11",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native capture may only start inside ffxiv_dx11");
        string path = Path.GetFullPath(Path.Combine(directory,"capture-runtime","deucalion.dll"));
        // Reuse another subscriber's module; never unload it or issue the global Exit command.
        if (GetModuleHandleW("deucalion.dll") == 0)
        {
            using var library = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(library)).Equals(LibrarySha256,StringComparison.Ordinal))
                throw new InvalidDataException("Deucalion DLL checksum mismatch");
            cancellation.ThrowIfCancellationRequested();
            // Deliberately do not FreeLibrary: upstream unloads after the final pipe subscriber disconnects.
            // DllMain starts its own thread; all initialization happens off the game framework thread.
            if (LoadLibraryExW(path,0,0x100 | 0x800) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),"Cannot load bundled Deucalion");
        }
        return await TryConnectAsync(name,10000,cancellation).ConfigureAwait(false)
            ?? throw new TimeoutException("Deucalion pipe did not become available; inspect %APPDATA%/deucalion logs");
    }

    private static async Task<Stream?> TryConnectAsync(string name, int timeout, CancellationToken cancellation)
    {
        var pipe = new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(timeout,cancellation).ConfigureAwait(false); return pipe; }
        catch (TimeoutException) { pipe.Dispose(); return null; }
        catch { pipe.Dispose(); throw; }
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern nint LoadLibraryExW(string file,nint reserved,uint flags);

    public void Dispose()
    {
        lock(gate)
        {
            if (disposed) return;
            SetEnabled(false); disposed = true;
        }
    }
}
