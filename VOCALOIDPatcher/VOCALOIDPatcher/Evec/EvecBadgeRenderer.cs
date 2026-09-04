using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VDM;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.Evec;

internal static class EvecBadgeRenderer
{
    private static readonly Brush PowerBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xD3, 0x54, 0x00))); // Dark Orange
    private static readonly Brush SoftBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xB9)));  // Blue
    private static readonly Brush AccentBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)));// Amber
    private static readonly Brush MildBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x16, 0xA0, 0x85)));  // Teal
    private static readonly Brush ReleaseBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD)));// Purple
    private static readonly Brush ExtensionBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x55, 0x65, 0x73)));// Slate
    private static readonly Brush DefaultBadgeBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0x33, 0x33)));
    private static readonly Brush TextBrush = FreezeBrush(new SolidColorBrush(Colors.White));
    private static readonly Typeface BadgeTypeface = new(new FontFamily("Segoe UI, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static Brush FreezeBrush(Brush b)
    {
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    internal static void RenderBadge(UINote noteControl, DrawingContext dc)
    {
        if (!EvecService.IsEnabled)
            return;

        var model = noteControl.Note;
        if (model == null)
            return;

        var part = model.Parent as WIVSMMidiPart;
        var voiceBank = part?.VoiceBank();
        if (voiceBank == null)
            return;

        var caps = EvecVoicebankDetector.GetCapabilities(voiceBank);
        if (!caps.IsSupported)
            return;

        var state = EvecService.GetState(model);
        if (!state.HasAnyEvec)
            return;

        bool hasValidRelease = state.ReleaseId != EvecConstants.ReleaseNone;
        bool hasValidAttack = state.AttackId != EvecConstants.AttackNone;
        bool hasValidColor = state.VoiceColorId != EvecConstants.VoiceColorNone;
        bool hasValidExtension = state.ConsonantExtension > EvecConstants.MinConsonantExtension;

        if (!hasValidRelease && !hasValidAttack && !hasValidColor && !hasValidExtension)
            return;

        double width = noteControl.ActualWidth;
        double height = noteControl.ActualHeight;

        if (width < 12.0 || height < 8.0)
            return;

        double dpiScale = 1.0;
        try
        {
            dpiScale = VisualTreeHelper.GetDpi(noteControl).PixelsPerDip;
        }
        catch { }

        // If protected, leave room for native padlock icon at right
        double right = model.IsProtected ? width - 8.0 : width - 2.0;
        double top = 1.0;
        double badgeHeight = Math.Min(11.0, Math.Max(7.0, height - 2.0));

        // 1. Render Voice Release indicator (bottom-right dot/glyph)
        if (hasValidRelease && width >= 14.0 && height >= 8.0)
        {
            double relX = width - 4.5;
            double relY = height - 4.0;
            double radius = state.ReleaseId == EvecConstants.ReleaseBreathLong ? 2.5 : 1.8;
            dc.DrawEllipse(ReleaseBrush, null, new Point(relX, relY), radius, radius);
        }

        // 2. Render pronunciation-extension repeat count.
        if (hasValidExtension && width >= 20.0 && height >= 8.0)
        {
            string extensionText = $"×{state.ConsonantExtension}";
            double extensionWidth = Math.Min(13.0, badgeHeight + 4.0);
            right -= extensionWidth;
            if (right >= 2.0)
            {
                var rect = new Rect(right, top, extensionWidth, badgeHeight);
                dc.DrawRoundedRectangle(ExtensionBrush, null, rect, 2.0, 2.0);
                DrawCenteredText(dc, extensionText, rect, dpiScale, Math.Min(7.5, badgeHeight - 2.0));
                right -= 2.0;
            }
        }

        // 3. Render Consonant Attack badge
        if (hasValidAttack && width >= 20.0 && height >= 8.0)
        {
            bool isAccent = EvecConstants.IsAccentAttack(state.AttackId);
            string attackText = isAccent ? "!" : "~";
            Brush attackBg = isAccent ? AccentBrush : MildBrush;
            double attackWidth = Math.Min(9.0, badgeHeight);
            right -= attackWidth;

            if (right >= 2.0)
            {
                var rect = new Rect(right, top, attackWidth, badgeHeight);
                dc.DrawRoundedRectangle(attackBg, null, rect, 2.0, 2.0);
                DrawCenteredText(dc, attackText, rect, dpiScale, Math.Min(8.0, badgeHeight - 2.0));
                right -= 2.0; // Margin between badges
            }
        }

        // 4. Render Voice Color badge
        if (hasValidColor && width >= 16.0 && height >= 8.0)
        {
            string colorText = state.VoiceColorId switch
            {
                EvecConstants.VoiceColorPower    => "P",
                EvecConstants.VoiceColorSoft     => "S",
                EvecConstants.VoiceColorWhisper  => "W",
                EvecConstants.VoiceColorHusky    => "H",
                EvecConstants.VoiceColorNative   => "N",
                EvecConstants.VoiceColorPower1   => "P1",
                EvecConstants.VoiceColorFalsetto => "F",
                EvecConstants.VoiceColorDark     => "D",
                EvecConstants.VoiceColorCute     => "C",
                _                                => "C"
            };

            Brush colorBg = state.VoiceColorId switch
            {
                EvecConstants.VoiceColorPower => PowerBrush,
                EvecConstants.VoiceColorSoft  => SoftBrush,
                _                             => DefaultBadgeBrush
            };

            double colorWidth = Math.Min(11.0, badgeHeight + 2.0);
            right -= colorWidth;

            if (right >= 2.0)
            {
                var rect = new Rect(right, top, colorWidth, badgeHeight);
                dc.DrawRoundedRectangle(colorBg, null, rect, 2.0, 2.0);
                DrawCenteredText(dc, colorText, rect, dpiScale, Math.Min(8.5, badgeHeight - 2.0));
            }
        }
    }

    private static void DrawCenteredText(DrawingContext dc, string text, Rect bounds, double dpiScale, double emSize)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            BadgeTypeface,
            emSize,
            TextBrush,
            dpiScale);

        double x = bounds.X + (bounds.Width - formatted.Width) / 2.0;
        double y = bounds.Y + (bounds.Height - formatted.Height) / 2.0;
        dc.DrawText(formatted, new Point(x, y));
    }
}
