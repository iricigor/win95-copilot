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
