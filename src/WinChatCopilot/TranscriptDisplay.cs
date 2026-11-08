using System.Globalization;
using System.Text;
using WinChatCopilot.Backend;

namespace WinChatCopilot;

internal static class TranscriptDisplay
{
    internal static string Heading(string speaker, DateTimeOffset submittedAt, bool timestamps) =>
        $"{Timestamp(submittedAt, timestamps)}{speaker}: ";

    internal static string Timestamp(DateTimeOffset submittedAt, bool timestamps) =>
        timestamps ? $"[{submittedAt.ToString("HH:mm", CultureInfo.InvariantCulture)}] " : "";

    internal static string Marker(ResponseState state, bool markGenerating = false) => state switch
    {
        ResponseState.Generating when markGenerating => "[In progress]",
        ResponseState.Failed => "[Failed]",
        ResponseState.Cancelled => "[Cancelled]",
        _ => ""
    };

    internal static string Message(string text) => text.TrimEnd('\r', '\n');

    internal static string Response(ChatTurn turn, bool markGenerating = false)
    {
        var text = Message(turn.Response);
        var marker = Marker(turn.State, markGenerating);
        return marker.Length == 0 ? text : text.Length == 0 ? marker : text + "\r\n" + marker;
    }

    internal static string Format(IEnumerable<ChatTurn> turns, bool timestamps, bool markGenerating = false,
        string userName = "User")
    {
        var text = new StringBuilder();
        foreach (var turn in turns)
        {
            text.Append(Heading(userName, turn.SubmittedAt, timestamps)).Append(Message(turn.Prompt)).Append("\r\n")
                .Append(Heading("Assistant", turn.SubmittedAt, timestamps)).Append(Response(turn, markGenerating)).Append("\r\n");
        }
        return text.ToString();
    }
}
