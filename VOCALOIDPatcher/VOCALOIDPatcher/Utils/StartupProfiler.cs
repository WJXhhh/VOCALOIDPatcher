using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace VOCALOIDPatcher.Utils;

internal static class StartupProfiler
{
    private static readonly long ProfilerStart = Stopwatch.GetTimestamp();
    private static readonly ConcurrentDictionary<MethodBase, string> Labels = new();
    private static readonly object LogLock = new();
    private static string? _logPath;
    private static int _sequence;

    public static string EnabledPath => Path.Combine(Patcher.ConfigDir, "startup-profile.enabled");

    private static bool IsEnabled
    {
        get
        {
            try { return File.Exists(EnabledPath); }
            catch { return false; }
        }
    }

    public static void InitializeLog()
    {
        if (!IsEnabled)
            return;

        try
        {
            _logPath = Path.Combine(Patcher.ConfigDir, "startup-profile.log");
            var processAge = DateTime.Now - Process.GetCurrentProcess().StartTime;
            File.WriteAllText(
                _logPath,
                $"VOCALOID Patcher startup profile{Environment.NewLine}" +
                $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}{Environment.NewLine}" +
                $"Editor: {typeof(Yamaha.VOCALOID.App).Assembly.GetName().Version}{Environment.NewLine}" +
                $"Patcher: {Patcher.Version}{Environment.NewLine}" +
                $"Process age when profiler loaded: {processAge.TotalMilliseconds:F1} ms{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            _logPath = null;
        }

        LogMilestone("Profiler loaded");
    }

    public static void LogMilestone(string label, double? durationMilliseconds = null)
    {
        if (_logPath == null)
            return;

        var total = Stopwatch.GetElapsedTime(ProfilerStart).TotalMilliseconds;
        var duration = durationMilliseconds.HasValue
            ? $" | duration {durationMilliseconds.Value.ToString("F1", CultureInfo.InvariantCulture),9} ms"
            : string.Empty;
        Write($"[{total,10:F1} ms] {label}{duration}");
    }

    public static void Install(Harmony harmony)
    {
        if (_logPath == null)
            return;

        var prefix = new HarmonyMethod(typeof(StartupProfiler), nameof(Prefix));
        var finalizer = new HarmonyMethod(typeof(StartupProfiler), nameof(Finalizer));

        InstallMethod(harmony, prefix, finalizer, "V6 total module initialization", "Yamaha.VOCALOID.App", "InitializeModule");
        InstallMethod(harmony, prefix, finalizer, "Authorization validation", "Yamaha.VOCALOID.App", "ValidateAuthorization");
        InstallMethod(harmony, prefix, finalizer, "Voicebank component enumeration", "Yamaha.VOCALOID.App", "CreateComponents");
        InstallMethod(harmony, prefix, finalizer, "Style file loading", "Yamaha.VOCALOID.App", "LoadStyle");
        InstallMethod(harmony, prefix, finalizer, "Shortcut file loading", "Yamaha.VOCALOID.App", "LoadSystemShortcutManagers");
        InstallMethod(harmony, prefix, finalizer, "Lua controller initialization", "Yamaha.VOCALOID.App", "InitializeLuaController");
        InstallMethod(harmony, prefix, finalizer, "Effect and audio engine initialization", "Yamaha.VOCALOID.App", "InitEngine");

        InstallMethod(harmony, prefix, finalizer, "VDM database creation", "Yamaha.VOCALOID.VDM.DatabaseManagerIF", "CreateDatabaseManager");
        InstallMethod(harmony, prefix, finalizer, "DSE manager creation", "Yamaha.VOCALOID.DSE.DSEManagerIF", "CreateManager");
        InstallMethod(harmony, prefix, finalizer, "G2PA language manager creation", "Yamaha.VOCALOID.G2PA.G2PAManagerIF", "CreateManager");
        InstallMethod(harmony, prefix, finalizer, "VSM manager creation", "Yamaha.VOCALOID.VSM.WVSMModuleIF", "CreateManager");
        InstallMethod(harmony, prefix, finalizer, "Style manager creation", "Yamaha.VOCALOID.VSStyle.StyleManagerIF", "CreateStyleManager");
        InstallMethod(harmony, prefix, finalizer, "Media manager creation", "Yamaha.VOCALOID.VMM.MediaManagerIF", "CreateMediaManager");

        InstallMethod(harmony, prefix, finalizer, "Editor update check", "Yamaha.VOCALOID.VSGKit.VSGAppVersionManager", "ExecuteAppVersionCheck");
        InstallMethod(harmony, prefix, finalizer, "Voicebank update check", "Yamaha.VOCALOID.VSGKit.VSGVoiceBankVersionManager", "ExecuteVoiceBankVersionCheck");
        InstallMethod(harmony, prefix, finalizer, "Contents update check", "Yamaha.VOCALOID.VSGKit.VSGContentsManager", "ExecuteContentsCheck");

        InstallConstructors(harmony, prefix, finalizer, "Audio player construction", "Yamaha.VOCALOID.AudioPlayer");
        InstallConstructors(harmony, prefix, finalizer, "Lua manager construction", "Yamaha.VOCALOID.LuaManager");
        LogMilestone($"Startup timing hooks installed ({Labels.Count} methods)");
    }

    private static void InstallMethod(
        Harmony harmony,
        HarmonyMethod prefix,
        HarmonyMethod finalizer,
        string label,
        string typeName,
        string methodName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            Write($"[warning] Startup profiler type not found: {typeName}");
            return;
        }

        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .Where(method => method.Name == methodName))
            InstallTarget(harmony, prefix, finalizer, label, method);
    }

    private static void InstallConstructors(
        Harmony harmony,
        HarmonyMethod prefix,
        HarmonyMethod finalizer,
        string label,
        string typeName)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type == null)
        {
            Write($"[warning] Startup profiler type not found: {typeName}");
            return;
        }

        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            InstallTarget(harmony, prefix, finalizer, label, constructor);
    }

    private static void InstallTarget(
        Harmony harmony,
        HarmonyMethod prefix,
        HarmonyMethod finalizer,
        string label,
        MethodBase target)
    {
        try
        {
            Labels[target] = label;
            harmony.Patch(target, prefix: prefix, finalizer: finalizer);
        }
        catch (Exception e)
        {
            Labels.TryRemove(target, out _);
            Write($"[warning] Could not profile {target.DeclaringType?.FullName}.{target.Name}: {e.Message}");
        }
    }

    private static void Prefix(out long __state)
    {
        __state = Stopwatch.GetTimestamp();
    }

    private static Exception? Finalizer(MethodBase __originalMethod, long __state, Exception? __exception)
    {
        if (!Labels.TryGetValue(__originalMethod, out var label))
            label = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";

        var sequence = System.Threading.Interlocked.Increment(ref _sequence);
        var duration = Stopwatch.GetElapsedTime(__state).TotalMilliseconds;
        var suffix = __exception == null ? string.Empty : $" | threw {__exception.GetType().Name}";
        LogMilestone($"#{sequence:D2} {label}{suffix}", duration);
        return __exception;
    }

    private static void Write(string message)
    {
        Debug.Print($"[Startup] {message}");
        if (_logPath == null)
            return;

        try
        {
            lock (LogLock)
                File.AppendAllText(_logPath, message + Environment.NewLine);
        }
        catch
        {
            // Profiling must never affect editor startup.
        }
    }
}
