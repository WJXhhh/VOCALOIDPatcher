using VOCALOIDPatcher.Evec;
using VOCALOIDPatcher.McpBridge;

internal static class EvecMcpContractProbe
{
    internal static void Run()
    {
        var miku = EvecVoicebankDetector.GetDatabaseCapabilities("MIKU_V4X_Original_EVEC");
        var rin = EvecVoicebankDetector.GetDatabaseCapabilities("RIN_V4X_Power_EVEC");
        var luka = EvecVoicebankDetector.GetDatabaseCapabilities("LUKA_V4X_Soft_EVEC");
        var original = new EvecNoteState(101, 302, 201, 0);
        var changed = EvecMcpEdit.Resolve(new(null, null, 202, null, false), original, "t t#2 e e#2 *#1", miku);
        if (changed.VoiceColorId != 101 || changed.AttackId != 302 || changed.ReleaseId != 202 ||
            original.ReleaseId != 201 || changed.ConsonantExtension != 0)
            throw new InvalidOperationException("EVEC partial update changed unrelated or source state.");
        var cleared = EvecMcpEdit.Resolve(new(null, null, null, null, true), original, "t t#2 e e#2 *#1", miku);
        if (cleared.HasAnyEvec || EvecPhonemeRecomposer.Recompose("t t#2 e e#2 *#1", cleared) != "t e")
            throw new InvalidOperationException("EVEC clear did not recover the base phonemes.");
        if (EvecMcpEdit.Resolve(new(null, null, null, 3, false), EvecNoteState.Empty, "t e", luka).ConsonantExtension != 3)
            throw new InvalidOperationException("Luka's independent extension was rejected.");
        Reject(() => EvecMcpEdit.Resolve(new(999, null, null, null, false), original, "t e", miku), false);
        Reject(() => EvecMcpEdit.Resolve(new(108, null, null, null, false), original, "t e", miku), true);
        Reject(() => EvecMcpEdit.Resolve(new(null, 302, null, null, false), EvecNoteState.Empty, "t e", rin), true);
        Reject(() => EvecMcpEdit.Resolve(new(null, null, null, 1, false), EvecNoteState.Empty, "a i", miku), true);
        Reject(() => EvecMcpEdit.Resolve(new(101, null, null, null, false), EvecNoteState.Empty, "t e s", miku), true);
        var plainAttack = new EvecNoteState(0, 301, 0, 0);
        var repeat = EvecMcpEdit.Resolve(new(null, 0, null, 1, false), plainAttack, "t t e", rin);
        if (repeat.AttackId != 0 || repeat.ConsonantExtension != 1)
            throw new InvalidOperationException("Rin's identical-spelling logical choices were collapsed.");
        Console.WriteLine("evec.mcp-edit-contracts=passed");
    }

    private static void Reject(Action action, bool unsupported)
    {
        try { action(); }
        catch (NotSupportedException) when (unsupported) { return; }
        catch (ArgumentException) when (!unsupported) { return; }
        throw new InvalidOperationException("Invalid EVEC request was silently normalized or accepted.");
    }
}
