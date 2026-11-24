namespace SnapLite.Capture;

internal enum CaptureAction
{
    Region,
    ActiveWindow,
    AllMonitors,
    CurrentMonitor,
    WebFullPage,
    WebSteps,
    ScrollWindow,
}

internal static class CaptureActionExtensions
{
    public static string DisplayName(this CaptureAction action) => action switch
    {
        CaptureAction.Region => "Region",
        CaptureAction.ActiveWindow => "Active window",
        CaptureAction.AllMonitors => "Full screen (all monitors)",
        CaptureAction.CurrentMonitor => "Current monitor",
        CaptureAction.WebFullPage => "Full webpage…",
        CaptureAction.WebSteps => "Webpage in steps…",
        CaptureAction.ScrollWindow => "Scrolling window (best effort)",
        _ => action.ToString(),
    };
}
