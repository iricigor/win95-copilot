using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length == 0)
{
    Console.Error.Write("A synthetic scenario is required.");
    return 64;
}

var scenario = args[0];
var pidFile = scenario == "child" ? "child.pid" : "parent.pid";
File.WriteAllText($"{pidFile}.pending", Environment.ProcessId.ToString());
File.Move($"{pidFile}.pending", pidFile, overwrite: true);
switch (scenario)
{
    case "--no-auto-update":
        return await ReadinessAsync(args);
    case "stream":
        await WriteBytesAsync(Encoding.UTF8.GetBytes("first "));
        await WaitForFileAsync("continue");
        var unicode = Encoding.UTF8.GetBytes("\u20ac\u6f22\U0001f680");
        await WriteBytesAsync(unicode[..1]);
        File.WriteAllText("unicode-prefix", "ready");
        await WaitForFileAsync("continue-unicode");
        await WriteBytesAsync(unicode[1..]);
        await WriteBytesAsync(Encoding.UTF8.GetBytes("\nsecond\r\nthird"));
        Console.Error.Write("diagnostic only");
        return 0;
    case "formatting":
        Console.Write("one \u001b[31mtwo\u001b[0m\r\nthree");
        Console.Error.Write("\u001b[33mwarning\u001b[0m");
        return 0;
    case "exit":
        Console.Write("complete");
        return 0;
    case "nonzero":
        Console.Error.Write("synthetic failure");
        return 17;
    case "partial-failure":
        Console.Write("partial response");
        Console.Out.Flush();
        await WaitForFileAsync("continue");
        Console.Error.Write("failure after output");
        return 23;
    case "stderr-pressure":
        Console.Write("ready");
        Console.Out.Flush();
        Console.Error.Write(new string('d', 256 * 1024));
        Console.Error.Flush();
        Console.Write("|drained");
        Console.Out.Flush();
        await WaitForFileAsync("continue");
        return 0;
    case "invalid-utf8":
    case "utf16":
        await WaitForFileAsync("continue");
        await WriteBytesAsync(scenario == "utf16" ? [0xff, 0xfe, 0x61, 0x00] : [0xff]);
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 0;
    case "inspect":
    case "wrong-session":
    case "wrong-prompt":
        var received = args[1..];
        if (scenario == "wrong-session")
        {
            received[0] = "--session-id=incorrect";
        }
        else if (scenario == "wrong-prompt")
        {
            received[^1] = "incorrect";
        }

        static async Task<int> ReadinessAsync(string[] arguments)
        {
            var scenario = File.Exists("readiness-scenario") ? File.ReadAllText("readiness-scenario") : "ready";
            if (arguments.Contains("--version"))
            {
                Console.WriteLine(scenario == "version" ? "GitHub Copilot CLI 9.9.9." : "GitHub Copilot CLI 1.0.81.");
                return 0;
            }
            if (arguments[^1] != "Connection check only. Reply with READY. Do not use tools.")
                return await ChatAsync(arguments);
            File.WriteAllText("readiness.arguments.json", JsonSerializer.Serialize(arguments));
            File.WriteAllText("readiness.home", Environment.GetEnvironmentVariable("COPILOT_HOME"));
            File.WriteAllText("readiness.started.pending", Environment.ProcessId.ToString());
            File.Move("readiness.started.pending", "readiness.started", overwrite: true);
            if (scenario == "auth")
            {
                Console.Error.Write("Synthetic authentication failure. Sign in separately.");
                return 1;
            }
            if (scenario == "stall")
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 75;
            }
            if (scenario == "malformed")
            {
                Console.Write("{not-json}");
                return 0;
            }
            if (scenario == "missing-fields")
            {
                Console.WriteLine("{\"type\":\"assistant.message\",\"data\":{}}");
                return 0;
            }
            if (scenario == "no-evidence") return 0;
            var session = arguments.Single(arg => arg.StartsWith("--session-id="))["--session-id=".Length..];
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "model.model_call_success",
                data = new { toolCount = scenario == "tools" ? 1 : 0, requestCapture = new { tools = Array.Empty<object>() } }
            }));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "assistant.message",
                data = new { content = "READY", toolRequests = Array.Empty<object>() }
            }));
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "result",
                sessionId = scenario == "session" ? Guid.NewGuid().ToString() : session,
                exitCode = 0
            }));
            return 0;
        }

        Console.Write(JsonSerializer.Serialize(new
        {
            Arguments = received,
            WorkingDirectory = Environment.CurrentDirectory,
            InputAtEof = Console.Read() == -1,
            FixtureValue = Environment.GetEnvironmentVariable("WINCHAT_TEST_VALUE"),
            Path = Environment.GetEnvironmentVariable("PATH")
        }));
        return 0;
    case "with-child":
        var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        childInfo.ArgumentList.Add("child");
        using (var child = Process.Start(childInfo)
            ?? throw new InvalidOperationException("Synthetic child did not start."))
        {
            await WaitForFileAsync("child.pid");
            Console.Write("child ready");
            Console.Out.Flush();
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        return 0;
    case "stall":
    case "silent-stall":
    case "sentinel":
    case "child":
        if (scenario is not ("silent-stall" or "child"))
        {
            Console.Write("ready");
            Console.Out.Flush();
        }

        await Task.Delay(TimeSpan.FromSeconds(30));
        Console.Error.Write("Synthetic stall safety bound expired.");
        return 75;
    default:
        Console.Error.Write($"Unknown synthetic scenario: {scenario}");
        return 64;
}

