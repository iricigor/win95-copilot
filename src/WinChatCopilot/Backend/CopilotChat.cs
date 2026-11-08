using System.IO;
using System.Text;
using System.Text.Json;
using WinChatCopilot.Processes;

namespace WinChatCopilot.Backend;

public sealed class CopilotChat
{
    public async Task<string> SendAsync(BackendSession session, string prompt, Action<string> onText,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(onText);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Enter a nonempty message before sending.", nameof(prompt));
        var response = new ResponseStream(session.Id, onText);
        var diagnostic = new StringBuilder();
        var exit = await ProcessTransport.RunAsync(CopilotReadiness.CreateRequest(session, prompt),
            response.Append, chunk =>
            {
                if (diagnostic.Length + chunk.Length > 1024 * 1024)
                    throw new InvalidDataException("Copilot diagnostics exceeded the 1 MiB safety limit.");
                diagnostic.Append(chunk);
            }, cancellationToken).ConfigureAwait(false);
        if (exit != 0 || diagnostic.Length != 0)
        {
            var cleanDiagnostic = new TerminalTextFilter().Append(diagnostic.ToString());
            throw new InvalidDataException($"Copilot request failed (exit {exit}). Check authentication, entitlement and network, then Connect again. No automatic retry was attempted. " +
                (diagnostic.Length == 0 ? "No stderr diagnostic was supplied." :
                    cleanDiagnostic[..Math.Min(2000, cleanDiagnostic.Length)]));
        }
        return response.Complete();
    }

    private sealed class ResponseStream(Guid session, Action<string> onText)
    {
        private readonly StringBuilder line = new();
        private readonly StringBuilder rawText = new();
        private readonly StringBuilder text = new();
        private readonly TerminalTextFilter filter = new();
        private string? final;
        private int calls;
        private bool resultSeen;

        public void Append(string chunk)
        {
            foreach (var character in chunk)
            {
                if (character == '\n')
                {
                    ParseLine();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= 1024 * 1024)
                        throw new InvalidDataException("Copilot event exceeded the 1 MiB safety limit.");
                    line.Append(character);
                }
            }
        }

        private void ParseLine()
        {
            if (string.IsNullOrWhiteSpace(line.ToString())) return;
            try
            {
                using var document = JsonDocument.Parse(line.ToString());
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (string.IsNullOrEmpty(type))
                    throw new InvalidDataException("Copilot event has no type.");
                if (type.StartsWith("tool.", StringComparison.Ordinal))
                    throw new InvalidDataException("Copilot reported tool activity. Text-only operation was not established.");
                if (type == "session.error")
                    throw new InvalidDataException("Copilot reported a session error. Check authentication, entitlement and network, then Connect again. No automatic retry was attempted.");
                if (type == "model.model_call_success")
                {
                    var data = root.GetProperty("data");
                    if (data.GetProperty("toolCount").GetInt32() != 0 ||
                        data.GetProperty("requestCapture").GetProperty("tools").GetArrayLength() != 0)
                        throw new InvalidDataException("Copilot exposed tools. This backend is incompatible with text-only operation.");
                    calls++;
                }
                if (type == "assistant.message_delta")
                {
                    if (final is not null || resultSeen)
                        throw new InvalidDataException("Copilot streamed text after completing the response.");
                    var delta = root.GetProperty("data").GetProperty("deltaContent").GetString()
                        ?? throw new InvalidDataException("Copilot returned a null text delta.");
                    if (rawText.Length + delta.Length > 1024 * 1024)
                        throw new InvalidDataException("Copilot response exceeded the 1 MiB safety limit.");
                    rawText.Append(delta);
                    Publish(filter.Append(delta));
                }
                if (type == "assistant.message")
                {
                    if (final is not null || resultSeen)
                        throw new InvalidDataException("Copilot returned a duplicate or out-of-order final message.");
                    var data = root.GetProperty("data");
                    if (data.GetProperty("toolRequests").GetArrayLength() != 0)
                        throw new InvalidDataException("Copilot requested a tool. Text-only operation failed.");
                    final = data.GetProperty("content").GetString()
                        ?? throw new InvalidDataException("Copilot returned null response text.");
                    if (final.Length > 1024 * 1024)
                        throw new InvalidDataException("Copilot response exceeded the 1 MiB safety limit.");
                    if (rawText.Length != 0 && rawText.ToString() != final)
                        throw new InvalidDataException("Copilot final response differed from its streamed text.");
                    if (rawText.Length == 0) Publish(filter.Append(final));
                }
                if (type == "result")
                {
                    if (resultSeen || final is null || root.GetProperty("sessionId").GetString() != session.ToString() ||
                        root.GetProperty("exitCode").GetInt32() != 0)
                        throw new InvalidDataException("Copilot returned an invalid session or failed/out-of-order result.");
                    resultSeen = true;
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("Copilot returned malformed response events. Select a compatible backend; no completed response was accepted.", error);
            }
        }

        private void Publish(string chunk)
        {
            if (chunk.Length == 0) return;
            text.Append(chunk);
            onText(chunk);
        }

        public string Complete()
        {
            if (line.Length != 0) ParseLine();
            if (calls == 0 || !resultSeen || final is null || string.IsNullOrWhiteSpace(text.ToString()))
                throw new InvalidDataException("Copilot did not provide complete text, zero-tool evidence and a matching successful session result. Connect again; no automatic retry was attempted.");
            return text.ToString();
        }
    }
}
