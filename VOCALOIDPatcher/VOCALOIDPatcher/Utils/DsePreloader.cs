using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID;

namespace VOCALOIDPatcher.Utils;

internal static class DsePreloader
{
    private static int _started;
    private static Task? _preloadTask;
    private static nint _dftHandle;
    private static nint _dseHandle;

    public static void Start()
    {
        if (!Settings.PreloadDse || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _preloadTask = Task.Run(Preload);
    }

    private static void Preload()
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var editorDirectory = Path.GetDirectoryName(typeof(App).Assembly.Location);
            if (string.IsNullOrEmpty(editorDirectory))
                throw new DirectoryNotFoundException("VOCALOID editor directory is unavailable.");

            _dftHandle = Load(Path.Combine(editorDirectory, "DSE_DFT.dll"));
            _dseHandle = Load(Path.Combine(editorDirectory, "DSE.dll"));

            StartupProfiler.LogMilestone(
                "DSE native modules preloaded",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception e)
        {
            StartupProfiler.LogMilestone(
                $"DSE native module preload failed ({e.GetType().Name}: {e.Message})",
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static nint Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("DSE native module was not found.", Path.GetFileName(path));

        // Keep the explicit reference for the process lifetime. The later DllImport calls
        // reuse the loaded module, and Windows releases it when the editor exits.
        return NativeLibrary.Load(path);
    }
}
