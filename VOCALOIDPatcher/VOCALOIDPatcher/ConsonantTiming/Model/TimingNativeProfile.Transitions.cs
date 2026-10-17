namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class TimingNativeProfile
{
    internal int DseTransitionSynthesis => Offset(nameof(DseTransitionSynthesis));
    internal int RowMaterial => Offset(nameof(RowMaterial));
    internal int RowStage => Offset(nameof(RowStage));
    internal int RowStageLimit => Offset(nameof(RowStageLimit));
    internal int RowReverse => Offset(nameof(RowReverse));
    internal int MaterialFrames => Offset(nameof(MaterialFrames));
    internal int MaterialPitch => Offset(nameof(MaterialPitch));
    internal int MaterialPitch2 => Offset(nameof(MaterialPitch2));
    internal int MaterialDynamics => Offset(nameof(MaterialDynamics));
    internal int MaterialTempo => Offset(nameof(MaterialTempo));
    internal int MaterialAlignmentBegin => Offset(nameof(MaterialAlignmentBegin));
    internal int MaterialAlignmentEnd => Offset(nameof(MaterialAlignmentEnd));
    internal int StateAnalysisPrimary => Offset(nameof(StateAnalysisPrimary));
    internal int StateAnalysisAuxiliary => Offset(nameof(StateAnalysisAuxiliary));
}
