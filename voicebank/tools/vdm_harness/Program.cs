using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string ImportName = "vdm";

    private static int Main()
    {
        string dllPath = Environment.GetEnvironmentVariable("VDM_HARNESS_DLL")
            ?? @"C:\Program Files\VOCALOID6\Editor\VDM.dll";
        string expressionLibrary = Environment.GetEnvironmentVariable("VDM_HARNESS_EXPLIB")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "VOCALOID6",
                "Explib");
        IntPtr nativeLibrary = NativeLibrary.Load(dllPath);

        NativeLibrary.SetDllImportResolver(
            Assembly.GetExecutingAssembly(),
            (libraryName, _, _) => libraryName == ImportName
                ? nativeLibrary
                : IntPtr.Zero);

        string? componentIdToDecode =
            Environment.GetEnvironmentVariable("VDM_HARNESS_DECODE_COMPONENT_ID");
        if (!string.IsNullOrWhiteSpace(componentIdToDecode))
        {
            return DecodeComponentId613(nativeLibrary, componentIdToDecode.Trim());
        }

        int result = -1;
        IntPtr manager = Native.VDM_createDatabaseManager("VOCALOID6", expressionLibrary, ref result);
        Console.WriteLine($"dll={dllPath}");
        Console.WriteLine($"expression_library={expressionLibrary}");
        Console.WriteLine($"create.result={result}");
        Console.WriteLine($"create.manager={(manager == IntPtr.Zero ? "null" : "non-null")}");

        if (manager == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            ulong count = Native.VDM_DatabaseManager_numVoiceBanks(manager, VoiceBankType.Dse).ToUInt64();
            Console.WriteLine($"voice_banks={count}");

            for (ulong index = 0; index < count; index++)
            {
                IntPtr voiceBank = Native.VDM_DatabaseManager_voiceBankByIndex(
                    manager,
                    new UIntPtr(index),
                    VoiceBankType.Dse);
                if (voiceBank == IntPtr.Zero)
                {
                    Console.WriteLine($"bank[{index}].handle=null");
                    continue;
                }

                int major = -1;
                int minor = -1;
                int revision = -1;
                bool hasVersion = Native.VDM_VoiceBank_version(
                    voiceBank,
                    ref major,
                    ref minor,
                    ref revision);

                string compId = ReadUtf16(Native.VDM_VoiceBank_compID(voiceBank));
                string drp = ReadUtf16(Native.VDM_VoiceBank_drp(voiceBank));
                string path = ReadUtf16(Native.VDM_VoiceBank_path(voiceBank));
                string date = ReadUtf16(Native.VDM_VoiceBank_date(voiceBank));
                string componentName = ReadUtf16(Native.VDM_VoiceBank_componentName(voiceBank));
                string name = ReadUtf16(Native.VDM_VoiceBank_name(voiceBank));
                string styleId = ReadUtf16(Native.VDM_VoiceBank_defaultStyleID(voiceBank));
                string groupName = ReadUtf16(Native.VDM_VoiceBank_groupName(voiceBank));

                ulong languageCount = Native.VDM_VoiceBank_langIDSize(voiceBank).ToUInt64();
                List<int> languages = new();
                for (ulong languageIndex = 0; languageIndex < languageCount; languageIndex++)
                {
                    languages.Add(Native.VDM_VoiceBank_langIDByIndex(
                        voiceBank,
                        new UIntPtr(languageIndex)));
                }

                Console.WriteLine($"bank[{index}].comp_id={compId}");
                Console.WriteLine($"bank[{index}].drp={drp} (length={drp.Length})");
                Console.WriteLine($"bank[{index}].component_name={componentName}");
                Console.WriteLine($"bank[{index}].name={name}");
                Console.WriteLine($"bank[{index}].path={path}");
                Console.WriteLine($"bank[{index}].date={date} (length={date.Length})");
                Console.WriteLine($"bank[{index}].version={(hasVersion ? $"{major}.{minor}.{revision}" : "invalid")}");
                Console.WriteLine($"bank[{index}].native_language={Native.VDM_VoiceBank_nativeLangID(voiceBank)}");
                Console.WriteLine($"bank[{index}].languages={string.Join(',', languages)}");
                Console.WriteLine($"bank[{index}].singer_id={Native.VDM_VoiceBank_singerID(voiceBank)}");
                Console.WriteLine($"bank[{index}].np_index={Native.VDM_VoiceBank_npIndex(voiceBank)}");
                Console.WriteLine($"bank[{index}].default_style_id={styleId}");
                Console.WriteLine($"bank[{index}].group_name={groupName}");
                Console.WriteLine($"bank[{index}].parameters={Native.VDM_VoiceBank_numParameters(voiceBank).ToUInt64()}");
                Console.WriteLine($"bank[{index}].licenses={Native.VDM_VoiceBank_numLicenses(voiceBank).ToUInt64()}");
                Console.WriteLine($"bank[{index}].synthesizable_version={Native.VDM_VoiceBank_isSynthesizableVersion(voiceBank)}");
            }

            return result == 0 ? 0 : 2;
        }
        finally
        {
            Native.VDM_DatabaseManager_destroy(manager);
        }
    }

    private static string ReadUtf16(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;

    private static int DecodeComponentId613(IntPtr nativeLibrary, string componentId)
    {
        // Internal RVA verified against VDM 6.13.0.1.  This mode is only a
        // native cross-check for compid_codec.py; it is not a stable public API.
        IntPtr function = IntPtr.Add(nativeLibrary, 0xD9DE0);
        DecodeComponentIdDelegate decode =
            Marshal.GetDelegateForFunctionPointer<DecodeComponentIdDelegate>(function);
        IntPtr input = Marshal.StringToHGlobalAnsi(componentId);
        IntPtr output = Marshal.AllocHGlobal(32);
        try
        {
            Span<byte> zero = stackalloc byte[32];
            Marshal.Copy(zero.ToArray(), 0, output, zero.Length);
            bool valid = (decode(input, output) & 0xFF) != 0;
            Console.WriteLine($"component_id={componentId}");
            Console.WriteLine($"native.valid={valid}");
            Console.WriteLine($"native.payload={(valid ? Marshal.PtrToStringAnsi(output) : string.Empty)}");
            return valid ? 0 : 1;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(input);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong DecodeComponentIdDelegate(IntPtr componentId, IntPtr payload);

    private enum VoiceBankType
    {
        Dse = 0,
        Dnn = 1,
    }

    private static class Native
    {
        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_createDatabaseManager(
            [MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string expressionLibrary,
            ref int result);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VDM_DatabaseManager_destroy(IntPtr manager);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_DatabaseManager_numVoiceBanks(
            IntPtr manager,
            VoiceBankType type);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_DatabaseManager_voiceBankByIndex(
            IntPtr manager,
            UIntPtr index,
            VoiceBankType type);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_compID(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_drp(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VDM_VoiceBank_version(
            IntPtr voiceBank,
            ref int major,
            ref int minor,
            ref int revision);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_componentName(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_path(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_date(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_name(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_nativeLangID(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_VoiceBank_langIDSize(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_langIDByIndex(IntPtr voiceBank, UIntPtr index);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_singerID(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_npIndex(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_VoiceBank_numParameters(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_VoiceBank_numLicenses(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_defaultStyleID(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_groupName(IntPtr voiceBank);

        [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VDM_VoiceBank_isSynthesizableVersion(IntPtr voiceBank);
    }
}
