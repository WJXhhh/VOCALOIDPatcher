using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace VOCALOIDPatcher.Utils.Audio;

/// <summary>
/// Copies the output pages filled by VAE's ASIO callbacks. The tap runs after
/// VAE has mixed the master output and before the ASIO driver sends it to the
/// device, so Windows session and endpoint volume controls are not involved.
/// </summary>
public static unsafe class AsioPcmTap
{
    private const int RingSize = 16384;
    private const int RingMask = RingSize - 1;
    private const int MaxHookRecords = 16;
    private const int MaxSessions = 32;
    private const int MaxChannels = 64;
    private const int CreateBuffersVtableIndex = 19;
    private const int GetLatenciesVtableIndex = 10;
    private const int GetSampleRateVtableIndex = 13;
    private const int GetChannelInfoVtableIndex = 18;

    private const uint PageReadWrite = 0x04;
    private const int EFail = unchecked((int)0x80004005);

    private static readonly object InstallLock = new();
    private static readonly object HookLock = new();
    private static readonly object SessionLock = new();
    private static readonly float[] Ring = new float[RingSize];
    private static readonly CreateBuffersHookRecord?[] CreateBuffersHooks =
        new CreateBuffersHookRecord?[MaxHookRecords];
    private static readonly AsioCaptureSession?[] Sessions = new AsioCaptureSession?[MaxSessions];

    private static readonly CoCreateInstanceDelegate CoCreateInstanceHookDelegate = CoCreateInstanceHook;
    private static readonly CreateBuffersDelegate CreateBuffersHookDelegate = CreateBuffersHook;
    private static readonly nint CoCreateInstanceHookPointer =
        Marshal.GetFunctionPointerForDelegate(CoCreateInstanceHookDelegate);
    private static readonly nint CreateBuffersHookPointer =
        Marshal.GetFunctionPointerForDelegate(CreateBuffersHookDelegate);

    private static CoCreateInstanceDelegate? _originalCoCreateInstance;
    private static bool _installAttempted;
    private static bool _installed;
    private static int _hasOutputBuffer;
    private static int _sampleRate = 44100;
    private static int _writeIndex;
    private static long _lastWriteTick;
    private static int _readerCount;
    private static AsioCaptureSession? _activeSession;

    public static bool Installed => _installed;

    public static bool HasOutputBuffer => Volatile.Read(ref _hasOutputBuffer) != 0;

    internal static bool TryGetLatencyInfo(out AsioLatencyInfo info)
    {
        var session = Volatile.Read(ref _activeSession);
        if (session == null || !session.IsConfigured)
        {
            info = default;
            return false;
        }

        return session.TryGetLatencyInfo(out info);
    }

    /// <summary>
    /// Hooks only VAE's CoCreateInstance import, then hooks createBuffers on the
    /// ASIO objects VAE creates. No driver or Yamaha DLL is changed on disk.
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
                if (!TryPatchImportByName(module, "ole32.dll", "CoCreateInstance",
                        CoCreateInstanceHookPointer, out var originalPointer))
                    return false;

                _originalCoCreateInstance =
                    Marshal.GetDelegateForFunctionPointer<CoCreateInstanceDelegate>(originalPointer);
                _installed = true;
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

    private static int CoCreateInstanceHook(Guid* classId, nint outer, uint context,
        Guid* interfaceId, nint* instance)
    {
        var original = _originalCoCreateInstance;
        if (original == null) return EFail;

        int result;
        try
        {
            result = original(classId, outer, context, interfaceId, instance);
        }
        catch
        {
            return EFail;
        }

        if (result >= 0 && classId != null && interfaceId != null && instance != null && *instance != 0 &&
            *classId == *interfaceId)
        {
            try
            {
                InstallCreateBuffersHook(*instance);
            }
            catch
            {
                // ASIO capture is optional; never change a successful COM result.
            }
        }

        return result;
    }

