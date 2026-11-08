using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinChatCopilot.Backend;

namespace WinChatCopilot;

public sealed class TranscriptView : RichTextBox
{
    private bool wordWrap;

    public TranscriptView()
    {
        Document.Blocks.Clear();
        Document.PagePadding = new Thickness(0);
        Document.FontWeight = FontWeights.Normal;
        IsReadOnly = true;
        IsDocumentEnabled = false;
        IsUndoEnabled = false;
    }

    internal string Text => new TextRange(Document.ContentStart, Document.ContentEnd).Text;

    internal void Update(IReadOnlyList<ChatTurn> turns, bool timestamps, int waitingDots = 0, string userName = "User")
    {
        if (Document.Blocks.Count > turns.Count * 2) Document.Blocks.Clear();
        while (Document.Blocks.Count < turns.Count * 2)
        {
            var speaker = Document.Blocks.Count % 2 == 0 ? "User" : "Assistant";
            var paragraph = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };
            paragraph.Inlines.Add(new Run { FontWeight = FontWeights.Normal });
            paragraph.Inlines.Add(new Run(speaker) { FontWeight = FontWeights.Bold });
            paragraph.Inlines.Add(new Run(":") { FontWeight = FontWeights.Bold });
            paragraph.Inlines.Add(new Run { FontWeight = FontWeights.Normal });
            paragraph.Inlines.Add(new Run { FontWeight = FontWeights.Normal });
            Document.Blocks.Add(paragraph);
        }
        var paragraphs = Document.Blocks.Cast<Paragraph>().ToArray();
        for (var i = 0; i < turns.Count; i++)
        {
            UpdateMessage(paragraphs[i * 2], userName, TranscriptDisplay.Timestamp(turns[i].SubmittedAt, timestamps),
                TranscriptDisplay.Message(turns[i].Prompt));
            UpdateMessage(paragraphs[i * 2 + 1], "Assistant", TranscriptDisplay.Timestamp(turns[i].SubmittedAt, timestamps),
                TranscriptDisplay.Response(turns[i]), i == turns.Count - 1 ? waitingDots : 0);
        }
        UpdatePageWidth();
    }

    internal void UpdateWaitingIndicator(int dots)
    {
        if (Document.Blocks.LastBlock is not Paragraph paragraph) return;
        var runs = paragraph.Inlines.Cast<Run>().ToArray();
        if (dots == 0 && runs[3].Text.Length != 0)
            UpdateRun(runs[4], " " + runs[4].Text);
        UpdateRun(runs[3], dots == 0 ? "" : " " + new string('.', dots));
        UpdatePageWidth();
    }

    private static void UpdateMessage(Paragraph paragraph, string speaker, string timestamp, string message, int dots = 0)
    {
        var runs = paragraph.Inlines.Cast<Run>().ToArray();
        UpdateRun(runs[0], timestamp);
        UpdateRun(runs[1], speaker);
        UpdateRun(runs[3], dots == 0 ? "" : " " + new string('.', dots));
        UpdateRun(runs[4], dots == 0 ? " " + message : message);
    }

    private static void UpdateRun(Run run, string text)
    {
        var previous = run.Text;
        var prefix = 0;
        while (prefix < previous.Length && prefix < text.Length && previous[prefix] == text[prefix]) prefix++;
        if (prefix == previous.Length && prefix == text.Length) return;
        // Edit only the changed suffix so selections in existing streamed text keep their anchors.
        var start = run.ContentStart.GetPositionAtOffset(prefix)!;
        start.DeleteTextInRun(previous.Length - prefix);
        start.InsertTextInRun(text[prefix..]);
    }

    internal void SetWordWrap(bool enabled)
    {
        wordWrap = enabled;
        HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        UpdatePageWidth();
    }

    private void UpdatePageWidth()
    {
        if (wordWrap)
        {
            Document.PageWidth = double.NaN;
            return;
        }
        var width = 0.0;
        foreach (var paragraph in Document.Blocks.OfType<Paragraph>())
        {
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n');
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection,
                new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var runs = paragraph.Inlines.OfType<Run>().ToArray();
            if (runs.Length == 5)
            {
                formatted.SetFontWeight(FontWeights.Bold, runs[0].Text.Length, runs[1].Text.Length);
                formatted.SetFontWeight(FontWeights.Bold, runs[0].Text.Length + runs[1].Text.Length, 1);
            }
            width = Math.Max(width, formatted.WidthIncludingTrailingWhitespace);
        }
        Document.PageWidth = Math.Clamp(width + 1, 1, 1_000_000);
    }
}
