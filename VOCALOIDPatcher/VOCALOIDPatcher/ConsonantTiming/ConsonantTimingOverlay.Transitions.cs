using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VOCALOIDPatcher.Translation;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    internal static void BeforePartPublication(WIVSMSequence source)
    {
        foreach (var pair in States)
        {
            var state = pair.Value;
            if (state._view.DataContext is not MusicalEditorViewModel { VSMSequence: { } current } || (nint)current != (nint)source) continue;
            if (!state._view.Dispatcher.CheckAccess()) throw new InvalidOperationException("Part publication requires the view owner thread.");
            // Release speculative plans before the committed renderer needs
            // the shared backend. Cancellation is checked between candidates.
            var preparation = state._transitionPreparation;
            state.InvalidateTransitionContext();
            preparation?.Dispose(); // Drain the canceled bounded plan lease.
            state.RequestRefresh();
        }
    }
    private sealed record TransitionBandItem(TransitionEdge Edge, TransitionSegment Row, UIEmotionNote Owner,
        double Left, double Right, double Split, double Y, double Height, float Decibels,
        bool LeftLocked = false, bool RightLocked = false, bool LeftShared = false, bool RightShared = false, double? PhaseX = null);
    private sealed record TransitionHit(TransitionBandItem Item, TransitionEndpoint? Endpoint, bool Gain, bool Phase = false);
    private sealed class TransitionBandDrawing : FrameworkElement
    {
        internal IReadOnlyList<TransitionBandItem> Items = [];
        internal TransitionIdentity? Selected;
        internal System.Collections.Generic.HashSet<TransitionIdentity> Related = [];
        internal Pen? Normal, Active, Reference;
        internal Brush? Fill;
        internal Rect? Range;
        internal double? Requested;
        internal double[] Reachable = [];
        protected override void OnRender(DrawingContext dc)
        {
            foreach (var item in Items)
            {
                var pen = item.Edge.Identity == Selected || Related.Contains(item.Edge.Identity) ? Active : Normal;
                double width = Math.Max(.5, item.Right - item.Left);
                if (item.Edge.Identity == Selected) dc.DrawRectangle(Fill, null, new(item.Left, item.Y, width, item.Height));
                dc.DrawLine(pen, new(item.Left, item.Y), new(item.Right, item.Y));
                dc.DrawLine(pen, new(item.Left, item.Y), new(item.Left, item.Y + item.Height));
                dc.DrawLine(pen, new(item.Right, item.Y), new(item.Right, item.Y + item.Height));
                if (item.LeftLocked) dc.DrawRectangle(Reference?.Brush, null, new(item.Left - 1, item.Y, 3, item.Height));
                if (item.RightLocked) dc.DrawRectangle(Reference?.Brush, null, new(item.Right - 1, item.Y, 3, item.Height));
                if (item.Edge.Identity == Selected)
                {
                    if (item.LeftShared) Diamond(dc, pen, item.Left, item.Y);
                    if (item.RightShared) Diamond(dc, pen, item.Right, item.Y);
                }
                dc.DrawLine(Reference, new(item.Split, item.Y), new(item.Split, item.Y + item.Height));
                if (item.PhaseX is { } onsetX && item.Edge.Identity == Selected) Diamond(dc, Active, onsetX, item.Y - 3);
                if (width >= 10)
                {
                    double y = item.Y + item.Height * .68 - Math.Clamp(item.Decibels, -12, 12) * item.Height / 60;
                    dc.DrawLine(pen, new(item.Left + 3, y), new(item.Right - 3, y));
                    dc.DrawEllipse(pen?.Brush, null, new((item.Left + item.Right) / 2, y), 1.7, 1.7);
                }
            }
            if (Range is { } range) dc.DrawRectangle(Fill, Reference, range);
            if (Range is { } certified) foreach (double x in Reachable)
                dc.DrawLine(Active, new(x, certified.Bottom - 2), new(x, certified.Bottom));
            if (Requested is { } requested && Range is { } r) dc.DrawLine(Reference, new(requested, r.Top), new(requested, r.Bottom));
        }
        private static void Diamond(DrawingContext dc, Pen? pen, double x, double y)
        {
            dc.DrawLine(pen, new(x, y - 4), new(x + 2, y - 2));
            dc.DrawLine(pen, new(x + 2, y - 2), new(x, y));
            dc.DrawLine(pen, new(x, y), new(x - 2, y - 2));
            dc.DrawLine(pen, new(x - 2, y - 2), new(x, y - 4));
        }
    }
    private readonly TransitionBandDrawing _transitionDrawing = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly List<TransitionBandItem> _transitionItems = new();
    private SampledTimingHost.TransitionPreparation? _transitionPreparation;
    private TransitionLayout? _transitionLayout;
    private TransitionControlPlan? _transitionControls;
    private TransitionExecutionPlan? _transitionExecution;
    private TransitionIdentity? _transitionSelected;
    private TransitionHit? _transitionHover;
    private CancellationTokenSource? _transitionCancellation;
    private Task? _transitionWork;
    private long _transitionGeneration, _transitionRequestId;
    private string? _transitionError;
    private nint _transitionPart;
    private Button? _transitionPanelTool;
    private Popup? _transitionPanel;
    private Action? _updateTransitionPanel;
    private bool _transitionLoading;
    private void PostTransition(Action callback)
    {
        if (_view.Dispatcher.HasShutdownStarted) return;
        _view.Dispatcher.BeginInvoke(new Action(() =>
        { try { callback(); } catch (Exception error) { TransitionFailure(error); } }));
    }
    private static string TransitionText(string suffix, params object[] args) => TranslationManager.Tr("VOCALOIDPatcher_Transitions_" + suffix, args);
    private static double TransitionBandHeight(MusicalEditorViewModel vm) => Math.Clamp(vm.OneKeyHeight * .32, 5, 10);
    private static string TransitionNumber(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);
    private static string TransitionPhoneName(string value, bool from) => value == "Sil" ? TransitionText(from ? "Start" : "End") : value;
    private static string TransitionName(TransitionIdentity edge) => TransitionPhoneName(edge.From, true) + " → " + TransitionPhoneName(edge.To, false);

    private void AttachTransitionBand()
    {
        _transitionDrawing.Normal = _reference; _transitionDrawing.Active = _editable; _transitionDrawing.Reference = _reference;
        _transitionDrawing.Fill = _highlight; _root.Children.Add(_transitionDrawing);
        _transitionPanelTool = new Button { Content = TransitionText("Precise"), Focusable = false, Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed };
        _transitionPanelTool.Click += (_, _) => OpenTransitionPanel(Mouse.GetPosition(_view));
        _editTools.Children.Add(_transitionPanelTool);
    }
    private void BeginTransitionRefresh(MusicalEditorViewModel vm)
    {
        _transitionDrawing.Width = Math.Max(vm.SongWidth, _view.ActualWidth);
        _transitionDrawing.Height = Math.Max(vm.AllKeyHeight, _view.ActualHeight);
        _transitionDrawing.Visibility = TransitionMode ? Visibility.Visible : Visibility.Collapsed;
        if (_transitionPanelTool != null) _transitionPanelTool.Visibility = TransitionMode ? Visibility.Visible : Visibility.Collapsed;
        if (!TransitionMode) return;
        if (_transitionPreparation != null && (vm.ActivePart == null || (nint)vm.ActivePart != _transitionPart ||
            _transitionPreparation.Revision != _transitionPreparation.Session.Revision)) InvalidateTransitionContext();
        if (!_transitionLoading && _transitionPreparation == null && _transitionError == null && vm.ActivePart is { } part)
            StartTransitionPreparation(part);
    }
    private void StartTransitionPreparation(WIVSMMidiPart part)
    {
        if (_transitionWork != null) return;
        long generation = _transitionGeneration;
        SampledTimingHost.TransitionPreparation? prepared = null;
        try
        {
            _transitionLoading = true; _transitionPart = (nint)part;
            prepared = SampledTimingHost.PrepareTransitions(part);
            _transitionPreparation = prepared;
            var cancellation = _transitionCancellation = new CancellationTokenSource();
            var owned = prepared;
            _transitionWork = Task.Run(() => RetryTransition(() => owned.Query(cancellation.Token), cancellation.Token));
            var work = (Task<(TransitionLayout Layout, TransitionControlPlan Controls, TransitionExecutionPlan Execution)>)_transitionWork;
            work.ContinueWith(done => PostTransition(() =>
            {
                if (generation != _transitionGeneration) { _ = done.Exception; return; }
                _transitionWork = null; _transitionLoading = false;
                if (done.IsCanceled) return;
                if (done.IsFaulted) { TransitionFailure(done.Exception!.GetBaseException()); return; }
                if (!TransitionPreparationValid()) { InvalidateTransitionContext(); return; }
                (_transitionLayout, _transitionControls, _transitionExecution) = done.Result;
                if (_transitionSelected == null || !_transitionLayout.Edges.Any(e => e.Identity == _transitionSelected))
                    _transitionSelected = _transitionLayout.Edges.FirstOrDefault()?.Identity;
                _updateTransitionPanel?.Invoke(); RequestRefresh();
            }), TaskScheduler.Default);
        }
        catch (Exception error) { _transitionLoading = false; prepared?.Dispose(); _transitionPreparation = null; TransitionFailure(error); }
    }
    private static T RetryTransition<T>(Func<T> action, CancellationToken cancellation)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return action(); }
            catch (TimeoutException) when (timer.ElapsedMilliseconds < 2000)
            { if (cancellation.WaitHandle.WaitOne(8)) cancellation.ThrowIfCancellationRequested(); }
        }
    }
    private bool TransitionPreparationValid() => Enabled(_view) && TransitionMode && _transitionPreparation is { } preparation &&
        _view.DataContext is MusicalEditorViewModel { VSMSequence: { IsOpen: true, IsStaged: false } source, ActivePart: { } part } &&
        (nint)source == preparation.Source && (nint)part == _transitionPart && preparation.Revision == preparation.Session.Revision;

    private void RefreshTransitionBands(MusicalEditorViewModel vm, UIEmotionNote[] visuals)
    {
        _transitionItems.Clear();
        if (!TransitionMode || _transitionLayout == null || _transitionExecution == null || vm.ActivePart is not { } part) return;
        var rows = _transitionExecution.Segments.Where(r => r.To != null).ToArray();
        var controls = TransitionControlPlan.FromCompiled(_transitionLayout, _transitionExecution.Controls);
        var joints = _transitionLayout.ConditionalJunctions(controls)
            .ToDictionary(j => j.OriginalFrame);
        var owners = visuals.Where(v => v.Note != null).GroupBy(v => v.Note!.CppObjPtr).ToDictionary(g => g.Key, g => g.First());
        double visibleLeft = (vm.PianorollViewer?.HorizontalOffset ?? 0) - 15;
        double visibleRight = visibleLeft + (vm.PianorollViewer?.ViewportWidth ?? _view.ActualWidth) + 30;
        for (int i = 0; i < _transitionLayout.Edges.Length; i++)
        {
            var edge = _transitionLayout.Edges[i]; var row = rows[i];
            var phone = edge.Identity.Right ?? edge.Identity.Left!;
            var note = part.GetNote(checked((ulong)phone.Note.Note));
            if (note == null) continue;
            double X(double frame) => vm.CalcTickToViewPosition(note.Sequence!.GetTickFromTime(part.AbsBeginTick, (frame - 3087) * 256d / 44100 - .5));
            double l = X(row.Begin), r = X(row.End), y = vm.CalcNoteNumberTopPosition(note.NoteNumber);
            if (r < visibleLeft || l > visibleRight || y + vm.OneKeyHeight < (vm.PianorollViewer?.VerticalOffset ?? 0) ||
                y > (vm.PianorollViewer?.VerticalOffset ?? 0) + (vm.PianorollViewer?.ViewportHeight ?? _view.ActualHeight)) continue;
            if (!owners.TryGetValue(note.CppObjPtr, out var owner)) owner = new UIEmotionNote { Note = note };
            float db = _transitionExecution.Controls.Gains.FirstOrDefault(g => g.Edge == edge.Identity)?.Decibels ?? 0;
            double height = TransitionBandHeight(vm);
            var left = joints[edge.Original.Begin]; var right = joints[edge.Original.End];
            var expressionRange = TransitionExpressionControl.Range(row, edge.Identity, controls);
            _transitionItems.Add(new(edge, row, owner, l, r, X(row.Split), y + vm.OneKeyHeight - height - 1, height, db,
                left.MinimumCandidate == left.MaximumCandidate, right.MinimumCandidate == right.MaximumCandidate,
                left.Aliases.Length > 1, right.Aliases.Length > 1, expressionRange is { } range ? X(TransitionExpressionControl.HandleFrame(range,
                    _transitionExecution.Controls.Phases.FirstOrDefault(p => p.Edge == edge.Identity)?.Amount ?? 0)) : null));
        }
        _transitionDrawing.Items = _transitionItems; _transitionDrawing.Selected = _transitionSelected;
        if (_transitionPanelTool != null && _transitionSelected != null)
            _transitionPanelTool.Content = TransitionName(_transitionSelected) + "…";
        _transitionDrawing.Related = _transitionSelected == null ? [] : joints.Values
            .Where(j => j.Aliases.Any(a => a.Edge == _transitionSelected)).SelectMany(j => j.Aliases).Select(a => a.Edge).ToHashSet();
        _transitionDrawing.InvalidateVisual();
    }
    private TransitionHit? HitTransition(Point point)
    {
        if (!TransitionMode || _transitionLayout == null) return null;
        return HitTransitionBand(_transitionItems, _transitionSelected, point);
    }
    private static TransitionHit? HitTransitionBand(IReadOnlyList<TransitionBandItem> items, TransitionIdentity? identity, Point point)
    {
        var selected = items.FirstOrDefault(i => i.Edge.Identity == identity);
        if (selected?.PhaseX is { } onsetX && Math.Abs(point.X - onsetX) <= 6 && point.Y >= selected.Y - 10 && point.Y <= selected.Y - 2)
            return new(selected, null, false, true);
        if (selected != null && selected.Right - selected.Left >= 20 && point.Y >= selected.Y - 2 && point.Y <= selected.Y + selected.Height + 3)
        {
            if (Math.Abs(point.X - selected.Left) <= 6) return new(selected, TransitionEndpoint.Begin, false);
            if (Math.Abs(point.X - selected.Right) <= 6) return new(selected, TransitionEndpoint.End, false);
        }
        var item = items.Where(i => point.X >= i.Left && point.X <= i.Right && point.Y >= i.Y - 1 && point.Y <= i.Y + i.Height + 2)
            .OrderBy(i => i.Edge.Identity == identity ? 0 : 1).FirstOrDefault();
        if (item == null) return null;
        double gainY = item.Y + item.Height * .68 - Math.Clamp(item.Decibels, -12, 12) * item.Height / 60;
        return new(item, null, item.Right - item.Left >= 20 && Math.Abs(point.Y - gainY) <= 3);
    }
    private bool SearchTransition(Point point, out PianorollView.EmotionModeSearchResults result)
    {
        result = default; _transitionHover = HitTransition(point);
        if (_transitionHover is not { } hit) return false;
        bool locked = hit.Endpoint == TransitionEndpoint.Begin ? hit.Item.LeftLocked : hit.Endpoint == TransitionEndpoint.End && hit.Item.RightLocked;
        _view.Cursor = locked ? Cursors.No : hit.Endpoint.HasValue || hit.Phase ? Cursors.SizeWE : hit.Gain ? Cursors.SizeNS : Cursors.Arrow;
        ShowTransitionTip(hit.Item, point, locked);
        result = new(hit.Item.Owner, PianorollView.HitObjectTypeEnum.Velocity, hit.Item.Left, hit.Item.Right, hit.Endpoint.HasValue); return true;
    }
    private void ShowTransitionTip(TransitionBandItem item, Point point, bool locked = false)
    {
        string value = TransitionText("Tip", TransitionPhoneName(item.Row.From, true), TransitionPhoneName(item.Row.To!, false),
            TransitionNumber((item.Row.Split - item.Row.Begin) * 256000d / 44100), TransitionNumber((item.Row.End - item.Row.Split) * 256000d / 44100), FormatGain(item.Decibels));
        var coverages = _transitionExecution?.Controls.Coverages ?? _transitionControls?.Coverages ?? [];
        if (TransitionExpressionControl.Range(item.Row, item.Edge.Identity, coverages) != null)
        {
            value += "\n" + TransitionText("PhaseTip", TransitionNumber(
            (_transitionExecution?.Controls.Phases.FirstOrDefault(p => p.Edge == item.Edge.Identity)?.Amount ?? 0) * 100));
        }
        if (TransitionExpressionControl.Range(item.Row, item.Edge.Identity, coverages, requireProgress: false) is { } range)
        {
            var coverage = coverages.FirstOrDefault(c => c.Edge == item.Edge.Identity);
            value += "\n" + (coverage?.Sides == TransitionSourceSides.Both ? TransitionText("CoverageBoth") :
                TransitionText(coverage != null ? "CoverageActive" : "CoverageOnDrag", range.Side == TransitionSourceSides.Left ? item.Row.From : item.Row.To!));
        }
        ShowTip(value + (locked ? "\n" + TransitionText("Locked") : ""), point);
    }
    private void TransitionFailure(Exception error)
    {
        _transitionError = error is TimeoutException ? TransitionText("Busy") : TransitionText("Unavailable");
        CancelTransitionDrag();
        if (_transitionPanelTool != null) _transitionPanelTool.ToolTip = _transitionError;
        ShowTip(_transitionError, Mouse.GetPosition(_view));
        VOCALOIDPatcher.Utils.Debug.Print("Transition interaction: " + error.GetType().Name + ": " + error.Message);
        _updateTransitionPanel?.Invoke();
    }
    private void InvalidateTransitionContext()
    {
        _transitionGeneration++; _transitionRequestId++;
        _transitionCancellation?.Cancel();
        var preparation = _transitionPreparation; var cancellation = _transitionCancellation; var work = _transitionWork;
        _transitionPreparation = null; _transitionCancellation = null; _transitionWork = null;
        if (work is { IsCompleted: false }) work.ContinueWith(done =>
        {
            _ = done.Exception;
            PostTransition(() => { preparation?.Dispose(); cancellation?.Dispose(); });
        }, TaskScheduler.Default);
        else { preparation?.Dispose(); cancellation?.Dispose(); }
        CancelTransitionDrag(); _transitionLoading = false; _transitionError = null;
        _transitionLayout = null; _transitionControls = null; _transitionExecution = null; _transitionHover = null;
        _transitionItems.Clear(); _transitionDrawing.Items = []; _transitionDrawing.Visibility = Visibility.Collapsed;
        if (_transitionPanel != null) _transitionPanel.IsOpen = false;
        _transitionPanel = null; _updateTransitionPanel = null;
    }
}