    private static int CreateBuffersHook(nint asio, AsioBufferInfo* bufferInfos,
        int numChannels, int bufferSize, AsioCallbacks* callbacks)
    {
        var hook = FindCreateBuffersHook(asio);
        if (hook == null) return EFail;

        AsioCaptureSession? session = null;
        var callbacksForDriver = callbacks;
        if (bufferInfos != null && callbacks != null && numChannels is > 0 and <= MaxChannels &&
            bufferSize is > 0 and <= 262144)
        {
            try
            {
                session = new AsioCaptureSession(*callbacks);
                if (RegisterSession(session))
                    callbacksForDriver = session.Callbacks;
                else
                    session = null;
            }
            catch
            {
                session = null;
            }
        }

        int result;
        try
        {
            result = hook.Original(asio, bufferInfos, numChannels, bufferSize, callbacksForDriver);
        }
        catch
        {
            return EFail;
        }

        if (result == 0 && session != null)
        {
            try
            {
                session.Configure(asio, bufferInfos, numChannels, bufferSize);
                if (session.IsConfigured)
                {
                    Volatile.Write(ref _hasOutputBuffer, 1);
                    Volatile.Write(ref _activeSession, session);
                }
            }
            catch
            {
                // The forwarding callbacks remain valid even if capture setup fails.
            }
        }

        return result;
    }

    private static void InstallCreateBuffersHook(nint asio)
    {
        var vtable = Marshal.ReadIntPtr(asio);
        var slot = vtable + CreateBuffersVtableIndex * nint.Size;

        lock (HookLock)
        {
            foreach (var hook in CreateBuffersHooks)
                if (hook?.Vtable == vtable)
                    return;

            var empty = FindEmptySlot(CreateBuffersHooks);
            if (empty < 0) return;

            var originalPointer = Marshal.ReadIntPtr(slot);
            if (originalPointer == 0) return;

            var hookRecord = new CreateBuffersHookRecord(vtable,
                Marshal.GetDelegateForFunctionPointer<CreateBuffersDelegate>(originalPointer));
            Volatile.Write(ref CreateBuffersHooks[empty], hookRecord);

            if (!WriteFunctionPointer(slot, CreateBuffersHookPointer))
                Volatile.Write(ref CreateBuffersHooks[empty], null);
        }
    }

    private static CreateBuffersHookRecord? FindCreateBuffersHook(nint asio)
    {
        var vtable = Marshal.ReadIntPtr(asio);
        for (var i = 0; i < CreateBuffersHooks.Length; i++)
        {
            var hook = Volatile.Read(ref CreateBuffersHooks[i]);
            if (hook?.Vtable == vtable) return hook;
        }

        return null;
    }

    private static bool RegisterSession(AsioCaptureSession session)
    {
        lock (SessionLock)
        {
            var empty = FindEmptySlot(Sessions);
            if (empty < 0) return false;
            Volatile.Write(ref Sessions[empty], session);
            return true;
        }
    }

    private static int FindEmptySlot<T>(T?[] records) where T : class
    {
        for (var i = 0; i < records.Length; i++)
            if (records[i] == null)
                return i;
        return -1;
    }

    private static void AppendOutput(AsioOutputChannel[] outputs, int outputCount,
        int bufferSize, int bufferIndex, int sampleRate)
    {
        if (Volatile.Read(ref _readerCount) <= 0 || outputCount <= 0 || bufferIndex is < 0 or > 1)
            return;

        var writeIndex = Volatile.Read(ref _writeIndex);
        for (var frame = 0; frame < bufferSize; frame++)
        {
            float mono = 0;
            var channelsRead = 0;
            for (var channel = 0; channel < outputCount; channel++)
            {
                ref var output = ref outputs[channel];
                var buffer = bufferIndex == 0 ? output.Buffer0 : output.Buffer1;
                if (buffer == 0) continue;

                mono += ReadSample((byte*)buffer, frame, output.SampleType);
                channelsRead++;
            }

            Ring[writeIndex & RingMask] = channelsRead > 0 ? mono / channelsRead : 0;
            writeIndex++;
        }

        Volatile.Write(ref _sampleRate, sampleRate);
        Volatile.Write(ref _writeIndex, writeIndex);
        Volatile.Write(ref _lastWriteTick, Environment.TickCount64);
    }

