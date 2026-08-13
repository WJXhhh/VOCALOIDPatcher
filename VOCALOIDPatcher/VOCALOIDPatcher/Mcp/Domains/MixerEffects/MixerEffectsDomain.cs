using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using VOCALOIDPatcher.Mcp.Core;
using VOCALOIDPatcher.McpBridge;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.VEM;
using Yamaha.VOCALOID.Mixer;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Mcp.Domains.MixerEffects;

internal static class MixerEffectsDomain
{
    private static readonly Version MinimumVersion = new(6, 13, 0);

    public static void Register()
        => McpDomainRegistry.Register(new McpDomainAdapter(
            MixerEffectsContracts.Domain,
            MixerEffectsContracts.Operations,
            Capabilities,
            Query,
            Apply,
            verb => (verb == "insert_effect", verb is "remove_effect" or "clear_effects")));

    private static IReadOnlyList<CapabilityStatus> Capabilities()
    {
        Version? version = typeof(App).Assembly.GetName().Version;
        bool versionOk = version == null || version >= MinimumVersion;
        bool mixerModel = HasMethod(typeof(WIVSMTrack), "InsertVolume")
                          && HasMethod(typeof(WIVSMTrack), "InsertPanpot");
        bool muteSoloModel = typeof(MixerViewModel).GetMethod("SetMute", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(WIVSMTrack), typeof(bool) }, null) != null
                             && typeof(MixerViewModel).GetMethod("SetSolo", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(WIVSMTrack), typeof(bool) }, null) != null
                             && typeof(MixerViewModel).GetMethod("AudioSendMixerInputEnabled", BindingFlags.Instance | BindingFlags.NonPublic) != null;
        bool effectsModel = typeof(WIVSMTrack).GetProperty("EffectManager") != null
                            && HasMethod(typeof(WIVSMEffectManager), "InsertAudioEffect")
                            && HasMethod(typeof(WIVSMEffectManager), "RemoveAudioEffect")
                            && HasMethod(typeof(WIVSMEffectManager), "MoveAudioEffect")
                            && HasMethod(typeof(WIVSMEffect), "SetBypass")
                            && HasMethod(typeof(WIVSMEffectValue), "SetRawFloat");
        bool engine = App.EffectEngine != null;
        return new[]
        {
            Status("query.mixer_effects.mixer", versionOk && mixerModel, versionOk ? null : VersionReason()),
            Status("operation.mixer_effects.track_static.volume_pan", versionOk && mixerModel, versionOk ? null : VersionReason()),
            Status("operation.mixer_effects.track_static.mute_solo", versionOk && muteSoloModel, !versionOk ? VersionReason() : muteSoloModel ? null : "The V6 6.13 MixerViewModel mute/solo synchronization path is unavailable."),
            Status("query.mixer_effects.effect_chains", versionOk && effectsModel, versionOk ? null : VersionReason()),
            Status("query.mixer_effects.effect_catalog", versionOk && engine, !versionOk ? VersionReason() : engine ? null : "The V6 effect engine is not initialized."),
            Status("operation.mixer_effects.effect_chains", versionOk && effectsModel && engine, !versionOk ? VersionReason() : engine ? null : "The V6 effect engine is not initialized."),
            Status("operation.mixer_effects.part_effect_chains", versionOk && effectsModel && engine, !versionOk ? VersionReason() : engine ? null : "The V6 effect engine is not initialized."),
            Status("operation.mixer_effects.master_effect_chain", versionOk && effectsModel && engine, !versionOk ? VersionReason() : engine ? null : "The V6 effect engine is not initialized."),
        };
    }

    private static CapabilityStatus Status(string id, bool available, string? reason)
        => new(id, available, false, "6.13.0", reason, available ? "host_validation_required" : "unsupported");

    private static string VersionReason() => "The installed editor is older than the verified 6.13 effect model.";
    private static bool HasMethod(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public) != null;

    private static object? Query(string kind, WIVSMSequence sequence, string projectId, long revision, JsonElement arguments)
        => kind switch
        {
            "mixer" => QueryMixer(sequence, projectId, revision),
            "effect_chains" => QueryEffectChains(sequence, projectId, revision, arguments),
            "effect_catalog" => QueryCatalog(sequence),
            _ => null,
        };

    private static object QueryMixer(WIVSMSequence sequence, string projectId, long revision)
        => new
        {
            project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision),
            value_semantics = new
            {
                volume = "The tick-zero automation point, or the V6 track default when absent; later automation points are not modified.",
                pan = "The tick-zero automation point, or the V6 track default when absent; later automation points are not modified.",
                mute_solo = "Static track flags written through the V6 MixerViewModel synchronization path. They intentionally live outside edit history and are not affected by undo/redo.",
            },
            tracks = sequence.Tracks.Select((track, index) => new
            {
                track_index = index,
                name = track.Name,
                volume = StaticValue(track.Volumes, track.GetDefaultVolumeValue()),
                volume_range = new { min = track.GetMinVolumeValue(), max = track.GetMaxVolumeValue(), unit = "0.1 dB" },
                pan = StaticValue(track.Panpots, track.GetDefaultPanpotValue()),
                pan_range = new { min = track.GetMinPanpotValue(), max = track.GetMaxPanpotValue(), unit = "step" },
                mute = track.IsMute,
                solo = track.IsSolo,
                mute_solo_undoable = false,
                automation = new { volume_points = track.NumVolumes, pan_points = track.NumPanpots },
            }).ToArray(),
        };

    private static object StaticValue<T>(IEnumerable<T> points, int defaultValue) where T : WIVSMBreakPoint
    {
        T? point = points.FirstOrDefault(item => item.RelPosTick.Value == 0);
        return new { value = point?.Value ?? defaultValue, source = point == null ? "track_default" : "automation_point_at_tick_zero" };
    }

    private static object QueryEffectChains(WIVSMSequence sequence, string projectId, long revision, JsonElement arguments)
    {
        string target = ReadString(arguments, "target") ?? "all";
        var chains = new List<object>();
        if (target is "all" or "master")
            AddChain(chains, "master", -1, -1, sequence.EffectManager);
        for (int trackIndex = 0; trackIndex < sequence.Tracks.Count; trackIndex++)
        {
            WIVSMTrack track = sequence.Tracks[trackIndex];
            if (target is "all" or "track")
                AddChain(chains, "track", trackIndex, -1, track.EffectManager);
            if (target is not ("all" or "part"))
                continue;
            for (int partIndex = 0; partIndex < track.Parts.Count; partIndex++)
                AddChain(chains, "part", trackIndex, partIndex, track.Parts[partIndex].EffectManager);
        }
        return new { project = new ProjectContext(McpBridgeService.InstanceId ?? string.Empty, projectId, revision), chains };
    }

    private static void AddChain(ICollection<object> chains, string target, int trackIndex, int partIndex, WIVSMEffectManager? manager)
    {
        if (manager == null)
            return;
        var effects = new List<object>();
        for (ulong index = 0; index < manager.NumAudioEffect; index++)
        {
            WIVSMAudioEffect? effect = manager.GetAudioEffect(index);
            if (effect == null)
                continue;
            effects.Add(new
            {
                index,
                guid = effect.Id,
                type = EffectType(effect.Id),
                bypass = effect.IsBypassed,
                virtual_effect = WEffectController.IsVirtualEffect(effect.Id),
                parameters = effect.GetEffectValues.Select(Value).ToArray(),
            });
        }
        chains.Add(new { target, track_index = trackIndex >= 0 ? trackIndex : (int?)null, part_index = partIndex >= 0 ? partIndex : (int?)null, effects });
    }

    private static object Value(WIVSMEffectValue value) => new
    {
        name = value.Name,
        type = value.Type.ToString(),
        value = value.Type switch
        {
            VSMEffectValueType.Int => (object)value.RawInt,
            VSMEffectValueType.Float => value.RawFloat,
            VSMEffectValueType.String => value.RawString,
            _ => value.RawString,
        },
        normalized = value.Type == VSMEffectValueType.Float,
    };

    private static object QueryCatalog(WIVSMSequence sequence)
    {
        WEffectManager? engine = App.EffectEngine;
        HashSet<string> installed = engine?.CollectedEffectGUID().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        return new
        {
            editor_version = typeof(App).Assembly.GetName().Version?.ToString(),
            schema_semantics = "Parameter ranges/defaults/units are read from an initialized V6 controller. No GUID or parameter metadata is synthesized.",
            effects = Enum.GetValues<WEffectController.Type>().Where(type => type != WEffectController.Type.Invalid).Select(type =>
            {
                string guid = WEffectController.GetGuid(type);
                WEffectController? controller = FindLoadedController(sequence, guid);
                return new
                {
                    type = EffectType(guid),
                    guid,
                    installed = installed.Contains(guid),
                    available = installed.Contains(guid) && WEffectController.IsCorrectGuid(guid),
                    schema_availability = controller == null ? "unavailable_until_loaded" : "available",
                    unavailable_reason = controller == null ? "V6 does not expose complete parameter metadata without an initialized controller; querying the catalog must not load an effect." : null,
                    parameters = controller == null ? Array.Empty<object>() : ParameterSchema(controller),
                };
            }).ToArray(),
        };
    }

    private static object[] ParameterSchema(WEffectController controller)
    {
        var result = new List<object>();
        for (ulong index = 0; index < controller.NumParameter; index++)
        {
            if (!controller.IsValidParameter(index))
                continue;
            bool hasRange = controller.PlainParamRange(index, out int min, out int max);
            float defaultNormalized = controller.DefaultParamNormalized(index);
            result.Add(new
            {
                index,
                name = controller.ParameterName(index),
                unit = controller.ParameterLabel(index),
                normalized_min = 0.0f,
                normalized_max = 1.0f,
                normalized_default = defaultNormalized,
                plain_min = hasRange ? min : (int?)null,
                plain_max = hasRange ? max : (int?)null,
                plain_default = hasRange ? controller.NormalizedParamToPlain(index, defaultNormalized) : (int?)null,
            });
        }
        return result.ToArray();
    }

    private static WEffectController? FindLoadedController(WIVSMSequence sequence, string guid)
    {
        WEffectManager? engine = App.EffectEngine;
        if (engine == null)
            return null;
        IEnumerable<object> owners = new object[] { sequence }.Concat(sequence.Tracks.Cast<object>()).Concat(sequence.Tracks.SelectMany(track => track.Parts).Cast<object>());
        foreach (object owner in owners)
        {
            WEffectBlock? block = engine.EffectBlockOf(owner);
            WEffectController? controller = block?.EffectController(guid);
            if (controller != null && controller.IsInitialized())
                return controller;
        }
        return null;
    }

    private static string EffectType(string guid)
    {
        if (!WEffectController.IsCorrectGuid(guid))
            return "unknown";
        return WEffectController.GetType(guid).ToString().ToLowerInvariant() switch
        {
            "comp" => "compressor",
            "eq" => "equalizer",
            var value => value,
        };
    }

    private static void Apply(WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        string op = RequiredString(operation, "op");
        if (op == "set_track_static")
        {
            ApplyTrackStatic(sequence, operation, execute);
            return;
        }

        WIVSMEffectManager manager = ResolveManager(sequence, operation);
        switch (op)
        {
            case "insert_effect": InsertEffect(manager, operation, execute); break;
            case "remove_effect": RemoveEffect(manager, operation, execute); break;
            case "move_effect": MoveEffect(manager, operation, execute); break;
            case "clear_effects": ClearEffects(manager, execute); break;
            case "set_bypass": SetBypass(manager, operation, execute); break;
            case "set_parameters": SetParameters(manager, operation, execute); break;
            default: throw new ArgumentException($"Unsupported mixer/effects operation '{op}'.");
        }
    }

    private static void ApplyTrackStatic(WIVSMSequence sequence, JsonElement operation, bool execute)
    {
        WIVSMTrack track = Track(sequence, ReadInt(operation, "track_index"));
        bool any = false;
        if (TryInt(operation, "volume", out int volume))
        {
            any = true;
            ValidateRange(volume, track.GetMinVolumeValue(), track.GetMaxVolumeValue(), "volume");
            if (execute) SetTickZero(track.Volumes, volume, () => track.InsertVolume(VSMRelTick.Zero, volume));
        }
        if (TryInt(operation, "pan", out int pan))
        {
            any = true;
            ValidateRange(pan, track.GetMinPanpotValue(), track.GetMaxPanpotValue(), "pan");
            if (execute) SetTickZero(track.Panpots, pan, () => track.InsertPanpot(VSMRelTick.Zero, pan));
        }
        bool? mute = OptionalBoolean(operation, "mute");
        bool? solo = OptionalBoolean(operation, "solo");
        if (mute != null || solo != null)
        {
            any = true;
            ValidateMuteSoloPath();
            if (execute)
                SetMuteSoloOutsideHistory(sequence, track, mute, solo);
        }
        if (!any)
            throw new ArgumentException("set_track_static requires volume, pan, mute, or solo.");
    }

    private static void ValidateMuteSoloPath()
    {
        if ((System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel)?.MixerVM == null
            || typeof(MixerViewModel).GetMethod("SetMute", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(WIVSMTrack), typeof(bool) }, null) == null
            || typeof(MixerViewModel).GetMethod("SetSolo", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(WIVSMTrack), typeof(bool) }, null) == null
            || typeof(MixerViewModel).GetMethod("AudioSendMixerInputEnabled", BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new NotSupportedException("The V6 mixer mute/solo synchronization path is unavailable.");
    }

    private static void SetMuteSoloOutsideHistory(WIVSMSequence sequence, WIVSMTrack track, bool? mute, bool? solo)
    {
        MixerViewModel mixer = (System.Windows.Application.Current?.MainWindow?.DataContext as MainViewModel)?.MixerVM
                               ?? throw new NotSupportedException("The V6 mixer view model is unavailable.");
        if (mute is { } muteValue && muteValue != track.IsMute)
            InvokeMixerSetter(mixer, "SetMute", track, muteValue);
        if (solo is { } soloValue && soloValue != track.IsSolo)
            InvokeMixerSetter(mixer, "SetSolo", track, soloValue);
        sequence.IsModifiedOutsideOfEditHistory = true;
        MethodInfo sync = typeof(MixerViewModel).GetMethod("AudioSendMixerInputEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new NotSupportedException("The V6 mixer audio synchronization path is unavailable.");
        sync.Invoke(mixer, null);
    }

    private static void InvokeMixerSetter(MixerViewModel mixer, string name, WIVSMTrack track, bool value)
    {
        MethodInfo setter = typeof(MixerViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(WIVSMTrack), typeof(bool) }, null)
                            ?? throw new NotSupportedException($"The V6 mixer {name} synchronization path is unavailable.");
        setter.Invoke(mixer, new object[] { track, value });
    }

    private static void SetTickZero<T>(IEnumerable<T> points, int value, Func<T?> insert) where T : WIVSMBreakPoint
    {
        T? point = points.FirstOrDefault(item => item.RelPosTick.Value == 0);
        if (point != null)
            point.Value = value;
        else if (insert() == null)
            throw new InvalidOperationException("VOCALOID rejected the tick-zero mixer value.");
    }

    private static void InsertEffect(WIVSMEffectManager manager, JsonElement operation, bool execute)
    {
        string guid = RequiredString(operation, "effect_guid");
        ValidateGuid(guid);
        if (manager.GetAudioEffect(guid) != null)
            throw new ArgumentException($"Effect '{guid}' already exists in this chain.");
        int index = operation.TryGetProperty("index", out _) ? ReadInt(operation, "index") : checked((int)manager.NumAudioEffect);
        if (index < 0 || (ulong)index > manager.NumAudioEffect)
            throw new ArgumentOutOfRangeException("index");
        if (!execute)
            return;
        WEffectBlock block = ResolveBlock(manager);
        WIVSMAudioEffect effect = manager.InsertAudioEffect((ulong)index, guid) ?? throw new InvalidOperationException("VOCALOID rejected effect insertion.");
        WEffectController controller = block.InsertLoadEffect(guid, (ulong)index) ?? throw new InvalidOperationException("The V6 effect engine could not initialize the effect.");
        if (!controller.IsInitialized())
            throw new InvalidOperationException("The V6 effect controller did not initialize.");
        bool bypass = block.GetBypass(guid);
        if (effect.IsBypassed != bypass && !effect.SetBypass(bypass))
            throw new InvalidOperationException("VOCALOID rejected the initial bypass state.");
        foreach (string name in block.GetEditableParameterNames(guid))
        {
            float value = block.GetParameterValue(guid, name);
            if (value == float.MinValue || effect.AddValueFloat(name, value) == null)
                throw new InvalidOperationException($"VOCALOID rejected initial parameter '{name}'.");
        }
    }

    private static void RemoveEffect(WIVSMEffectManager manager, JsonElement operation, bool execute)
    {
        WIVSMAudioEffect effect = Effect(manager, ReadInt(operation, "effect_index"));
        if (WEffectController.IsVirtualEffect(effect.Id))
            throw new NotSupportedException("Virtual audio-part effects cannot be removed through the native audio-effect chain contract.");
        if (execute && !manager.RemoveAudioEffect(effect))
            throw new InvalidOperationException("VOCALOID rejected effect removal.");
    }

    private static void MoveEffect(WIVSMEffectManager manager, JsonElement operation, bool execute)
    {
        WIVSMAudioEffect effect = Effect(manager, ReadInt(operation, "effect_index"));
        int to = ReadInt(operation, "to_index");
        if (to < 0 || (ulong)to >= manager.NumAudioEffect)
            throw new ArgumentOutOfRangeException("to_index");
        if (WEffectController.IsVirtualEffect(effect.Id))
            throw new NotSupportedException("Virtual audio-part effects cannot be moved through this contract.");
        if (execute && !manager.MoveAudioEffect((ulong)to, effect))
            throw new InvalidOperationException("VOCALOID rejected effect movement.");
    }

    private static void ClearEffects(WIVSMEffectManager manager, bool execute)
    {
        WIVSMAudioEffect[] effects = manager.AudioEffects.Where(item => !WEffectController.IsVirtualEffect(item.Id)).ToArray();
        if (!execute)
            return;
        foreach (WIVSMAudioEffect effect in effects)
            if (!manager.RemoveAudioEffect(effect))
                throw new InvalidOperationException($"VOCALOID rejected removal of effect '{effect.Id}'.");
    }

    private static void SetBypass(WIVSMEffectManager manager, JsonElement operation, bool execute)
    {
        WIVSMAudioEffect effect = Effect(manager, ReadInt(operation, "effect_index"));
        bool bypass = operation.TryGetProperty("bypass", out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new ArgumentException("bypass must be boolean.");
        if (execute && !effect.SetBypass(bypass))
            throw new InvalidOperationException("VOCALOID rejected effect bypass.");
    }

    private static void SetParameters(WIVSMEffectManager manager, JsonElement operation, bool execute)
    {
        WIVSMAudioEffect effect = Effect(manager, ReadInt(operation, "effect_index"));
        if (!operation.TryGetProperty("parameters", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("parameters must be an object of normalized numeric values.");
        var changes = new List<(WIVSMEffectValue Value, float Normalized)>();
        foreach (JsonProperty property in parameters.EnumerateObject())
        {
            if (!property.Value.TryGetSingle(out float normalized) || !float.IsFinite(normalized) || normalized is < 0 or > 1)
                throw new ArgumentOutOfRangeException(property.Name, "Effect parameters use normalized values in [0, 1].");
            WIVSMEffectValue value = effect.GetValueByName(property.Name) ?? throw new ArgumentException($"Effect parameter '{property.Name}' is not available.");
            if (value.Type != VSMEffectValueType.Float)
                throw new NotSupportedException($"Effect parameter '{property.Name}' is not a normalized float parameter.");
            changes.Add((value, normalized));
        }
        if (changes.Count == 0)
            throw new ArgumentException("parameters must not be empty.");
        if (!execute)
            return;
        foreach ((WIVSMEffectValue value, float normalized) in changes)
            if (!value.SetRawFloat(normalized))
                throw new InvalidOperationException($"VOCALOID rejected effect parameter '{value.Name}'.");
    }

    private static WIVSMEffectManager ResolveManager(WIVSMSequence sequence, JsonElement operation)
    {
        string target = RequiredString(operation, "target").ToLowerInvariant();
        WIVSMEffectManager? manager = target switch
        {
            "master" => sequence.EffectManager,
            "track" => Track(sequence, ReadInt(operation, "track_index")).EffectManager,
            "part" => Part(sequence, ReadInt(operation, "track_index"), ReadInt(operation, "part_index")).EffectManager,
            _ => throw new ArgumentException("target must be master, track, or part."),
        };
        return manager ?? throw new NotSupportedException($"The {target} target has no native V6 effect manager.");
    }

    private static WEffectBlock ResolveBlock(WIVSMEffectManager manager)
        => App.EffectEngine?.EffectBlockOf(manager.Parent)
           ?? throw new NotSupportedException("The V6 effect engine has no initialized block for this target.");

    private static void ValidateGuid(string guid)
    {
        if (!Guid.TryParse(guid, out _) || !WEffectController.IsCorrectGuid(guid))
            throw new NotSupportedException($"Effect GUID '{guid}' is not in the installed V6 catalog.");
        WEffectManager engine = App.EffectEngine ?? throw new NotSupportedException("The V6 effect engine is not initialized.");
        if (!engine.CollectedEffectGUID().Contains(guid, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Effect GUID '{guid}' is not installed or does not match this V6 version.");
    }

    private static WIVSMAudioEffect Effect(WIVSMEffectManager manager, int index)
    {
        if (index < 0 || (ulong)index >= manager.NumAudioEffect)
            throw new ArgumentOutOfRangeException("effect_index");
        return manager.GetAudioEffect((ulong)index) ?? throw new ArgumentException("The effect no longer exists.");
    }

    private static WIVSMTrack Track(WIVSMSequence sequence, int index)
        => index >= 0 && index < sequence.Tracks.Count ? sequence.Tracks[index] : throw new ArgumentOutOfRangeException("track_index");

    private static WIVSMPart Part(WIVSMSequence sequence, int track, int part)
    {
        WIVSMTrack owner = Track(sequence, track);
        return part >= 0 && part < owner.Parts.Count ? owner.Parts[part] : throw new ArgumentOutOfRangeException("part_index");
    }

    private static void ValidateRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(name, $"{name} must be in [{min}, {max}].");
    }

    private static string RequiredString(JsonElement element, string name)
        => ReadString(element, name) is { Length: > 0 } value ? value : throw new ArgumentException($"{name} is required.");

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int ReadInt(JsonElement element, string name)
        => TryInt(element, name, out int value) ? value : throw new ArgumentException($"{name} must be an integer.");

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement item) && item.TryGetInt32(out value);
    }

    private static bool? OptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"{name} must be boolean.");
        return value.GetBoolean();
    }
}
