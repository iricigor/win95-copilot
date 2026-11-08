using System.Text;

namespace WinChatCopilot.Backend;

internal sealed class TerminalTextFilter
{
    private enum EscapeState { Text, Escape, Csi, String, StringEscape }
    private EscapeState state;

    public string Append(string text)
    {
        var clean = new StringBuilder();
        foreach (var character in text)
        {
            switch (state)
            {
                case EscapeState.Escape:
                    state = character switch
                    {
                        '[' => EscapeState.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => EscapeState.String,
                        >= '\x20' and <= '\x2f' => EscapeState.Escape,
                        _ => EscapeState.Text
                    };
                    break;
                case EscapeState.Csi:
                    if (character is >= '\x40' and <= '\x7e') state = EscapeState.Text;
                    break;
                case EscapeState.String:
                    if (character is '\a' or '\u009c') state = EscapeState.Text;
                    else if (character == '\x1b') state = EscapeState.StringEscape;
                    break;
                case EscapeState.StringEscape:
                    state = character == '\\' ? EscapeState.Text : EscapeState.String;
                    break;
                default:
                    if (character == '\x1b') state = EscapeState.Escape;
                    else if (character == '\u009b') state = EscapeState.Csi;
                    else if (character is '\u009d' or '\u0090' or '\u0098' or '\u009e' or '\u009f')
                        state = EscapeState.String;
                    else if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
                        clean.Append(character);
                    break;
            }
        }
        return clean.ToString();
    }
}
