using System.Diagnostics;
using System.Text.Json;
using VOCALOIDPatcher.McpBridge;
using VOCALOIDPatcher.Mcp.Core;

namespace VOCALOIDPatcher.McpTests;

public sealed class StageZeroToTwoContractTests
{
    [Fact]
    public void EntityReferenceKeepsLegacyIndicesAndAddsStableIdentity()
    {
        var reference = new EntityRef("project", 7, "note", 1, 2, 3, "stable-note", "client-note");
        JsonElement json = JsonSerializer.SerializeToElement(reference, BridgeProtocol.JsonOptions);

        Assert.Equal(1, json.GetProperty("track_index").GetInt32());
        Assert.Equal("stable-note", json.GetProperty("entity_id").GetString());
        Assert.Equal("client-note", json.GetProperty("client_tag").GetString());
    }

    [Fact]
    public void BaselineContractsCoverFourMutationDomainsWithUniqueIds()
    {
        Assert.Equal(McpContractCatalog.Operations.Count, McpContractCatalog.Operations.Select(item => item.Id).Distinct().Count());
        Assert.Contains(McpContractCatalog.Operations, item => item.Domain == "structure");
        Assert.Contains(McpContractCatalog.Operations, item => item.Domain == "notes");
        Assert.Contains(McpContractCatalog.Operations, item => item.Domain == "parameters");
        Assert.Contains(McpContractCatalog.Operations, item => item.Domain == "g2pa");
        Assert.True(McpContractCatalog.Domains.Count >= 2);
        Assert.All(McpContractCatalog.Domains, domain => Assert.NotEmpty(domain.CapabilityPrefix));
    }

    [Theory]
    [InlineData("structure.add_track", "{\"op\":\"add_track\"}")]
    [InlineData("notes.add", "{\"op\":\"add\",\"track_index\":0,\"part_index\":0,\"duration_tick\":480,\"note_number\":60}")]
    [InlineData("parameters.add_controller", "{\"op\":\"add_controller\",\"track_index\":0,\"part_index\":0,\"parameter_type\":\"Dynamics\",\"value\":64}")]
    [InlineData("g2pa.set_lyrics", "{\"action\":\"set_lyrics\",\"track_index\":0,\"part_index\":0,\"note_index\":0,\"lyrics\":\"a\"}")]
    public void MinimalFacadeFixturesSatisfyPublishedContracts(string operationId, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Empty(OperationContractValidator.Validate(operationId, document.RootElement));
    }

    [Fact]
    public void ContractValidatorReportsMissingFields()
    {
        using JsonDocument document = JsonDocument.Parse("{\"op\":\"add\"}");
        IReadOnlyList<string> missing = OperationContractValidator.Validate("notes.add", document.RootElement);
        Assert.Contains("track_index", missing);
        Assert.Contains("duration_tick", missing);
    }

    [Fact]
    public async Task EventBufferIsMonotonicBoundedAndWaitable()
    {
        var buffer = new BoundedEventBuffer(2);
        McpEvent first = buffer.Publish("one");
        McpEvent second = buffer.Publish("two");
        McpEvent third = buffer.Publish("three");

        Assert.True(first.EventId < second.EventId && second.EventId < third.EventId);
        Assert.Equal(new[] { second.EventId, third.EventId }, buffer.ReadAfter(0).Select(item => item.EventId));

        Task<IReadOnlyList<McpEvent>> wait = buffer.WaitAsync(third.EventId, TimeSpan.FromSeconds(2));
        buffer.Publish("four");
        Assert.Single(await wait);
    }

    [Fact]
    public async Task EventWaitTimesOutWithoutHoldingAWorkerIndefinitely()
    {
        var buffer = new BoundedEventBuffer();
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<McpEvent> result = await buffer.WaitAsync(0, TimeSpan.FromMilliseconds(30));
        Assert.Empty(result);
        Assert.InRange(watch.ElapsedMilliseconds, 1, 1000);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public void QueryBudgetHasDeterministicDenseProjectLimits(int itemCount)
    {
        var budget = new QueryBudget(itemCount + 1, QueryBudget.DefaultMaxResponseBytes, 5000);
        for (int index = 0; index < itemCount; index++)
            Assert.True(budget.TryScan());
        Assert.Equal(itemCount, budget.ScannedItems);
        Assert.InRange(budget.ElapsedMilliseconds, 0, 5000);
    }
}

public sealed class StageSevenContractTests
{
    [Fact]
    public void ActivePartActivationUsesPostconditionInsteadOfNativeChangeFlag()
    {
        Assert.False(SelectionActivation.ShouldActivate(alreadyActive: true));
        Assert.True(SelectionActivation.Succeeded(requestedPartIsActive: true));
        Assert.True(SelectionActivation.ShouldActivate(alreadyActive: false));
        Assert.False(SelectionActivation.Succeeded(requestedPartIsActive: false));
    }

    [Fact]
    public void UiCapabilitiesExposeConfirmedPanelEntryPointsAndDisablePlaybackRate()
    {
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "ui.selection" && item.Implemented);
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "transport.grid_step" && item.Implemented);
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "transport.start_mode" && item.Implemented);
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "transport.playback_rate" && !item.Implemented && item.Availability == "unsupported");
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "ui.panel.lower_zone" && item.Implemented && item.Availability == "host_validation_required");
        Assert.Contains(McpContractCatalog.StageSevenCapabilities, item => item.Id == "ui.panel.right_zone" && item.Implemented && item.Availability == "host_validation_required");
    }

    [Fact]
    public void StageSevenCapabilitiesRemainUnverifiedUntilHostMatrixRuns()
        => Assert.All(McpContractCatalog.StageSevenCapabilities, item => Assert.False(item.HostVerified));
}

public sealed class StageEightContractTests
{
    [Fact]
    public void RevertProjectContextSerializesAsProtocolObject()
    {
        var result = new
        {
            reverted = true,
            outcome = "discarded_then_reverted",
            project = new ProjectContext("instance", "replacement-project", 9),
        };

        JsonElement json = JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions);
        JsonElement project = json.GetProperty("project");
        Assert.Equal("instance", project.GetProperty("instance_id").GetString());
        Assert.Equal("replacement-project", project.GetProperty("project_id").GetString());
        Assert.Equal(9, project.GetProperty("revision").GetInt64());
    }

    [Fact]
    public void NativeLifecycleCapabilitiesRemainUnverifiedUntilHostMatrixRuns()
    {
        CapabilityStatus[] capabilities = McpContractCatalog.BaselineCapabilities
            .Where(item => item.Id.StartsWith("project.", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(capabilities, item => item.Id == "project.revert" && item.Implemented);
        Assert.Contains(capabilities, item => item.Id == "project.native_import.project" && item.Implemented);
        Assert.Contains(capabilities, item => item.Id == "project.native_import.midi" && item.Implemented);
        Assert.Contains(capabilities, item => item.Id == "project.native_import.tempo_time_signature" && item.Implemented);
        Assert.Contains(capabilities, item => item.Id == "project.native_import.audio" && item.Implemented);
        Assert.All(capabilities, item => Assert.False(item.HostVerified));
    }

    [Fact]
    public void JobProtocolDistinguishesNativeCompletionAfterCancellationRequest()
        => Assert.True(Enum.IsDefined(BridgeJobStatus.CompletedAfterCancel));
}
