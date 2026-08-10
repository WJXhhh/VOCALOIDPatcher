using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.Utils.Audio;

/// <summary>
/// Copies the PCM that VAE writes to its DirectSound output buffer. This tap is
/// upstream of the Windows audio-session and endpoint volume controls.
/// </summary>
public static unsafe class DirectSoundPcmTap
{
    private const int RingSize = 16384;
    private const int RingMask = RingSize - 1;
    private const int MaxHookRecords = 4;
    private const int MaxDirectSoundObjects = 16;

    private const uint DsbcapsPrimaryBuffer = 0x00000001;
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const uint PageReadWrite = 0x04;
    private const int EFail = unchecked((int)0x80004005);

    private static readonly object InstallLock = new();
    private static readonly object HookLock = new();
    private static readonly object DirectSoundObjectsLock = new();
    private static readonly float[] Ring = new float[RingSize];
    private static readonly CreateSoundBufferHookRecord?[] CreateSoundBufferHooks =
        new CreateSoundBufferHookRecord?[MaxHookRecords];
    private static readonly LockHookRecord?[] LockHooks = new LockHookRecord?[MaxHookRecords];
    private static readonly UnlockHookRecord?[] UnlockHooks = new UnlockHookRecord?[MaxHookRecords];
    private static readonly nint[] DirectSoundObjects = new nint[MaxDirectSoundObjects];

    private static readonly DirectSoundCreateDelegate DirectSoundCreateHookDelegate = DirectSoundCreateHook;
    private static readonly DirectSoundCreateDelegate DirectSoundCreate8HookDelegate = DirectSoundCreate8Hook;
    private static readonly CreateSoundBufferDelegate CreateSoundBufferHookDelegate = CreateSoundBufferHook;
    private static readonly LockDelegate LockHookDelegate = LockHook;
    private static readonly UnlockDelegate UnlockHookDelegate = UnlockHook;

    private static readonly nint DirectSoundCreateHookPointer =
        Marshal.GetFunctionPointerForDelegate(DirectSoundCreateHookDelegate);
    private static readonly nint DirectSoundCreate8HookPointer =
        Marshal.GetFunctionPointerForDelegate(DirectSoundCreate8HookDelegate);
    private static readonly nint CreateSoundBufferHookPointer =
        Marshal.GetFunctionPointerForDelegate(CreateSoundBufferHookDelegate);
    private static readonly nint LockHookPointer = Marshal.GetFunctionPointerForDelegate(LockHookDelegate);
    private static readonly nint UnlockHookPointer = Marshal.GetFunctionPointerForDelegate(UnlockHookDelegate);

    private static DirectSoundCreateDelegate? _originalDirectSoundCreate;
    private static DirectSoundCreateDelegate? _originalDirectSoundCreate8;
    private static bool _installAttempted;
    private static bool _installed;
    private static int _directSoundObjectCursor;

    private static long _outputBuffer;
    private static int _sampleRate = 44100;
    private static int _channels = 2;
    private static int _blockAlign = 4;
    private static int _bufferBytes;
    private static int _sampleEncoding = (int)SampleEncoding.Pcm16;
    private static int _writeIndex;
    private static long _lastWriteTick;
    private static int _readerCount;
    private static long _lastQueueMeasurementTick;
    private static int _queueMeasurementCount;
    private static double _queueLatencySeconds;
    private static double _queueJitterSeconds;

    public static bool Installed => _installed;

    public static bool HasOutputBuffer => Interlocked.Read(ref _outputBuffer) != 0;

    internal static bool TryGetLatencyInfo(out DirectSoundLatencyInfo info)
    {
        var lastMeasurement = Volatile.Read(ref _lastQueueMeasurementTick);
        var count = Volatile.Read(ref _queueMeasurementCount);
        if (lastMeasurement == 0 || Environment.TickCount64 - lastMeasurement > 500 || count <= 0)
        {
            info = default;
            return false;
        }

        var sampleRate = Volatile.Read(ref _sampleRate);
        var blockAlign = Volatile.Read(ref _blockAlign);
        var bufferBytes = Volatile.Read(ref _bufferBytes);
        info = new DirectSoundLatencyInfo(
            Volatile.Read(ref _queueLatencySeconds),
            Volatile.Read(ref _queueJitterSeconds),
            count,
            sampleRate,
            blockAlign > 0 ? bufferBytes / blockAlign : 0);
        return true;
    }

