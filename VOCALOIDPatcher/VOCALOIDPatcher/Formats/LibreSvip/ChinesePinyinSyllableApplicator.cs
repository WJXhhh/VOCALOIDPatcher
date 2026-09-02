using System.Collections.Generic;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.G2PA;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Formats.LibreSvip;

internal static class ChinesePinyinSyllableApplicator
{
    public static bool TrySetSyllables(
        WIVSMNote note,
        IReadOnlyList<ChinesePinyinSyllable> syllables,
        int languageId,
        out (bool IsSuccess, WIVSMNote? NextNote) result)
    {
        result = (false, null);
        if (syllables.Count == 0)
            return false;

        try
        {
            using var syllablesData = new SyllablesData();
            var nativeSyllables = new List<SyllableData>(syllables.Count);
            syllablesData.InitializeData(syllables.Count);
            try
            {
                for (int i = 0; i < syllables.Count; i++)
                {
                    var syllableData = new SyllableData
                    {
                        syllable = syllables[i].Lyric,
                        phonemes = syllables[i].Phonemes,
                    };
                    nativeSyllables.Add(syllableData);
                    syllablesData.SetSyllableData(syllableData, i);
                }

                result = G2PAMultiLingualManager.SetSyllables(note, syllablesData, syllables.Count, languageId);
                return result.IsSuccess;
            }
            finally
            {
                foreach (SyllableData nativeSyllable in nativeSyllables)
                    nativeSyllable.Dispose();
            }
        }
        catch
        {
            result = (false, null);
            return false;
        }
    }

    public static Syllables CreateCandidate(IReadOnlyList<ChinesePinyinSyllable> syllables, int languageId)
    {
        var arguments = new List<SyllableArgs>(syllables.Count);
        foreach (var syllable in syllables)
            arguments.Add(new SyllableArgs(syllable.Lyric, syllable.Phonemes));
        return new Syllables(languageId, arguments);
    }
}
