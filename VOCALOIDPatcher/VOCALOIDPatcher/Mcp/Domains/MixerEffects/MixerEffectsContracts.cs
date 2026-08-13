using System.Collections.Generic;
using VOCALOIDPatcher.McpBridge;

namespace VOCALOIDPatcher.Mcp.Domains.MixerEffects;

internal static class MixerEffectsContracts
{
    public const string DomainId = "mixer_effects";

    public static IReadOnlyList<OperationContract> Operations { get; } = new[]
    {
        new OperationContract("mixer_effects.set_track_static", DomainId, "v6_apply_operations", new[] { "op", "track_index" }, new[] { "volume", "pan", "mute", "solo", "client_tag" }),
        new OperationContract("mixer_effects.insert_effect", DomainId, "v6_apply_operations", new[] { "op", "target", "effect_guid" }, new[] { "track_index", "part_index", "index", "client_tag" }),
        new OperationContract("mixer_effects.remove_effect", DomainId, "v6_apply_operations", new[] { "op", "target", "effect_index" }, new[] { "track_index", "part_index", "client_tag" }, true),
        new OperationContract("mixer_effects.move_effect", DomainId, "v6_apply_operations", new[] { "op", "target", "effect_index", "to_index" }, new[] { "track_index", "part_index", "client_tag" }),
        new OperationContract("mixer_effects.clear_effects", DomainId, "v6_apply_operations", new[] { "op", "target" }, new[] { "track_index", "part_index", "client_tag" }, true),
        new OperationContract("mixer_effects.set_bypass", DomainId, "v6_apply_operations", new[] { "op", "target", "effect_index", "bypass" }, new[] { "track_index", "part_index", "client_tag" }),
        new OperationContract("mixer_effects.set_parameters", DomainId, "v6_apply_operations", new[] { "op", "target", "effect_index", "parameters" }, new[] { "track_index", "part_index", "client_tag" }),
    };

    public static DomainContract Domain { get; } = new(
        DomainId,
        new[] { "mixer", "effect_chains", "effect_catalog" },
        System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Operations, item => item.Id)),
        new[] { "track_mixer", "effect_chain", "audio_effect", "effect_parameter" },
        "operation.mixer_effects");
}