    /// <summary>
    /// Installs hooks into VAE's own DirectSound imports. The installed DLL is
    /// never modified; only function pointers in the current process are changed.
    /// </summary>
    public static bool Install()
    {
        lock (InstallLock)
        {
            if (_installAttempted) return _installed;
            _installAttempted = true;

            try
            {
                if (nint.Size != 8) return false;

                var vaePath = Path.Combine(AppContext.BaseDirectory, "VAE.dll");
                if (!File.Exists(vaePath)) return false;

                var module = NativeLibrary.Load(vaePath);
                var patched = false;

                if (TryPatchImportByOrdinal(module, "DSOUND.dll", 1,
                        DirectSoundCreateHookPointer, out var createPointer))
                {
                    _originalDirectSoundCreate =
                        Marshal.GetDelegateForFunctionPointer<DirectSoundCreateDelegate>(createPointer);
                    patched = true;
                }

                if (TryPatchImportByOrdinal(module, "DSOUND.dll", 11,
                        DirectSoundCreate8HookPointer, out var create8Pointer))
                {
                    _originalDirectSoundCreate8 =
                        Marshal.GetDelegateForFunctionPointer<DirectSoundCreateDelegate>(create8Pointer);
                    patched = true;
                }

                _installed = patched;
            }
            catch
            {
                _installed = false;
            }

            return _installed;
        }
    }

