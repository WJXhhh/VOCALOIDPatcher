using System;
using System.Linq;
using VOCALOIDPatcher.Config;
using VOCALOIDPatcher.Formats.LibreSvip;

namespace VOCALOIDPatcher.Formats.LibreSvip.Plugins.Vsqx;

internal static class VsqxPhonemeGenerator
{
    private const string DefaultChinesePhoneme = "l a";
    private const string DefaultJapanesePhoneme = "4 a";

    public static (string Lyric, string Phoneme) Generate(string lyric, VocaloidLanguage language)
    {
        if (VsqxPhonemeMaps.LegatoChars.Contains(lyric))
            return (lyric, "-");

        if (Settings.ExtendedChinesePinyin
            && ChinesePinyinPhonemeConverter.TryConvertSequence(lyric, out var specialSyllables, out _)
            && ChinesePinyinPhonemeConverter.IsVocaloidSpecialSequence(specialSyllables))
        {
            return (lyric, string.Join(" ", specialSyllables.Select(syllable => syllable.Phonemes)));
        }

        switch (language)
        {
            case VocaloidLanguage.SimplifiedChinese:
            {
                string key = lyric;
                if (VsqxPhonemeMaps.Pinyin2Xsampa.TryGetValue(key, out var exactPhoneme))
                    return (key, exactPhoneme);

                if (Settings.ExtendedChinesePinyin
                    && ChinesePinyinPhonemeConverter.TryConvertSequence(lyric, out var syllables, out _))
                {
                    return (key, string.Join(" ", syllables.Select(syllable => syllable.Phonemes)));
                }

                return (key, DefaultChinesePhoneme);
            }
            case VocaloidLanguage.Japanese:
            {
                string phoneme = VsqxPhonemeMaps.Romaji2Xsampa.TryGetValue(lyric, out var jp)
                    ? jp
                    : DefaultJapanesePhoneme;
                return (lyric, phoneme);
            }
            case VocaloidLanguage.Korean:
                return (lyric, VsqxKoreanRomanizer.Hangul2Xsampa(lyric));
            default:
                return (lyric, DefaultChinesePhoneme);
        }
    }
}
