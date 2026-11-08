using System.Text.Json.Serialization;

namespace WinChatCopilot.Settings;

[JsonConverter(typeof(JsonStringEnumConverter<SendMode>))]
public enum SendMode { ControlEnter, Enter }

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string? ExecutablePath { get; init; }
    public SendMode SendMode { get; init; } = SendMode.ControlEnter;
    public bool WordWrap { get; init; } = false;
    public bool AutoConnect { get; init; } = false;
    public bool ShowTimestamp { get; init; } = false;
    public string UserName { get; init; } = "User";
}
