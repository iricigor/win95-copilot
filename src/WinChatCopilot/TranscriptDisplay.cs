using System.Globalization;
using System.Text;
using WinChatCopilot.Backend;

namespace WinChatCopilot;

internal static class TranscriptDisplay
{
    internal static string Heading(string speaker, DateTimeOffset submittedAt, bool timestamps) =>
        timestamps
            ? $"{speaker} [{submittedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}]:\r\n"
            : $"{speaker}:\r\n";

    internal static string Ending(ResponseState state, bool markGenerating = false) => state switch
    {
        ResponseState.Generating when markGenerating => "\r\n[In progress]\r\n\r\n",
        ResponseState.Generating => "",
        ResponseState.Failed => "\r\n[Failed]\r\n\r\n",
        ResponseState.Cancelled => "\r\n[Cancelled]\r\n\r\n",
        _ => "\r\n\r\n"
    };

    internal static string Format(IEnumerable<ChatTurn> turns, bool timestamps, bool markGenerating = false)
    {
        var text = new StringBuilder();
        foreach (var turn in turns)
        {
            text.Append(Heading("User", turn.SubmittedAt, timestamps)).Append(turn.Prompt).Append("\r\n\r\n")
                .Append(Heading("Assistant", turn.SubmittedAt, timestamps)).Append(turn.Response).Append(Ending(turn.State, markGenerating));
        }
        return text.ToString();
    }
}
