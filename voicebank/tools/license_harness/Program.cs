using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string VdmImportName = "vdm";
    private const string DseImportName = "dse";

    private static int Main()
    {
        bool includeEntries = string.Equals(
            Environment.GetEnvironmentVariable("LICENSE_HARNESS_INCLUDE_ENTRIES"),
            "1",
            StringComparison.Ordinal);
        string editorDirectory = Environment.GetEnvironmentVariable("LICENSE_HARNESS_EDITOR")
            ?? @"C:\Program Files\VOCALOID6\Editor";
        string vdmPath = Environment.GetEnvironmentVariable("LICENSE_HARNESS_VDM")
            ?? Path.Combine(editorDirectory, "VDM.dll");
        string dsePath = Environment.GetEnvironmentVariable("LICENSE_HARNESS_DSE")
            ?? Path.Combine(editorDirectory, "DSE.dll");
        string expressionLibrary = Environment.GetEnvironmentVariable("LICENSE_HARNESS_EXPLIB")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                "VOCALOID6",
                "Explib");

        if (!File.Exists(vdmPath) || !File.Exists(dsePath))
        {
            Console.Error.WriteLine("VDM.dll or DSE.dll does not exist.");
            return 1;
        }

        IntPtr vdmLibrary = NativeLibrary.Load(vdmPath);
        IntPtr dseLibrary = NativeLibrary.Load(dsePath);
        NativeLibrary.SetDllImportResolver(
            Assembly.GetExecutingAssembly(),
            (libraryName, _, _) => libraryName switch
            {
                VdmImportName => vdmLibrary,
                DseImportName => dseLibrary,
                _ => IntPtr.Zero,
            });

        int vdmResult = -1;
        IntPtr databaseManager = Native.VDM_createDatabaseManager(
            "VOCALOID6",
            expressionLibrary,
            ref vdmResult);
        Console.WriteLine($"vdm.create.result={vdmResult}");
        Console.WriteLine($"vdm.create.manager={(databaseManager == IntPtr.Zero ? "null" : "non-null")}");
        if (databaseManager == IntPtr.Zero)
        {
            return 2;
        }

        IntPtr dseManager = IntPtr.Zero;
        bool initialized = false;
        try
        {
            Dictionary<string, VoiceBankSummary> registeredDse = ReadRegisteredVoiceBanks(
                databaseManager,
                VoiceBankType.Dse);
            Dictionary<string, VoiceBankSummary> registeredDnn = ReadRegisteredVoiceBanks(
                databaseManager,
                VoiceBankType.Dnn);
            Console.WriteLine($"vdm.dse_voice_banks={registeredDse.Count}");
            Console.WriteLine(
                $"vdm.license_descriptors={registeredDse.Values.Sum(value => value.Count)}");
            Console.WriteLine(
                $"vdm.nonempty_license_keys={registeredDse.Values.Sum(value => value.NonEmptyKeys)}");
            Console.WriteLine(
                $"vdm.nonempty_serial_numbers={registeredDse.Values.Sum(value => value.NonEmptySerials)}");
            foreach (IGrouping<int, VoiceBankSummary> group in registeredDse.Values
                         .GroupBy(value => value.Count)
                         .OrderBy(group => group.Key))
            {
                Console.WriteLine($"vdm.banks_with_{group.Key}_descriptors={group.Count()}");
            }
            Console.WriteLine($"vdm.dnn_voice_banks={registeredDnn.Count}");
            Console.WriteLine(
                $"vdm.dnn_license_descriptors={registeredDnn.Values.Sum(value => value.Count)}");
            Console.WriteLine(
                $"vdm.dnn_nonempty_license_keys={registeredDnn.Values.Sum(value => value.NonEmptyKeys)}");
            Console.WriteLine(
                $"vdm.dnn_nonempty_serial_numbers={registeredDnn.Values.Sum(value => value.NonEmptySerials)}");
            foreach (IGrouping<int, VoiceBankSummary> group in registeredDnn.Values
                         .GroupBy(value => value.Count)
                         .OrderBy(group => group.Key))
            {
                Console.WriteLine(
                    $"vdm.dnn_banks_with_{group.Key}_descriptors={group.Count()}");
            }
            Console.WriteLine($"details.include_entries={includeEntries}");

            dseManager = Native.VIS_DSE_CreateManager();
            if (dseManager == IntPtr.Zero)
            {
                Console.Error.WriteLine("DSE manager creation failed.");
                return 3;
            }

            DseResult initializeResult = Native.VIS_DSE_InitializeManager(
                dseManager,
                databaseManager);
            Console.WriteLine($"dse.initialize.result={initializeResult} ({(int)initializeResult})");
            if (initializeResult != DseResult.Successful)
            {
                return 4;
            }
            initialized = true;

            ulong count = Native.VIS_DSE_NumLicenses(dseManager).ToUInt64();
            Console.WriteLine($"licenses={count}");
            Dictionary<LicenseType, int> typeCounts = new();
            Dictionary<LicenseResult, int> resultCounts = new();
            Dictionary<LicenseResult, int> registeredResultCounts = new();
            Dictionary<LicenseResult, int> registeredDnnResultCounts = new();
            int voiceLicenses = 0;
            int matchingRegisteredDse = 0;
            int matchingRegisteredDnn = 0;
            int unmatchedVoiceLicenses = 0;
            int matchingIdentityNames = 0;
            int matchingIdentityVersions = 0;
            int identityNameMismatches = 0;
            int identityVersionMismatches = 0;

            for (ulong index = 0; index < count; index++)
            {
                IntPtr license = Native.VIS_DSE_GetLicense(dseManager, new UIntPtr(index));
                if (license == IntPtr.Zero)
                {
                    Console.WriteLine($"license[{index}].handle=null");
                    continue;
                }

                LicenseType type = Native.VIS_DSE_GetCompTypeFromLicense(license);
                LicenseResult result = Native.VIS_DSE_GetResultFromLicense(license);
                string componentId = ReadUtf16(Native.VIS_DSE_GetCompIDFromLicense(license));
                string componentName =
                    ReadUtf16(Native.VIS_DSE_GetCompNameFromLicense(license));
                int major = 0;
                int minor = 0;
                int revision = 0;
                bool hasVersion = Native.VIS_DSE_GetCompVersionFromLicense(
                    license,
                    ref major,
                    ref minor,
                    ref revision);
                VoiceBankSummary descriptorSummary = default;
                bool registeredDseMatch = type == LicenseType.Voice
                    && registeredDse.TryGetValue(componentId, out descriptorSummary);
                VoiceBankSummary dnnDescriptorSummary = default;
                bool registeredDnnMatch = type == LicenseType.Voice
                    && registeredDnn.TryGetValue(componentId, out dnnDescriptorSummary);
                VoiceBankSummary registeredSummary = registeredDseMatch
                    ? descriptorSummary
                    : dnnDescriptorSummary;
                bool registeredVoiceMatch = registeredDseMatch || registeredDnnMatch;
                bool identityNameMatches = registeredVoiceMatch
                    && string.Equals(
                        componentName,
                        registeredSummary.Name,
                        StringComparison.Ordinal);
                bool identityVersionMatches = registeredVoiceMatch
                    && hasVersion
                    && registeredSummary.HasVersion
                    && major == registeredSummary.Major
                    && minor == registeredSummary.Minor
                    && revision == registeredSummary.Revision;

                typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
                resultCounts[result] = resultCounts.GetValueOrDefault(result) + 1;
                if (type == LicenseType.Voice)
                {
                    voiceLicenses++;
                }
                if (registeredDseMatch)
                {
                    matchingRegisteredDse++;
                    registeredResultCounts[result] =
                        registeredResultCounts.GetValueOrDefault(result) + 1;
                }
                if (registeredDnnMatch)
                {
                    matchingRegisteredDnn++;
                    registeredDnnResultCounts[result] =
                        registeredDnnResultCounts.GetValueOrDefault(result) + 1;
                }
                if (type == LicenseType.Voice
                    && !registeredDseMatch
                    && !registeredDnnMatch)
                {
                    unmatchedVoiceLicenses++;
                }
                if (registeredVoiceMatch)
                {
                    if (identityNameMatches)
                    {
                        matchingIdentityNames++;
                    }
                    else
                    {
                        identityNameMismatches++;
                    }
                    if (identityVersionMatches)
                    {
                        matchingIdentityVersions++;
                    }
                    else
                    {
                        identityVersionMismatches++;
                    }
                }

                if (includeEntries)
                {
                    LicenseResult spliceResult =
                        Native.VIS_DSE_GetSpliceResultFromLicense(license);
                    long expiryUnix = Native.VIS_DSE_GetExpiryDateFromLicense(license);
                    long remainingTrialDays =
                        Native.VIS_DSE_GetRemainingTrialDaysFromLicense(license);

                    Console.WriteLine($"license[{index}].type={type} ({(int)type})");
                    Console.WriteLine($"license[{index}].comp_id={componentId}");
                    Console.WriteLine($"license[{index}].comp_name={componentName}");
                    Console.WriteLine(
                        $"license[{index}].version={(hasVersion ? $"{major}.{minor}.{revision}" : "invalid")}");
                    Console.WriteLine($"license[{index}].result={result} ({(int)result})");
                    Console.WriteLine(
                        $"license[{index}].splice_result={spliceResult} ({(int)spliceResult})");
                    Console.WriteLine($"license[{index}].expiry_unix={expiryUnix}");
                    Console.WriteLine(
                        $"license[{index}].remaining_trial_days={remainingTrialDays}");
                    Console.WriteLine(
                        $"license[{index}].matches_registered_dse={registeredDseMatch}");
                    Console.WriteLine(
                        $"license[{index}].matches_registered_dnn={registeredDnnMatch}");
                    Console.WriteLine(
                        $"license[{index}].identity_name_matches_vdm={identityNameMatches}");
                    Console.WriteLine(
                        $"license[{index}].identity_version_matches_vdm={identityVersionMatches}");
                    if (registeredDseMatch)
                    {
                        Console.WriteLine(
                            $"license[{index}].vdm_descriptors={descriptorSummary.Count}");
                        Console.WriteLine(
                            $"license[{index}].vdm_nonempty_keys={descriptorSummary.NonEmptyKeys}");
                        Console.WriteLine(
                            $"license[{index}].vdm_nonempty_serials={descriptorSummary.NonEmptySerials}");
                        WriteVoiceBankIdentity(index, descriptorSummary, "vdm");
                    }
                    if (registeredDnnMatch)
                    {
                        Console.WriteLine(
                            $"license[{index}].vdm_dnn_descriptors={dnnDescriptorSummary.Count}");
                        Console.WriteLine(
                            $"license[{index}].vdm_dnn_nonempty_keys={dnnDescriptorSummary.NonEmptyKeys}");
                        Console.WriteLine(
                            $"license[{index}].vdm_dnn_nonempty_serials={dnnDescriptorSummary.NonEmptySerials}");
                        WriteVoiceBankIdentity(index, dnnDescriptorSummary, "vdm_dnn");
                    }
                }
            }

            Console.WriteLine($"summary.voice_licenses={voiceLicenses}");
            Console.WriteLine($"summary.registered_dse_matches={matchingRegisteredDse}");
            Console.WriteLine($"summary.registered_dnn_matches={matchingRegisteredDnn}");
            Console.WriteLine($"summary.unmatched_voice_licenses={unmatchedVoiceLicenses}");
            Console.WriteLine($"summary.identity_name_matches={matchingIdentityNames}");
            Console.WriteLine($"summary.identity_name_mismatches={identityNameMismatches}");
            Console.WriteLine($"summary.identity_version_matches={matchingIdentityVersions}");
            Console.WriteLine($"summary.identity_version_mismatches={identityVersionMismatches}");
            foreach ((LicenseType type, int value) in typeCounts.OrderBy(item => item.Key))
            {
                Console.WriteLine($"summary.type.{type}={value}");
            }
            foreach ((LicenseResult result, int value) in resultCounts.OrderBy(item => item.Key))
            {
                Console.WriteLine($"summary.result.{result}={value}");
            }
            foreach ((LicenseResult result, int value) in registeredResultCounts
                         .OrderBy(item => item.Key))
            {
                Console.WriteLine($"summary.registered_result.{result}={value}");
            }
            foreach ((LicenseResult result, int value) in registeredDnnResultCounts
                         .OrderBy(item => item.Key))
            {
                Console.WriteLine($"summary.registered_dnn_result.{result}={value}");
            }
            return 0;
        }
        finally
        {
            if (dseManager != IntPtr.Zero)
            {
                if (initialized)
                {
                    DseResult terminateResult = Native.VIS_DSE_TerminateManager(dseManager);
                    Console.WriteLine($"dse.terminate.result={terminateResult} ({(int)terminateResult})");
                }
                Native.VIS_DSE_DestroyManager(dseManager);
            }
            Native.VDM_DatabaseManager_destroy(databaseManager);
        }
    }

    private static Dictionary<string, VoiceBankSummary> ReadRegisteredVoiceBanks(
        IntPtr databaseManager,
        VoiceBankType type)
    {
        ulong count = Native.VDM_DatabaseManager_numVoiceBanks(
            databaseManager,
            type).ToUInt64();
        Dictionary<string, VoiceBankSummary> result = new(StringComparer.Ordinal);
        for (ulong index = 0; index < count; index++)
        {
            IntPtr voiceBank = Native.VDM_DatabaseManager_voiceBankByIndex(
                databaseManager,
                new UIntPtr(index),
                type);
            if (voiceBank != IntPtr.Zero)
            {
                string componentId = ReadUtf16(Native.VDM_VoiceBank_compID(voiceBank));
                if (!string.IsNullOrEmpty(componentId))
                {
                    int descriptors = checked((int)Native.VDM_VoiceBank_numLicenses(
                        voiceBank).ToUInt64());
                    int nonEmptyKeys = 0;
                    int nonEmptySerials = 0;
                    for (int descriptorIndex = 0; descriptorIndex < descriptors; descriptorIndex++)
                    {
                        IntPtr descriptor = Native.VDM_VoiceBank_license(
                            voiceBank,
                            new UIntPtr((uint)descriptorIndex));
                        if (descriptor == IntPtr.Zero)
                        {
                            continue;
                        }
                        if (!string.IsNullOrEmpty(ReadAnsi(Native.VDM_License_key(descriptor))))
                        {
                            nonEmptyKeys++;
                        }
                        if (!string.IsNullOrEmpty(ReadAnsi(
                            Native.VDM_License_serialNumber(descriptor))))
                        {
                            nonEmptySerials++;
                        }
                    }
                    int major = -1;
                    int minor = -1;
                    int revision = -1;
                    bool hasVersion = Native.VDM_VoiceBank_version(
                        voiceBank,
                        ref major,
                        ref minor,
                        ref revision);
                    ulong languageCount = Native.VDM_VoiceBank_langIDSize(
                        voiceBank).ToUInt64();
                    int[] languages = new int[checked((int)languageCount)];
                    for (ulong languageIndex = 0; languageIndex < languageCount; languageIndex++)
                    {
                        languages[languageIndex] = Native.VDM_VoiceBank_langIDByIndex(
                            voiceBank,
                            new UIntPtr(languageIndex));
                    }
                    result[componentId] = new VoiceBankSummary(
                        descriptors,
                        nonEmptyKeys,
                        nonEmptySerials,
                        ReadUtf16(Native.VDM_VoiceBank_componentName(voiceBank)),
                        ReadUtf16(Native.VDM_VoiceBank_name(voiceBank)),
                        hasVersion,
                        major,
                        minor,
                        revision,
                        Native.VDM_VoiceBank_nativeLangID(voiceBank),
                        languages,
                        Native.VDM_VoiceBank_isSynthesizableVersion(voiceBank));
                }
            }
        }
        return result;
    }

    private static string ReadUtf16(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;

    private static string ReadAnsi(IntPtr value) =>
        value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(value) ?? string.Empty;

    private static void WriteVoiceBankIdentity(
        ulong index,
        VoiceBankSummary summary,
        string prefix)
    {
        Console.WriteLine($"license[{index}].{prefix}_component_name={summary.ComponentName}");
        Console.WriteLine($"license[{index}].{prefix}_name={summary.Name}");
        Console.WriteLine(
            $"license[{index}].{prefix}_version="
            + (summary.HasVersion
                ? $"{summary.Major}.{summary.Minor}.{summary.Revision}"
                : "invalid"));
        Console.WriteLine($"license[{index}].{prefix}_native_language={summary.NativeLanguage}");
        Console.WriteLine(
            $"license[{index}].{prefix}_languages={string.Join(',', summary.Languages)}");
        Console.WriteLine(
            $"license[{index}].{prefix}_synthesizable_version={summary.SynthesizableVersion}");
    }

    private readonly record struct VoiceBankSummary(
        int Count,
        int NonEmptyKeys,
        int NonEmptySerials,
        string ComponentName,
        string Name,
        bool HasVersion,
        int Major,
        int Minor,
        int Revision,
        int NativeLanguage,
        int[] Languages,
        bool SynthesizableVersion);

    private enum VoiceBankType
    {
        Dse = 0,
        Dnn = 1,
    }

    private enum DseResult
    {
        Successful = 0,
    }

    private enum LicenseType
    {
        Undefined = 0,
        Application = 1,
        Voice = 2,
    }

    private enum LicenseResult
    {
        Undefined = 0,
        MissingLeaseFile = 1,
        Trial = 2,
        Expired = 3,
        InvalidTrialKey = 4,
        ExpiredLeaseFile = 5,
        InvalidLeaseFile = 6,
        ExpiredKey = 7,
        ValidLeaseFile = 8,
        PaidOffLeaseFile = 9,
        InvalidKey = 10,
        InvalidSerialNumber = 11,
        InvalidComponent = 12,
        InvalidHash = 13,
        ValidExpiryKey = 14,
        NoError = 15,
    }

    private static class Native
    {
        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_createDatabaseManager(
            [MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string expressionLibrary,
            ref int result);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VDM_DatabaseManager_destroy(IntPtr manager);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_DatabaseManager_numVoiceBanks(
            IntPtr manager,
            VoiceBankType type);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_DatabaseManager_voiceBankByIndex(
            IntPtr manager,
            UIntPtr index,
            VoiceBankType type);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_compID(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_componentName(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_name(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VDM_VoiceBank_version(
            IntPtr voiceBank,
            ref int major,
            ref int minor,
            ref int revision);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_nativeLangID(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_VoiceBank_langIDSize(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int VDM_VoiceBank_langIDByIndex(IntPtr voiceBank, UIntPtr index);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VDM_VoiceBank_isSynthesizableVersion(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VDM_VoiceBank_numLicenses(IntPtr voiceBank);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_VoiceBank_license(IntPtr voiceBank, UIntPtr index);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_License_key(IntPtr license);

        [DllImport(VdmImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VDM_License_serialNumber(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_DSE_CreateManager();

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void VIS_DSE_DestroyManager(IntPtr manager);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern DseResult VIS_DSE_InitializeManager(
            IntPtr manager,
            IntPtr databaseManager);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern DseResult VIS_DSE_TerminateManager(IntPtr manager);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern UIntPtr VIS_DSE_NumLicenses(IntPtr manager);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_DSE_GetLicense(IntPtr manager, UIntPtr index);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_DSE_GetCompIDFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr VIS_DSE_GetCompNameFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern LicenseType VIS_DSE_GetCompTypeFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool VIS_DSE_GetCompVersionFromLicense(
            IntPtr license,
            ref int major,
            ref int minor,
            ref int revision);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern LicenseResult VIS_DSE_GetResultFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern LicenseResult VIS_DSE_GetSpliceResultFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long VIS_DSE_GetExpiryDateFromLicense(IntPtr license);

        [DllImport(DseImportName, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long VIS_DSE_GetRemainingTrialDaysFromLicense(IntPtr license);
    }
}
