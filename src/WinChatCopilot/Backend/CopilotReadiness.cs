using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WinChatCopilot.Processes;

namespace WinChatCopilot.Backend;

public sealed record BackendSession(string ExecutablePath, Guid Id, string Home, string WorkingDirectory);

public sealed class CopilotReadiness(string storageDirectory)
{
    public async Task<BackendSession> PrepareAsync(string executable, CancellationToken cancellationToken)
    {
        var home = Path.Combine(storageDirectory, "cli");
        var work = Path.Combine(storageDirectory, "work");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        var version = await RunAsync(CreateStartInfo(executable, home, work,
            ["--no-auto-update", "--version"]), cancellationToken).ConfigureAwait(false);
        if (!version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Contains("GitHub Copilot CLI 1.0.81."))
            throw new InvalidDataException("This app supports the verified bundled Copilot CLI 1.0.81 contract only. Select a compatible executable; other versions need text-only contract verification. No install or update was attempted.");

        var session = new BackendSession(executable, Guid.NewGuid(), home, work);
        var arguments = new[]
        {
            "--no-auto-update", "--no-custom-instructions", "--no-ask-user",
            "--no-remote", "--no-remote-export", "--no-color", "--disable-builtin-mcps",
            "--disallow-temp-dir", "--available-tools=__winchat_text_only__",
            "--deny-tool=shell", "--deny-tool=write", "--output-format=json",
            "--stream=on", "--mode=interactive", $"--session-id={session.Id}",
            "--prompt", "Connection check only. Reply with READY. Do not use tools."
        };
        var output = await RunAsync(CreateStartInfo(executable, home, work, arguments),
            cancellationToken).ConfigureAwait(false);
        try { VerifyResponse(output, session.Id); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Copilot returned malformed readiness events. Select a compatible backend; no ready state was accepted.", error);
        }
        return session;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string home, string work,
        IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = work };
        foreach (var key in info.Environment.Keys.ToArray())
        {
            if ((key.StartsWith("COPILOT_", StringComparison.OrdinalIgnoreCase) &&
                 !key.Equals("COPILOT_GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase)) ||
                key.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(key);
        }
        info.Environment["COPILOT_HOME"] = home;
        info.Environment["COPILOT_OTEL_ENABLED"] = "false";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<string> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        var exit = await ProcessTransport.RunAsync(info,
            chunk => AppendBounded(output, chunk), chunk => AppendBounded(error, chunk),
            cancellationToken).ConfigureAwait(false);
        if (exit != 0 || error.Length != 0)
            throw new InvalidDataException($"Copilot readiness failed (exit {exit}). Authenticate separately with Copilot CLI, verify your entitlement and network, then Connect again. No automatic retry was attempted. " +
                (error.Length == 0 ? "No stderr diagnostic was supplied." : Sanitize(error.ToString())));
        return output.ToString();
    }

    private static void AppendBounded(StringBuilder target, string chunk)
    {
        if (target.Length + chunk.Length > 1024 * 1024)
            throw new InvalidDataException("Copilot readiness output exceeded its 1 MiB safety limit.");
        target.Append(chunk);
    }

    private static string Sanitize(string text) => new(text.Take(2000)
        .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray());

    internal static void VerifyResponse(string output, Guid session)
    {
        var calls = 0;
        var resultSeen = false;
        var contentSeen = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type is "tool.execution_start" or "session.error")
                throw new InvalidDataException($"Copilot readiness reported {type}; text-only readiness was not established.");
            if (type == "model.model_call_success")
            {
                var data = root.GetProperty("data");
                if (data.GetProperty("toolCount").GetInt32() != 0 ||
                    data.GetProperty("requestCapture").GetProperty("tools").GetArrayLength() != 0)
                    throw new InvalidDataException("Copilot exposed tools. This backend is incompatible with text-only operation.");
                calls++;
            }
            if (type == "assistant.message")
            {
                var data = root.GetProperty("data");
                if (data.GetProperty("toolRequests").GetArrayLength() != 0)
                    throw new InvalidDataException("Copilot requested a tool. Text-only readiness failed.");
                var content = data.GetProperty("content").GetString();
                if (string.IsNullOrWhiteSpace(content) || content.Contains('\u001b'))
                    throw new InvalidDataException("Copilot returned empty or terminal-formatted response text.");
                contentSeen = true;
            }
            if (type == "result")
            {
                if (resultSeen || root.GetProperty("sessionId").GetString() != session.ToString() ||
                    root.GetProperty("exitCode").GetInt32() != 0)
                    throw new InvalidDataException("Copilot returned an invalid session or failed result.");
                resultSeen = true;
            }
        }
        if (calls == 0 || !resultSeen || !contentSeen)
            throw new InvalidDataException("Copilot did not provide a complete authenticated, zero-tool response and session identity. Check authentication, entitlement and network, then retry manually.");
    }
}
