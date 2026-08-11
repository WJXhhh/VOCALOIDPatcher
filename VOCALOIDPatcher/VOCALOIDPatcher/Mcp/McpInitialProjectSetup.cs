using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Yamaha.VOCALOID.TrackEditor;

namespace VOCALOIDPatcher.Mcp;

internal static class McpInitialProjectSetup
{
    public static void SuppressNextAddTrackDialog()
    {
        Application? application = Application.Current;
        Dispatcher? dispatcher = application?.Dispatcher;
        if (application == null || dispatcher == null)
            return;

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        timer.Tick += (_, _) =>
        {
            AddTrackDlg[] dialogs = application.Windows.OfType<AddTrackDlg>().ToArray();
            foreach (AddTrackDlg dialog in dialogs)
                dialog.Close();
            if (dialogs.Length != 0 || DateTime.UtcNow >= deadline)
                timer.Stop();
        };
        timer.Start();
    }
}