static async Task WriteBytesAsync(byte[] bytes)
{
    var output = Console.OpenStandardOutput();
    await output.WriteAsync(bytes);
    await output.FlushAsync();
}

static async Task WaitForFileAsync(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!File.Exists(path))
    {
        await Task.Delay(20, timeout.Token);
    }

}

static async Task<int> ChatAsync(string[] arguments)
{
    var scenario = File.Exists("chat-scenario") ? File.ReadAllText("chat-scenario") : "complete";
    File.WriteAllText("chat.arguments.json", JsonSerializer.Serialize(arguments));
    File.AppendAllText("chat.requests.jsonl", JsonSerializer.Serialize(arguments) + "\n");
    File.WriteAllText("chat.started.pending", Environment.ProcessId.ToString());
    File.Move("chat.started.pending", "chat.started", overwrite: true);
    if (scenario is "pre-failure" or "auth")
    {
        Console.Error.Write(scenario == "auth" ? "Synthetic authentication failure. Sign in separately." :
            "\u001b[31mSynthetic submission failure\u001b[0m");
        return 19;
    }
    if (scenario == "silent-stall")
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 75;
    }
    using var child = scenario == "with-child"
        ? Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "child" }, UseShellExecute = false })
            ?? throw new InvalidOperationException("Synthetic chat child did not start.")
        : null;
    if (child is not null) await WaitForFileAsync("child.pid");
    if (scenario == "malformed")
    {
        Console.WriteLine("{not-json}");
        return 0;
    }
    if (scenario == "missing-fields")
    {
        Console.WriteLine("{\"type\":\"assistant.message_delta\",\"data\":{}}");
        return 0;
    }
    if (scenario == "oversized-event")
    {
        Console.Write(new string('x', 1024 * 1024 + 1));
        return 0;
    }
    if (scenario == "tool-activity") Emit(new { type = "tool.execution_start", data = new { } });
    if (scenario == "session-error") Emit(new { type = "session.error", data = new { message = "Synthetic service failure" } });
    if (scenario != "no-model")
        Emit(new
        {
            type = "model.model_call_success",
            data = new
            {
                toolCount = scenario == "tools" ? 1 : 0,
                requestCapture = new { tools = scenario == "captured-tools" ? new[] { "shell" } : [] }
            }
        });
    Emit(new { type = "usage", data = new { content = "DIAGNOSTIC_NOT_RESPONSE" } });
    var first = scenario == "long-stream"
        ? string.Join("\r\n", Enumerable.Range(0, 150).Select(i => $"synthetic line {i}")) + "\r\n"
        : "first \u001b[";
    var second = scenario == "long-stream"
        ? string.Join("\r\n", Enumerable.Range(150, 20).Select(i => $"synthetic line {i}"))
        : "31m\u20ac\u6f22\U0001f680\u001b[0m\r\nsecond\tline\u001b]0;hidden title\a\nthird";
    var raw = first + second;
    if (scenario != "final-only")
    {
        var json = JsonSerializer.Serialize(new { type = "assistant.message_delta", data = new { deltaContent = first } });
        foreach (var part in Encoding.UTF8.GetBytes(json + "\n").Chunk(7))
            await WriteBytesAsync(part);
        if (scenario is "stream" or "long-stream" or "partial-failure" or "stall" or "with-child") await WaitForFileAsync("chat.continue");
        var unicodeJson = JsonSerializer.Serialize(new { type = "assistant.message_delta", data = new { deltaContent = second } },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        foreach (var part in Encoding.UTF8.GetBytes(unicodeJson + "\n").Chunk(1))
            await WriteBytesAsync(part);
    }
    if (scenario == "partial-failure")
    {
        Console.Error.Write("\u001b[33mSynthetic failure after output\u001b[0m");
        return 23;
    }
    if (scenario == "stall")
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        return 75;
    }
    if (scenario == "truncated")
    {
        Console.Write("{\"type\":");
        return 0;
    }
    var final = new
    {
        type = "assistant.message",
        data = new
        {
            content = scenario == "mismatch" ? "wrong final" : raw,
            toolRequests = scenario == "tool-request" ? new[] { "shell" } : []
        }
    };
    Emit(final);
    if (scenario == "duplicate-final") Emit(final);
    if (scenario != "no-result")
    {
        var session = arguments.Single(arg => arg.StartsWith("--session-id="))["--session-id=".Length..];
        Emit(new
        {
            type = "result",
            sessionId = scenario == "wrong-session" ? Guid.NewGuid().ToString() : session,
            exitCode = scenario == "failed-result" ? 1 : 0
        });
    }
    if (scenario == "stderr") Console.Error.Write("Synthetic unexpected stderr");
    return scenario == "nonzero" ? 21 : 0;

    static void Emit(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }
}
