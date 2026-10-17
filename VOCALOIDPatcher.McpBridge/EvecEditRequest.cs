using System.Text.Json;

namespace VOCALOIDPatcher.McpBridge;

/// <summary>Partial EVEC update. Missing fields preserve the current logical value.</summary>
public sealed record EvecEditRequest(int? VoiceColorId, int? AttackId, int? ReleaseId, int? ConsonantExtension, bool Clear)
{
    public static EvecEditRequest Parse(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("evec must be an object.");
        int? color = null, attack = null, release = null, extension = null;
        bool clear = false;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in source.EnumerateObject())
        {
            if (!fields.Add(property.Name))
                throw new ArgumentException($"Duplicate evec field '{property.Name}'.");
            if (property.Name == "clear")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("evec.clear must be boolean.");
                clear = property.Value.GetBoolean();
                continue;
            }
            if (property.Name is not ("voice_color_id" or "attack_id" or "release_id" or "consonant_extension"))
                throw new ArgumentException($"Unknown evec field '{property.Name}'.");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int value))
                throw new ArgumentException($"evec.{property.Name} must be an integer.");
            switch (property.Name)
            {
                case "voice_color_id": color = value; break;
                case "attack_id": attack = value; break;
                case "release_id": release = value; break;
                case "consonant_extension": extension = value; break;
            }
        }
        if (clear && fields.Count != 1)
            throw new ArgumentException("evec.clear cannot be combined with option fields.");
        if (!clear && color == null && attack == null && release == null && extension == null)
            throw new ArgumentException("evec requires at least one option or clear=true.");
        if (extension is < 0 or > 3)
            throw new ArgumentException("evec.consonant_extension must be 0–3 (none, x2, x3, x4).");
        return new(color, attack, release, extension, clear);
    }
}
