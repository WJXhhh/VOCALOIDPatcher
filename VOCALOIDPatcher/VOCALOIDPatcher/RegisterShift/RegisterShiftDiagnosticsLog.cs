using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VOCALOIDPatcher.Config;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.RegisterShift;

internal static class RegisterShiftDiagnosticsLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VOCALOIDPatcher", "register-shift.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception exception)
        {
            Debug.Print($"Register shift diagnostics failed: {exception.Message}");
        }
    }

    internal static void WriteStatus(string boundary, IntPtr part = default)
    {
        var status = NativeRegisterShift.GetSnapshot();
        Write($"{boundary} part=0x{part.ToInt64():X} state={NativeRegisterShift.Status} " +
              $"install={status.InstallResult} install1a={status.OneAInstallResult} " +
              $"bitmap=0x{status.InstallBitmap:X} " +
              $"tables={status.PartCount} states={status.StateCount} " +
              $"prepare={status.Prepare1ACalls}/{status.Prepare1BCalls} resolved={status.ResolvedPartCalls} " +
              $"matches={status.CurrentMatches}/{status.TargetMatches} misses={status.MatchMisses} " +
              $"selector1b={status.Selector1BCalls} " +
              $"prune1a={status.Prune1ACalls} score1a={status.Score1ACalls} " +
              $"scratch1a={status.Scratch1ACalls} applied={status.Applied1ACalls}/{status.Applied1BCalls} " +
              $"callsiteMiss={status.CallsiteMisses} lastPart=0x{status.LastPart:X} " +
              $"epoch={status.LastEpoch} dseMode={status.LastMode} " +
              $"vsmModeCandidate={status.LastVsmMode} slot={status.LastSlot} " +
              $"thread={status.LastThread} " +
              $"outer=0x{status.LastOuter:X} parser=0x{status.LastParser:X} " +
              $"synthesis=0x{status.LastSynthesis:X} " +
              $"record={status.LastBeginFrame}+{status.LastDurationFrames} " +
              $"pitch={BitConverter.Int32BitsToSingle(unchecked((int)status.LastPitchBits)):0.###} " +
              $"selection={status.LastCurrentShift}:0x{status.LastCurrentSelection:X}/" +
              $"{status.LastCurrentSelectionCount}," +
              $"{status.LastTargetShift}:0x{status.LastTargetSelection:X}/" +
              $"{status.LastTargetSelectionCount} " +
              $"pool={status.LastPoolShift}:0x{status.LastPoolSignature:X}/" +
              $"{status.LastPoolCount}/" +
              $"{BitConverter.Int32BitsToSingle(unchecked((int)status.LastPoolPitchMinBits)):0.###}.." +
              $"{BitConverter.Int32BitsToSingle(unchecked((int)status.LastPoolPitchMaxBits)):0.###} " +
              $"sequence=0x{status.RenderOutputSignature:X}/0x{status.RenderInputSignature:X}/" +
              $"{status.RenderScopeCalls} " +
              $"scopes={FormatScope(status.Scope0Context, status.Scope0Output, status.Scope0Input)};" +
              $"{FormatScope(status.Scope1Context, status.Scope1Output, status.Scope1Input)};" +
              $"{FormatScope(status.Scope2Context, status.Scope2Output, status.Scope2Input)}");
    }

    private static string FormatScope(ulong context, ulong output, ulong input)
    {
        var current = unchecked((int)(uint)context);
        var target = unchecked((int)(uint)(context >> 32));
        return $"{current},{target}:0x{output:X}/0x{input:X}";
    }

    internal static void WriteRenderedFlags(string boundary, WIVSMMidiPart part, ulong epoch)
    {
        var handle = (IntPtr)part;
        string rawWave = "?";
        string rawScore = "?";
        try
        {
            if (handle != IntPtr.Zero && NativeRegisterShift.Status == RegisterShiftStatus.Installed)
            {
                rawWave = (Marshal.ReadByte(handle, 0x4c1) != 0).ToString();
                rawScore = (Marshal.ReadByte(handle, 0x4c2) != 0).ToString();
            }
        }
        catch (Exception exception)
        {
            Write($"{boundary} raw-valid-read failed part=0x{handle.ToInt64():X} " +
                  $"epoch={epoch} error={exception.GetType().Name}");
        }
        Write($"{boundary} part=0x{handle.ToInt64():X} epoch={epoch} " +
              $"rawValidScore={rawScore} rawValidWave={rawWave} " +
              $"effectiveValidScore={part.HasValidRenderedScore} " +
              $"effectiveValidWave={part.HasValidRenderedWave} " +
              $"extendedChinesePinyin={Settings.ExtendedChinesePinyin}");
    }
}
