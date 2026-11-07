using System.Diagnostics;
using System.IO;
using System.Text;

namespace WinChatCopilot.Processes;

public static class ProcessTransport
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        Action<string> onStandardOutput,
        Action<string> onStandardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(onStandardOutput);
        ArgumentNullException.ThrowIfNull(onStandardError);
        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new ArgumentException("Use ArgumentList, not a shell command string.", nameof(startInfo));
        }

        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = new UTF8Encoding(false, true);
        startInfo.StandardErrorEncoding = new UTF8Encoding(false, true);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The backend process did not start.");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            process.StandardInput.Close();
            using var standardOutput = new StreamReader(process.StandardOutput.BaseStream,
                startInfo.StandardOutputEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var standardError = new StreamReader(process.StandardError.BaseStream,
                startInfo.StandardErrorEncoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var output = PumpAsync(standardOutput, onStandardOutput, stop);
            var error = PumpAsync(standardError, onStandardError, stop);
            var exit = WaitForExitAsync(process, stop.Token);
            await Task.WhenAll(output, error, exit).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            StopOwnedProcess(process);
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Action<string> receive,
        CancellationTokenSource stop)
    {
        var completed = false;
        try
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false)) != 0)
            {
                receive(new string(buffer, 0, count));
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopOwnedProcess(process);
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
    }

    private static void StopOwnedProcess(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited between the ownership check and Kill.
            }
        }
    }
}
