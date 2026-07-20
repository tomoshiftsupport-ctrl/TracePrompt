using System.IO;

namespace TracePrompt.Models;

public sealed class HistoryItem
{
    private HistoryItem(
        DateTime timestamp,
        string message,
        RecordedAction? action,
        CaptureFrame? frame,
        bool isHighlighted = false)
    {
        Timestamp = timestamp;
        Message = message;
        Action = action;
        Frame = frame;
        IsHighlighted = isHighlighted;
    }

    public DateTime Timestamp { get; }
    public string Message { get; }
    public RecordedAction? Action { get; }
    public CaptureFrame? Frame { get; }
    public bool IsHighlighted { get; }

    public RecordedAction? Click => Action;

    public bool IsCaptureLog =>
        Frame is not null
        || (Action is not null && !string.IsNullOrWhiteSpace(Action.ScreenshotPath));

    public bool IsActionLog => Action is not null;

    public bool IsPlainActionLog =>
        Action is not null && string.IsNullOrWhiteSpace(Action.ScreenshotPath);

    public bool IsStatusLog => Action is null && Frame is null;

    public string LogMarker =>
        IsCaptureLog ? "[C]"
        : IsActionLog ? "[A]"
        : ">";

    public string DisplayText =>
        Action?.ToHistoryText()
        ?? Frame?.ToHistoryText()
        ?? $"{Timestamp:HH:mm:ss}  {Message}";

    public bool HasScreenshot
    {
        get
        {
            string? pathValue = ScreenshotPath;
            return !string.IsNullOrWhiteSpace(pathValue) && File.Exists(pathValue);
        }
    }

    public string? ScreenshotPath =>
        Action?.ScreenshotPath
        ?? Frame?.ScreenshotPath;

    public static HistoryItem FromMessage(string message, bool isHighlighted = false)
    {
        return new HistoryItem(DateTime.Now, message, null, null, isHighlighted);
    }

    public static HistoryItem FromAction(RecordedAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new HistoryItem(action.RecordedAt, action.ToHistoryText(), action, null);
    }

    public static HistoryItem FromFrame(CaptureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new HistoryItem(frame.RecordedAt, frame.ToHistoryText(), null, frame);
    }
}