    private static bool IsSupportedSampleType(AsioSampleType sampleType)
    {
        return sampleType is AsioSampleType.Int16Msb or AsioSampleType.Int24Msb or
            AsioSampleType.Int32Msb or AsioSampleType.Float32Msb or AsioSampleType.Float64Msb or
            AsioSampleType.Int32Msb16 or AsioSampleType.Int32Msb18 or
            AsioSampleType.Int32Msb20 or AsioSampleType.Int32Msb24 or
            AsioSampleType.Int16Lsb or AsioSampleType.Int24Lsb or
            AsioSampleType.Int32Lsb or AsioSampleType.Float32Lsb or AsioSampleType.Float64Lsb or
            AsioSampleType.Int32Lsb16 or AsioSampleType.Int32Lsb18 or
            AsioSampleType.Int32Lsb20 or AsioSampleType.Int32Lsb24;
    }

    private static float ReadSample(byte* buffer, int frame, AsioSampleType sampleType)
    {
        switch (sampleType)
        {
            case AsioSampleType.Int16Lsb:
                return *(short*)(buffer + frame * 2) / 32768f;
            case AsioSampleType.Int24Lsb:
            {
                var sample = buffer + frame * 3;
                var value = sample[0] | (sample[1] << 8) | (sample[2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            case AsioSampleType.Int32Lsb:
            case AsioSampleType.Int32Lsb16:
            case AsioSampleType.Int32Lsb18:
            case AsioSampleType.Int32Lsb20:
            case AsioSampleType.Int32Lsb24:
                return *(int*)(buffer + frame * 4) / 2147483648f;
            case AsioSampleType.Float32Lsb:
            {
                var value = *(float*)(buffer + frame * 4);
                return float.IsFinite(value) ? value : 0;
            }
            case AsioSampleType.Float64Lsb:
            {
                var value = *(double*)(buffer + frame * 8);
                return double.IsFinite(value) ? (float)value : 0;
            }
            case AsioSampleType.Int16Msb:
            {
                var sample = buffer + frame * 2;
                return (short)((sample[0] << 8) | sample[1]) / 32768f;
            }
            case AsioSampleType.Int24Msb:
            {
                var sample = buffer + frame * 3;
                var value = (sample[0] << 16) | (sample[1] << 8) | sample[2];
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            case AsioSampleType.Int32Msb:
            case AsioSampleType.Int32Msb16:
            case AsioSampleType.Int32Msb18:
            case AsioSampleType.Int32Msb20:
            case AsioSampleType.Int32Msb24:
            {
                var sample = buffer + frame * 4;
                var value = (sample[0] << 24) | (sample[1] << 16) | (sample[2] << 8) | sample[3];
                return value / 2147483648f;
            }
            case AsioSampleType.Float32Msb:
            {
                var sample = buffer + frame * 4;
                var bits = (sample[0] << 24) | (sample[1] << 16) | (sample[2] << 8) | sample[3];
                var value = BitConverter.Int32BitsToSingle(bits);
                return float.IsFinite(value) ? value : 0;
            }
            case AsioSampleType.Float64Msb:
            {
                var sample = buffer + frame * 8;
                ulong bits = ((ulong)sample[0] << 56) | ((ulong)sample[1] << 48) |
                    ((ulong)sample[2] << 40) | ((ulong)sample[3] << 32) |
                    ((ulong)sample[4] << 24) | ((ulong)sample[5] << 16) |
                    ((ulong)sample[6] << 8) | sample[7];
                var value = BitConverter.Int64BitsToDouble((long)bits);
                return double.IsFinite(value) ? (float)value : 0;
            }
            default:
                return 0;
        }
    }

    private static bool TryPatchImportByName(nint module, string importedDll, string functionName,
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
                    if ((*lookup & ordinalFlag) != 0) continue;

                    var importByName = image + (uint)*lookup;
                    var importedName = Marshal.PtrToStringAnsi((nint)(importByName + 2));
                    if (!string.Equals(importedName, functionName, StringComparison.Ordinal))
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

    private sealed class AsioCaptureSession
    {
        private readonly BufferSwitchDelegate? _originalBufferSwitch;
        private readonly SampleRateDidChangeDelegate? _originalSampleRateDidChange;
        private readonly AsioMessageDelegate? _originalAsioMessage;
        private readonly BufferSwitchTimeInfoDelegate? _originalBufferSwitchTimeInfo;

        private readonly BufferSwitchDelegate _bufferSwitch;
        private readonly SampleRateDidChangeDelegate _sampleRateDidChange;
        private readonly AsioMessageDelegate _asioMessage;
        private readonly BufferSwitchTimeInfoDelegate _bufferSwitchTimeInfo;
        private readonly AsioOutputChannel[] _outputs = new AsioOutputChannel[MaxChannels];

        private int _outputCount;
        private int _bufferSize;
        private int _sampleRate = 44100;
        private int _configured;
        private int _driverLatencyAvailable;
        private int _outputLatencySamples;
        private long _lastCallbackTimestamp;
        private long _lastCallbackTick;
        private int _callbackCount;
        private double _callbackPeriodSeconds;
        private double _callbackJitterSeconds;

        public AsioCaptureSession(AsioCallbacks original)
        {
            if (original.BufferSwitch != 0)
                _originalBufferSwitch =
                    Marshal.GetDelegateForFunctionPointer<BufferSwitchDelegate>(original.BufferSwitch);
            if (original.SampleRateDidChange != 0)
                _originalSampleRateDidChange =
                    Marshal.GetDelegateForFunctionPointer<SampleRateDidChangeDelegate>(original.SampleRateDidChange);
            if (original.AsioMessage != 0)
                _originalAsioMessage =
                    Marshal.GetDelegateForFunctionPointer<AsioMessageDelegate>(original.AsioMessage);
            if (original.BufferSwitchTimeInfo != 0)
                _originalBufferSwitchTimeInfo =
                    Marshal.GetDelegateForFunctionPointer<BufferSwitchTimeInfoDelegate>(original.BufferSwitchTimeInfo);

            _bufferSwitch = BufferSwitch;
            _sampleRateDidChange = SampleRateDidChange;
            _asioMessage = AsioMessage;
            _bufferSwitchTimeInfo = BufferSwitchTimeInfo;

            RuntimeHelpers.PrepareDelegate(_bufferSwitch);
            RuntimeHelpers.PrepareDelegate(_sampleRateDidChange);
            RuntimeHelpers.PrepareDelegate(_asioMessage);
            RuntimeHelpers.PrepareDelegate(_bufferSwitchTimeInfo);

            Callbacks = (AsioCallbacks*)Marshal.AllocHGlobal(sizeof(AsioCallbacks));
            *Callbacks = new AsioCallbacks
            {
                BufferSwitch = _originalBufferSwitch != null
                    ? Marshal.GetFunctionPointerForDelegate(_bufferSwitch)
                    : 0,
                SampleRateDidChange = _originalSampleRateDidChange != null
                    ? Marshal.GetFunctionPointerForDelegate(_sampleRateDidChange)
                    : 0,
                AsioMessage = _originalAsioMessage != null
                    ? Marshal.GetFunctionPointerForDelegate(_asioMessage)
                    : 0,
                BufferSwitchTimeInfo = _originalBufferSwitchTimeInfo != null
                    ? Marshal.GetFunctionPointerForDelegate(_bufferSwitchTimeInfo)
                    : 0
            };
        }

        public AsioCallbacks* Callbacks { get; }

        public bool IsConfigured => Volatile.Read(ref _configured) != 0;

        public void Configure(nint asio, AsioBufferInfo* bufferInfos, int numChannels, int bufferSize)
        {
            var vtable = Marshal.ReadIntPtr(asio);
            var getSampleRatePointer = Marshal.ReadIntPtr(vtable + GetSampleRateVtableIndex * nint.Size);
            if (getSampleRatePointer != 0)
            {
                var getSampleRate = Marshal.GetDelegateForFunctionPointer<GetSampleRateDelegate>(getSampleRatePointer);
                double sampleRate;
                if (getSampleRate(asio, &sampleRate) == 0 && sampleRate is >= 8000 and <= 768000)
                    _sampleRate = (int)Math.Round(sampleRate);
            }

            var getLatenciesPointer = Marshal.ReadIntPtr(vtable + GetLatenciesVtableIndex * nint.Size);
            if (getLatenciesPointer != 0)
            {
                var getLatencies = Marshal.GetDelegateForFunctionPointer<GetLatenciesDelegate>(getLatenciesPointer);
                int inputLatency;
                int outputLatency;
                if (getLatencies(asio, &inputLatency, &outputLatency) == 0 &&
                    outputLatency > 0 && outputLatency <= _sampleRate / 4)
                {
                    Volatile.Write(ref _outputLatencySamples, outputLatency);
                    Volatile.Write(ref _driverLatencyAvailable, 1);
                }
            }

            var getChannelInfoPointer = Marshal.ReadIntPtr(vtable + GetChannelInfoVtableIndex * nint.Size);
            if (getChannelInfoPointer == 0) return;
            var getChannelInfo = Marshal.GetDelegateForFunctionPointer<GetChannelInfoDelegate>(getChannelInfoPointer);

            var outputCount = 0;
            for (var i = 0; i < numChannels && outputCount < _outputs.Length; i++)
            {
                ref var bufferInfo = ref bufferInfos[i];
                if (bufferInfo.IsInput != 0 || bufferInfo.Buffer0 == 0 || bufferInfo.Buffer1 == 0)
                    continue;

                var channelInfo = new AsioChannelInfo
                {
                    Channel = bufferInfo.ChannelNum,
                    IsInput = 0
                };
                if (getChannelInfo(asio, &channelInfo) != 0 || !IsSupportedSampleType(channelInfo.SampleType))
                    continue;

                _outputs[outputCount++] = new AsioOutputChannel
                {
                    Buffer0 = bufferInfo.Buffer0,
                    Buffer1 = bufferInfo.Buffer1,
                    SampleType = channelInfo.SampleType
                };
            }

            _bufferSize = bufferSize;
            Volatile.Write(ref _outputCount, outputCount);
            Volatile.Write(ref _configured, outputCount > 0 ? 1 : 0);
        }

        public bool TryGetLatencyInfo(out AsioLatencyInfo info)
        {
            if (!IsConfigured)
            {
                info = default;
                return false;
            }

            var sampleRate = Volatile.Read(ref _sampleRate);
            var driverReported = Volatile.Read(ref _driverLatencyAvailable) != 0;
            var outputLatencySamples = Volatile.Read(ref _outputLatencySamples);
            var latencySamples = driverReported ? outputLatencySamples : _bufferSize;
            var lastCallback = Volatile.Read(ref _lastCallbackTick);
            info = new AsioLatencyInfo(
                sampleRate > 0 ? (double)latencySamples / sampleRate : 0.0,
                driverReported,
                outputLatencySamples,
                _bufferSize,
                sampleRate,
                Volatile.Read(ref _callbackCount),
                Volatile.Read(ref _callbackPeriodSeconds),
                Volatile.Read(ref _callbackJitterSeconds),
                lastCallback != 0 && Environment.TickCount64 - lastCallback <= 500);
            return true;
        }

        private void BufferSwitch(int doubleBufferIndex, int directProcess)
        {
            ObserveCallback();
            try
            {
                _originalBufferSwitch?.Invoke(doubleBufferIndex, directProcess);
            }
            catch
            {
                return;
            }

            Capture(doubleBufferIndex);
        }

        private void SampleRateDidChange(double sampleRate)
        {
            if (sampleRate is >= 8000 and <= 768000)
                Volatile.Write(ref _sampleRate, (int)Math.Round(sampleRate));

            try
            {
                _originalSampleRateDidChange?.Invoke(sampleRate);
            }
            catch
            {
                // Never allow a managed exception to cross the ASIO callback boundary.
            }
        }

        private int AsioMessage(int selector, int value, nint message, nint option)
        {
            try
            {
                return _originalAsioMessage?.Invoke(selector, value, message, option) ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private nint BufferSwitchTimeInfo(nint parameters, int doubleBufferIndex, int directProcess)
        {
            ObserveCallback();
            nint result;
            try
            {
                result = _originalBufferSwitchTimeInfo?.Invoke(parameters, doubleBufferIndex, directProcess) ?? parameters;
            }
            catch
            {
                return parameters;
            }

            Capture(doubleBufferIndex);
            return result;
        }

        private void ObserveCallback()
        {
            var now = Stopwatch.GetTimestamp();
            var previousTimestamp = Volatile.Read(ref _lastCallbackTimestamp);
            if (previousTimestamp == 0)
            {
                Volatile.Write(ref _lastCallbackTimestamp, now);
                Volatile.Write(ref _lastCallbackTick, Environment.TickCount64);
                return;
            }

            var period = Stopwatch.GetElapsedTime(previousTimestamp, now).TotalSeconds;
            var sampleRate = Volatile.Read(ref _sampleRate);
            var expectedPeriod = sampleRate > 0 ? (double)_bufferSize / sampleRate : 0.0;

            // Some drivers invoke both callback variants for the same page. Do not
            // count the second near-simultaneous invocation as a real buffer period.
            if (expectedPeriod > 0.0 && period < expectedPeriod * 0.25)
                return;

            Volatile.Write(ref _lastCallbackTimestamp, now);
            Volatile.Write(ref _lastCallbackTick, Environment.TickCount64);
            if (!double.IsFinite(period) || period <= 0.0 ||
                (expectedPeriod > 0.0 && period > expectedPeriod * 4.0))
                return;

            var count = Volatile.Read(ref _callbackCount);
            var previousPeriod = Volatile.Read(ref _callbackPeriodSeconds);
            var updatedPeriod = count == 0 ? period : previousPeriod * 0.9 + period * 0.1;
            var deviation = Math.Abs(period - updatedPeriod);
            var previousJitter = Volatile.Read(ref _callbackJitterSeconds);

            Volatile.Write(ref _callbackPeriodSeconds, updatedPeriod);
            Volatile.Write(ref _callbackJitterSeconds,
                count == 0 ? 0.0 : previousJitter * 0.9 + deviation * 0.1);
            Volatile.Write(ref _callbackCount, count == int.MaxValue ? count : count + 1);
        }

        private void Capture(int doubleBufferIndex)
        {
            try
            {
                if (IsConfigured)
                    AppendOutput(_outputs, Volatile.Read(ref _outputCount), _bufferSize,
                        doubleBufferIndex, Volatile.Read(ref _sampleRate));
            }
            catch
            {
                // The driver must always regain control even if spectrum capture fails.
            }
        }
    }

    private sealed class CreateBuffersHookRecord(nint vtable, CreateBuffersDelegate original)
    {
        public nint Vtable { get; } = vtable;
        public CreateBuffersDelegate Original { get; } = original;
    }

    private struct AsioOutputChannel
    {
        public nint Buffer0;
        public nint Buffer1;
        public AsioSampleType SampleType;
    }

    internal readonly record struct AsioLatencyInfo(
        double LatencySeconds,
        bool DriverReported,
        int OutputLatencySamples,
        int BufferFrames,
        int SampleRate,
        int CallbackCount,
        double CallbackPeriodSeconds,
        double CallbackJitterSeconds,
        bool CallbackActive);

    private enum AsioSampleType
    {
        Int16Msb = 0,
        Int24Msb = 1,
        Int32Msb = 2,
        Float32Msb = 3,
        Float64Msb = 4,
        Int32Msb16 = 8,
        Int32Msb18 = 9,
        Int32Msb20 = 10,
        Int32Msb24 = 11,
        Int16Lsb = 16,
        Int24Lsb = 17,
        Int32Lsb = 18,
        Float32Lsb = 19,
        Float64Lsb = 20,
        Int32Lsb16 = 24,
        Int32Lsb18 = 25,
        Int32Lsb20 = 26,
        Int32Lsb24 = 27
    }

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
    private struct AsioBufferInfo
    {
        public int IsInput;
        public int ChannelNum;
        public nint Buffer0;
        public nint Buffer1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AsioCallbacks
    {
        public nint BufferSwitch;
        public nint SampleRateDidChange;
        public nint AsioMessage;
        public nint BufferSwitchTimeInfo;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct AsioChannelInfo
    {
        public int Channel;
        public int IsInput;
        public int IsActive;
        public int ChannelGroup;
        public AsioSampleType SampleType;
        public fixed byte Name[32];
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CoCreateInstanceDelegate(Guid* classId, nint outer, uint context,
        Guid* interfaceId, nint* instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBuffersDelegate(nint asio, AsioBufferInfo* bufferInfos,
        int numChannels, int bufferSize, AsioCallbacks* callbacks);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetLatenciesDelegate(nint asio, int* inputLatency, int* outputLatency);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSampleRateDelegate(nint asio, double* sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetChannelInfoDelegate(nint asio, AsioChannelInfo* channelInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BufferSwitchDelegate(int doubleBufferIndex, int directProcess);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SampleRateDidChangeDelegate(double sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AsioMessageDelegate(int selector, int value, nint message, nint option);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint BufferSwitchTimeInfoDelegate(nint parameters, int doubleBufferIndex,
        int directProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint newProtect,
        out uint oldProtect);
}
