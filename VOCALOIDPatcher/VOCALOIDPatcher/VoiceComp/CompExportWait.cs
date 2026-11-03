using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VOCALOIDPatcher.VoiceComp;

internal static class CompExportWait
{
    private static bool _waiting;

    internal static void UntilReady(Func<bool> ready)
    {
        if (ready()) return;
        if (_waiting) throw new InvalidOperationException("Voice-comp export preparation is already running.");
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Missing Editor Dispatcher.");
        dispatcher.VerifyAccess(); _waiting = true;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        Window? dialog = null; Exception? failure = null; bool complete = false;
        try
        {
            if (Application.Current?.MainWindow is { IsVisible: true } owner)
            {
                var content = new StackPanel { Margin = new Thickness(20) };
                content.Children.Add(new TextBlock { Text = CompEditorPanel.Text("Rendering"), Foreground = CompEditorPanel.Brush("#D6EFFF"), FontSize = 13 });
                content.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0, 15, 0, 18), Foreground = CompEditorPanel.Brush("#78C7E8") });
                content.Children.Add(CompEditorPanel.Action("Cancel", () => dialog?.Close()));
                dialog = new Window { Owner = owner, Title = CompEditorPanel.Text("Mode"), Content = content,
                    Width = 320, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = CompEditorPanel.Brush("#1B2026"), ShowInTaskbar = false };
            }
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (watch.Elapsed > TimeSpan.FromMinutes(5)) throw new TimeoutException("Voice-comp export preparation timed out.");
                    if (!ready()) return;
                    complete = true;
                }
                catch (Exception error) { failure = error; }
                frame.Continue = false; timer.Stop(); dialog?.Close();
            };
            timer.Start();
            if (dialog != null) dialog.ShowDialog(); else Dispatcher.PushFrame(frame);
            if (failure != null) throw new InvalidOperationException("Voice-comp export preparation failed.", failure);
            if (!complete) throw new OperationCanceledException("Voice-comp export canceled.");
        }
        finally { timer.Stop(); _waiting = false; }
    }
}
