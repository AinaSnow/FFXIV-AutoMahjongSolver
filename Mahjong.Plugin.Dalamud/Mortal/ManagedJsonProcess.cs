using System.Diagnostics;
namespace Mahjong.Plugin.Dalamud.Mortal;

internal interface IManagedJsonProcess : IDisposable
{
    TextWriter Input { get; }
    TextReader Output { get; }
    TextReader Error { get; }
    void Start();
    Task<int?> WaitForExitAsync(CancellationToken token);
    void Terminate();
}
internal sealed class ManagedJsonProcess(ProcessStartInfo startInfo) : IManagedJsonProcess
{
    private readonly Process process = new() { StartInfo = startInfo };
    public TextWriter Input => process.StandardInput;
    public TextReader Output => process.StandardOutput;
    public TextReader Error => process.StandardError;
    public void Start() { if (!process.Start()) throw new IOException("wsl.exe did not start"); }
    public async Task<int?> WaitForExitAsync(CancellationToken token)
    { await process.WaitForExitAsync(token).ConfigureAwait(false); return process.ExitCode; }
    public void Terminate()
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    public void Dispose() => process.Dispose();
}
