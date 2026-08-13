using System.Text.Json;
using VOCALOIDPatcher.Mcp.Domains.ExtensionParameters;

namespace VOCALOIDPatcher.McpTests;

public sealed class ExtensionParameterContractTests
{
    [Fact]
    public void RegistrySeparatesPatcherParametersFromNativeControllers()
    {
        Assert.All(ExtensionParameterContracts.Parameters, parameter => Assert.Equal("patcher", parameter.Source));
        Assert.Equal(new[] { "patcher.bvl", "patcher.register_shift" },
            ExtensionParameterContracts.Parameters.Select(parameter => parameter.Id));
        Assert.All(ExtensionParameterContracts.Parameters, parameter => Assert.StartsWith("operation.extension_parameters.", parameter.CapabilityId));
    }

    [Theory]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"patcher.bvl\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":0}")]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"patcher.bvl\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":127}")]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"patcher.register_shift\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":-12}")]
    [InlineData("{\"op\":\"clear\",\"parameter_id\":\"patcher.register_shift\",\"track_index\":0,\"part_index\":0,\"note_index\":0}")]
    public void ValidOperationsShareOnePureValidator(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Empty(ExtensionParameterContracts.Validate(document.RootElement));
    }

    [Theory]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"BVL\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":64}", "parameter_id")]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"patcher.bvl\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":128}", "between 0 and 127")]
    [InlineData("{\"op\":\"set\",\"parameter_id\":\"patcher.register_shift\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"value\":-13}", "between -12 and 12")]
    [InlineData("{\"op\":\"clear\",\"parameter_id\":\"patcher.bvl\",\"track_index\":-1,\"part_index\":0,\"note_index\":0}", "track_index")]
    public void InvalidOperationsReturnFieldSpecificErrors(string json, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Contains(expected, ExtensionParameterContracts.Validate(document.RootElement));
    }
}