    public static void Start()
    {
        if (Interlocked.Increment(ref _readerCount) != 1) return;

        Array.Clear(Ring, 0, Ring.Length);
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _lastWriteTick, 0);
    }

    public static void Stop()
    {
        var readers = Interlocked.Decrement(ref _readerCount);
        if (readers > 0) return;
        if (readers < 0) Interlocked.Exchange(ref _readerCount, 0);

        Array.Clear(Ring, 0, Ring.Length);
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _lastWriteTick, 0);
    }

    public static bool TryReadLatest(float[] destination, out int sampleRate)
    {
        sampleRate = Volatile.Read(ref _sampleRate);
        if (Volatile.Read(ref _readerCount) <= 0 || !HasOutputBuffer)
            return false;

        var lastWrite = Volatile.Read(ref _lastWriteTick);
        if (lastWrite == 0 || Environment.TickCount64 - lastWrite > 250)
            return false;

        var end = Volatile.Read(ref _writeIndex);
        var start = end - destination.Length;
        for (var i = 0; i < destination.Length; i++)
            destination[i] = Ring[(start + i) & RingMask];

        return true;
    }

    private static int DirectSoundCreateHook(Guid* deviceGuid, nint* directSound, nint outer)
    {
        var original = _originalDirectSoundCreate;
        if (original == null) return EFail;

        int result;
        try
        {
            result = original(deviceGuid, directSound, outer);
        }
        catch
        {
            return EFail;
        }

        if (result >= 0 && directSound != null && *directSound != 0)
        {
            try
            {
                RegisterDirectSoundObject(*directSound);
                InstallCreateSoundBufferHook(*directSound);
            }
            catch
            {
                // Never turn a successful DirectSound call into an engine failure.
            }
        }

        return result;
    }

    private static int DirectSoundCreate8Hook(Guid* deviceGuid, nint* directSound, nint outer)
    {
        var original = _originalDirectSoundCreate8;
        if (original == null) return EFail;

        int result;
        try
        {
            result = original(deviceGuid, directSound, outer);
        }
        catch
        {
            return EFail;
        }

        if (result >= 0 && directSound != null && *directSound != 0)
        {
            try
            {
                RegisterDirectSoundObject(*directSound);
                InstallCreateSoundBufferHook(*directSound);
            }
            catch
            {
                // Never turn a successful DirectSound call into an engine failure.
            }
        }

        return result;
    }

    private static int CreateSoundBufferHook(nint directSound, DsBufferDescription* description,
        nint* soundBuffer, nint outer)
    {
        var hook = FindCreateSoundBufferHook(directSound);
        if (hook == null) return EFail;

        int result;
        try
        {
            result = hook.Original(directSound, description, soundBuffer, outer);
        }
        catch
        {
            return EFail;
        }

        if (result >= 0 && description != null && soundBuffer != null && *soundBuffer != 0)
        {
            try
            {
                if (IsDirectSoundObject(directSound))
                    TryRegisterOutputBuffer(*soundBuffer, *description);
            }
            catch
            {
                // Capturing is optional; the VAE buffer remains valid without it.
            }
        }

        return result;
    }

    private static int UnlockHook(nint soundBuffer, nint audio1, uint bytes1, nint audio2, uint bytes2)
    {
        var hook = FindUnlockHook(soundBuffer);
        if (hook == null) return EFail;

        try
        {
            if (soundBuffer == (nint)Interlocked.Read(ref _outputBuffer) &&
                Volatile.Read(ref _readerCount) > 0)
            {
                AppendBlock(audio1, bytes1);
                AppendBlock(audio2, bytes2);
            }
        }
        catch
        {
            // The original DirectSound call must still run if the tap fails.
        }

        try
        {
            return hook.Original(soundBuffer, audio1, bytes1, audio2, bytes2);
        }
        catch
        {
            return EFail;
        }
    }

    private static int LockHook(nint soundBuffer, uint offset, uint bytes,
        nint* audio1, uint* bytes1, nint* audio2, uint* bytes2, uint flags)
    {
        var hook = FindLockHook(soundBuffer);
        if (hook == null) return EFail;

        int result;
        try
        {
            result = hook.Original(soundBuffer, offset, bytes, audio1, bytes1, audio2, bytes2, flags);
        }
        catch
        {
            return EFail;
        }

        if (result >= 0 && soundBuffer == (nint)Interlocked.Read(ref _outputBuffer))
        {
            try
            {
                ObserveQueueLatency(soundBuffer, hook.GetCurrentPosition, offset, bytes);
            }
            catch
            {
                // Measuring latency must never change a successful buffer lock.
            }
        }

        return result;
    }

    private static void TryRegisterOutputBuffer(nint soundBuffer, DsBufferDescription description)
    {
        if ((description.Flags & DsbcapsPrimaryBuffer) != 0 || description.BufferBytes == 0 ||
            description.Format == 0)
            return;

        var format = *(WaveFormatEx*)description.Format;
        if (format.Channels == 0 || format.SampleRate == 0 || format.BlockAlign == 0)
            return;

        SampleEncoding encoding;
        if (format.FormatTag == WaveFormatPcm && format.BitsPerSample == 16)
            encoding = SampleEncoding.Pcm16;
        else if (format.FormatTag == WaveFormatPcm && format.BitsPerSample == 24)
            encoding = SampleEncoding.Pcm24;
        else if (format.FormatTag == WaveFormatPcm && format.BitsPerSample == 32)
            encoding = SampleEncoding.Pcm32;
        else if (format.FormatTag == WaveFormatIeeeFloat && format.BitsPerSample == 32)
            encoding = SampleEncoding.Float32;
        else
            return;

        var bytesPerSample = format.BitsPerSample / 8;
        if (format.BlockAlign < format.Channels * bytesPerSample ||
            !InstallLockHook(soundBuffer) || !InstallUnlockHook(soundBuffer))
            return;

        var previousBuffer = Interlocked.Read(ref _outputBuffer);
        Volatile.Write(ref _sampleRate, (int)format.SampleRate);
        Volatile.Write(ref _channels, format.Channels);
        Volatile.Write(ref _blockAlign, format.BlockAlign);
        Volatile.Write(ref _bufferBytes, (int)description.BufferBytes);
        Volatile.Write(ref _sampleEncoding, (int)encoding);
        Interlocked.Exchange(ref _outputBuffer, soundBuffer);

        if (previousBuffer != soundBuffer)
        {
            Volatile.Write(ref _lastQueueMeasurementTick, 0);
            Volatile.Write(ref _queueMeasurementCount, 0);
            Volatile.Write(ref _queueLatencySeconds, 0.0);
            Volatile.Write(ref _queueJitterSeconds, 0.0);
        }
    }

    private static void ObserveQueueLatency(nint soundBuffer,
        GetCurrentPositionDelegate getPosition, uint offset, uint bytes)
    {
        var bufferBytes = Volatile.Read(ref _bufferBytes);
        var sampleRate = Volatile.Read(ref _sampleRate);
        var blockAlign = Volatile.Read(ref _blockAlign);
        if (bufferBytes <= 0 || sampleRate <= 0 || blockAlign <= 0 ||
            bytes == 0 || bytes >= bufferBytes)
            return;

        uint playCursor;
        if (getPosition(soundBuffer, &playCursor, null) < 0) return;

        var normalizedOffset = offset % (uint)bufferBytes;
        var distance = normalizedOffset >= playCursor
            ? normalizedOffset - playCursor
            : normalizedOffset + (uint)bufferBytes - playCursor;
        var latency = distance / ((double)sampleRate * blockAlign);
        if (!double.IsFinite(latency) || latency is < 0.0 or > 0.5) return;

        var count = Volatile.Read(ref _queueMeasurementCount);
        var previous = Volatile.Read(ref _queueLatencySeconds);
        var updated = count == 0 ? latency : previous * 0.9 + latency * 0.1;
        var deviation = Math.Abs(latency - updated);
        var previousJitter = Volatile.Read(ref _queueJitterSeconds);

        Volatile.Write(ref _queueLatencySeconds, updated);
        Volatile.Write(ref _queueJitterSeconds,
            count == 0 ? 0.0 : previousJitter * 0.9 + deviation * 0.1);
        Volatile.Write(ref _queueMeasurementCount, count == int.MaxValue ? count : count + 1);
        Volatile.Write(ref _lastQueueMeasurementTick, Environment.TickCount64);
    }

    private static void AppendBlock(nint audio, uint byteCount)
    {
        if (audio == 0 || byteCount == 0) return;

        var channels = Volatile.Read(ref _channels);
        var blockAlign = Volatile.Read(ref _blockAlign);
        var encoding = (SampleEncoding)Volatile.Read(ref _sampleEncoding);
        if (channels <= 0 || blockAlign <= 0) return;

        var frames = (int)(byteCount / (uint)blockAlign);
        var source = (byte*)audio;
        var bytesPerSample = encoding == SampleEncoding.Pcm24 ? 3 :
            encoding == SampleEncoding.Pcm16 ? 2 : 4;
        var writeIndex = Volatile.Read(ref _writeIndex);

        for (var frame = 0; frame < frames; frame++)
        {
            var frameStart = source + frame * blockAlign;
            float mono = 0;
            for (var channel = 0; channel < channels; channel++)
                mono += ReadSample(frameStart + channel * bytesPerSample, encoding);

            Ring[writeIndex & RingMask] = mono / channels;
            writeIndex++;
        }

        Volatile.Write(ref _writeIndex, writeIndex);
        Volatile.Write(ref _lastWriteTick, Environment.TickCount64);
    }

    private static float ReadSample(byte* sample, SampleEncoding encoding)
    {
        switch (encoding)
        {
            case SampleEncoding.Pcm16:
                return *(short*)sample / 32768f;
            case SampleEncoding.Pcm24:
            {
                var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            case SampleEncoding.Pcm32:
                return *(int*)sample / 2147483648f;
            case SampleEncoding.Float32:
            {
                var value = *(float*)sample;
                return float.IsFinite(value) ? value : 0;
            }
            default:
                return 0;
        }
    }

    private static void RegisterDirectSoundObject(nint directSound)
    {
        lock (DirectSoundObjectsLock)
        {
            foreach (var item in DirectSoundObjects)
                if (item == directSound)
                    return;

            DirectSoundObjects[_directSoundObjectCursor] = directSound;
            _directSoundObjectCursor = (_directSoundObjectCursor + 1) % DirectSoundObjects.Length;
        }
    }

    private static bool IsDirectSoundObject(nint directSound)
    {
        lock (DirectSoundObjectsLock)
        {
            foreach (var item in DirectSoundObjects)
                if (item == directSound)
                    return true;
        }

        return false;
    }

    private static void InstallCreateSoundBufferHook(nint directSound)
    {
        var vtable = Marshal.ReadIntPtr(directSound);
        var slot = vtable + 3 * nint.Size;

        lock (HookLock)
        {
            foreach (var hook in CreateSoundBufferHooks)
                if (hook?.Vtable == vtable)
                    return;

            var empty = FindEmptySlot(CreateSoundBufferHooks);
            if (empty < 0) return;

            var originalPointer = Marshal.ReadIntPtr(slot);
            var hookRecord = new CreateSoundBufferHookRecord(vtable,
                Marshal.GetDelegateForFunctionPointer<CreateSoundBufferDelegate>(originalPointer));
            Volatile.Write(ref CreateSoundBufferHooks[empty], hookRecord);

            if (!WriteFunctionPointer(slot, CreateSoundBufferHookPointer))
                Volatile.Write(ref CreateSoundBufferHooks[empty], null);
        }
    }

    private static bool InstallUnlockHook(nint soundBuffer)
    {
        var vtable = Marshal.ReadIntPtr(soundBuffer);
        var slot = vtable + 19 * nint.Size;

        lock (HookLock)
        {
            foreach (var hook in UnlockHooks)
                if (hook?.Vtable == vtable)
                    return true;

            var empty = FindEmptySlot(UnlockHooks);
            if (empty < 0) return false;

            var originalPointer = Marshal.ReadIntPtr(slot);
            var hookRecord = new UnlockHookRecord(vtable,
                Marshal.GetDelegateForFunctionPointer<UnlockDelegate>(originalPointer));
            Volatile.Write(ref UnlockHooks[empty], hookRecord);

            if (WriteFunctionPointer(slot, UnlockHookPointer)) return true;

            Volatile.Write(ref UnlockHooks[empty], null);
            return false;
        }
    }

    private static bool InstallLockHook(nint soundBuffer)
    {
        var vtable = Marshal.ReadIntPtr(soundBuffer);
        var slot = vtable + 11 * nint.Size;

        lock (HookLock)
        {
            foreach (var hook in LockHooks)
                if (hook?.Vtable == vtable)
                    return true;

            var empty = FindEmptySlot(LockHooks);
            if (empty < 0) return false;

            var originalPointer = Marshal.ReadIntPtr(slot);
            var getPositionPointer = Marshal.ReadIntPtr(vtable + 4 * nint.Size);
            if (originalPointer == 0 || getPositionPointer == 0) return false;
            var hookRecord = new LockHookRecord(vtable,
                Marshal.GetDelegateForFunctionPointer<LockDelegate>(originalPointer),
                Marshal.GetDelegateForFunctionPointer<GetCurrentPositionDelegate>(getPositionPointer));
            Volatile.Write(ref LockHooks[empty], hookRecord);

            if (WriteFunctionPointer(slot, LockHookPointer)) return true;

            Volatile.Write(ref LockHooks[empty], null);
            return false;
        }
    }

    private static CreateSoundBufferHookRecord? FindCreateSoundBufferHook(nint directSound)
    {
        var vtable = Marshal.ReadIntPtr(directSound);
        for (var i = 0; i < CreateSoundBufferHooks.Length; i++)
        {
            var hook = Volatile.Read(ref CreateSoundBufferHooks[i]);
            if (hook?.Vtable == vtable) return hook;
        }

        return null;
    }

    private static UnlockHookRecord? FindUnlockHook(nint soundBuffer)
    {
        var vtable = Marshal.ReadIntPtr(soundBuffer);
        for (var i = 0; i < UnlockHooks.Length; i++)
        {
            var hook = Volatile.Read(ref UnlockHooks[i]);
            if (hook?.Vtable == vtable) return hook;
        }

        return null;
    }

    private static LockHookRecord? FindLockHook(nint soundBuffer)
    {
        var vtable = Marshal.ReadIntPtr(soundBuffer);
        for (var i = 0; i < LockHooks.Length; i++)
        {
            var hook = Volatile.Read(ref LockHooks[i]);
            if (hook?.Vtable == vtable) return hook;
        }

        return null;
    }

    private static int FindEmptySlot<T>(T?[] records) where T : class
    {
        for (var i = 0; i < records.Length; i++)
            if (records[i] == null)
                return i;
        return -1;
    }

    private static bool TryPatchImportByOrdinal(nint module, string importedDll, ushort ordinal,
        nint replacement, out nint original)
    {
        original = 0;
        var image = (byte*)module;
        if (*(ushort*)image != 0x5A4D) return false;

        var peOffset = *(int*)(image + 0x3C);
        if (peOffset <= 0) return false;

        var ntHeaders = image + peOffset;
        if (*(uint*)ntHeaders != 0x00004550) return false;

        var optionalHeader = ntHeaders + 24;
        if (*(ushort*)optionalHeader != 0x20B) return false;

        var importRva = *(uint*)(optionalHeader + 112 + 8);
        if (importRva == 0) return false;

        var descriptor = (ImageImportDescriptor*)(image + importRva);
        while (descriptor->Name != 0)
        {
            var name = Marshal.PtrToStringAnsi((nint)(image + descriptor->Name));
            if (string.Equals(name, importedDll, StringComparison.OrdinalIgnoreCase))
            {
                var lookupRva = descriptor->OriginalFirstThunk != 0
                    ? descriptor->OriginalFirstThunk
                    : descriptor->FirstThunk;
                var lookup = (ulong*)(image + lookupRva);
                var address = (nint*)(image + descriptor->FirstThunk);

                for (; *lookup != 0; lookup++, address++)
                {
                    const ulong ordinalFlag = 0x8000000000000000;
                    if ((*lookup & ordinalFlag) == 0 || (ushort)(*lookup & 0xFFFF) != ordinal)
                        continue;

                    original = *address;
                    return WriteFunctionPointer((nint)address, replacement);
                }

                return false;
            }

            descriptor++;
        }

        return false;
    }

    private static bool WriteFunctionPointer(nint slot, nint replacement)
    {
        if (!VirtualProtect(slot, (nuint)nint.Size, PageReadWrite, out var oldProtect))
            return false;

        try
        {
            Marshal.WriteIntPtr(slot, replacement);
            return true;
        }
        finally
        {
            VirtualProtect(slot, (nuint)nint.Size, oldProtect, out _);
        }
    }

    private enum SampleEncoding
    {
        Pcm16,
        Pcm24,
        Pcm32,
        Float32
    }

    private sealed class CreateSoundBufferHookRecord(
        nint vtable,
        CreateSoundBufferDelegate original)
    {
        public nint Vtable { get; } = vtable;
        public CreateSoundBufferDelegate Original { get; } = original;
    }

    private sealed class UnlockHookRecord(nint vtable, UnlockDelegate original)
    {
        public nint Vtable { get; } = vtable;
        public UnlockDelegate Original { get; } = original;
    }

    private sealed class LockHookRecord(nint vtable, LockDelegate original,
        GetCurrentPositionDelegate getCurrentPosition)
    {
        public nint Vtable { get; } = vtable;
        public LockDelegate Original { get; } = original;
        public GetCurrentPositionDelegate GetCurrentPosition { get; } = getCurrentPosition;
    }

    internal readonly record struct DirectSoundLatencyInfo(
        double LatencySeconds,
        double JitterSeconds,
        int MeasurementCount,
        int SampleRate,
        int BufferFrames);

    [StructLayout(LayoutKind.Sequential)]
    private struct ImageImportDescriptor
    {
        public uint OriginalFirstThunk;
        public uint TimeDateStamp;
        public uint ForwarderChain;
        public uint Name;
        public uint FirstThunk;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DsBufferDescription
    {
        public uint Size;
        public uint Flags;
        public uint BufferBytes;
        public uint Reserved;
        public nint Format;
        public Guid Algorithm3D;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SampleRate;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DirectSoundCreateDelegate(Guid* deviceGuid, nint* directSound, nint outer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSoundBufferDelegate(nint directSound, DsBufferDescription* description,
        nint* soundBuffer, nint outer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockDelegate(nint soundBuffer, uint offset, uint bytes,
        nint* audio1, uint* bytes1, nint* audio2, uint* bytes2, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentPositionDelegate(nint soundBuffer, uint* playCursor, uint* writeCursor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockDelegate(nint soundBuffer, nint audio1, uint bytes1, nint audio2, uint bytes2);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);
}
