namespace WinChatCopilot.Backend;

public enum ResponseState { Generating, Completed, Failed, Cancelled }

public sealed class ChatTurn(string prompt, DateTimeOffset? submittedAt = null)
{
    public string Prompt { get; } = prompt;
    public DateTimeOffset SubmittedAt { get; } = submittedAt ?? DateTimeOffset.Now;
    public string Response { get; internal set; } = "";
    public ResponseState State { get; internal set; } = ResponseState.Generating;
    public DateTimeOffset? FinishedAt { get; internal set; }
    public string? Error { get; internal set; }
}
