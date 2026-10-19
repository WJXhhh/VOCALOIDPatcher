using System;
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
using VOCALOIDPatcher.Patch.Patches;
using Yamaha.VOCALOID;
using Yamaha.VOCALOID.MusicalEditor;
using Yamaha.VOCALOID.VSM;

namespace VOCALOIDPatcher.ConsonantTiming;

internal sealed partial class ConsonantTimingOverlay
{
    private sealed class TransitionDrag(TransitionHit hit, MusicalEditorViewModel vm, Point down,
        double downFrame, TransitionControlPlan controls, TransitionExecutionPlan execution)
    {
        internal readonly TransitionHit Hit = hit;
        internal readonly MusicalEditorViewModel VM = vm;
        internal readonly Point Down = down;
        internal readonly double DownFrame = downFrame;
        internal readonly TransitionControlPlan Original = controls;
        internal readonly TransitionExecutionPlan OriginalExecution = execution;
        internal Point Point = down;
        internal bool Moved, Final;
    }
    private sealed record TransitionRequest(long Id, TransitionControlPlan Plan, TransitionEndpointRef? Endpoint,
        double? Goal, bool Final);
    private TransitionDrag? _transitionDrag;
    private TransitionRequest? _transitionPending;
    private DispatcherTimer? _transitionTimer;
    private CancellationTokenSource? _transitionJobCancellation;
    private static TransitionControlPlan TransitionGain(TransitionControlPlan current, TransitionIdentity edge, float db)
        => new(current.LayoutFingerprint, current.Junctions, current.Gains.Where(g => g.Edge != edge).Append(new(edge, db)), current.Onsets, current.Phases, current.Coverages);
    private static TransitionControlPlan PhaseDragPlan(TransitionControlPlan original, TransitionBandItem item, double delta, bool fine)
    {
        if (Math.Abs(delta) < 1e-8) return original;
        var range = TransitionExpressionControl.Range(item.Row, item.Edge.Identity, original)
            ?? throw new NotSupportedException("No editable complete-source interval.");
        float amount = original.Phases.FirstOrDefault(p => p.Edge == item.Edge.Identity)?.Amount ?? 0;
        double frame = TransitionExpressionControl.HandleFrame(range, amount) + delta * (fine ? .2 : 1);
        return TransitionExpressionControl.Progress(original, item.Row, item.Edge.Identity,
            (float)Math.Round(TransitionExpressionControl.AmountAt(range, frame), 3));
    }
    private static double TransitionFrameAt(MusicalEditorViewModel vm, WIVSMMidiPart part, double x)
        => 3087 + (part.Sequence!.PresendTimeSec + part.Sequence.GetTimeFromTick(part.AbsBeginTick,
            vm.CalcViewPositionToTick(x, QuantizeStrategy.None))) * 44100 / 256;
    private static double TransitionFrameX(MusicalEditorViewModel vm, WIVSMMidiPart part, double frame)
        => vm.CalcTickToViewPosition(part.Sequence!.GetTickFromTime(part.AbsBeginTick, (frame - 3087) * 256d / 44100 - .5));

    private bool BeginTransitionDrag(Point point, int clicks)
    {
        if (!TransitionMode) return false;
        if (_transitionJobCancellation != null && _transitionDrag == null) { ShowTip(TransitionText("Validating"), point); return true; }
        if (_transitionDrag != null) return true;
        if (!TransitionPreparationValid() || _transitionLayout == null || _transitionControls == null || _transitionExecution == null)
        {
            if (_transitionLoading || _transitionError != null) ShowTip(_transitionError ?? TransitionText("Preparing"), point);
            return false;
        }
        var hit = HitTransition(point);
        if (hit == null) return false;
        _transitionSelected = hit.Item.Edge.Identity; _transitionDrawing.Selected = _transitionSelected;
        _transitionDrawing.InvalidateVisual();
        if (_view.DataContext is not MusicalEditorViewModel vm || vm.ActivePart is not { } part) return true;
        if (hit.Endpoint is { } endpoint)
        {
            var joint = _transitionLayout.ConditionalJunction(new(hit.Item.Edge.Identity, endpoint), _transitionControls);
            if (joint.MinimumCandidate == joint.MaximumCandidate)
            { OpenTransitionPanel(point); ShowTip(TransitionText("Locked"), point); return true; }
        }
        if (!hit.Endpoint.HasValue && !hit.Gain && !hit.Phase) { OpenTransitionPanel(point); return true; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var reset = hit.Endpoint is { } side ? _transitionControls.WithJunction(_transitionLayout,
                new(hit.Item.Edge.Identity, side), _transitionLayout.Junction(new(hit.Item.Edge.Identity, side)).CurrentFrame) :
                hit.Phase ? TransitionExpressionControl.Reset(_transitionControls, hit.Item.Edge.Identity) : TransitionGain(_transitionControls, hit.Item.Edge.Identity, 0);
            QueueTransitionPlan(reset); return true;
        }
        _view.Focus();
        _transitionDrag = new(hit, vm, point, TransitionFrameAt(vm, part, point.X), _transitionControls, _transitionExecution);
        if (!_view.CaptureMouse()) { _transitionDrag = null; return true; }
        vm.EditorMode.IsIdleMouseOperation = false;
        _view.Cursor = hit.Endpoint.HasValue || hit.Phase ? Cursors.SizeWE : Cursors.SizeNS;
        return true;
    }
    private void MoveTransitionDrag(MouseEventArgs e)
    {
        e.Handled = true;
        if (_transitionDrag is not { } drag) return;
        if (!TransitionPreparationValid() || _view.DataContext != drag.VM) { CancelTransitionDrag(); return; }
        drag.Point = e.GetPosition(_view);
        if (!drag.Moved && (drag.Hit.Endpoint.HasValue || drag.Hit.Phase ? Math.Abs(drag.Point.X - drag.Down.X) : Math.Abs(drag.Point.Y - drag.Down.Y)) < 3) return;
        drag.Moved = true;
        if (drag.Hit.Phase)
        {
            double delta = TransitionFrameAt(drag.VM, _transitionPreparation!.Part, drag.Point.X) - drag.DownFrame;
            var plan = PhaseDragPlan(drag.Original, drag.Hit.Item, delta, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            _transitionPending = new(++_transitionRequestId, plan, null, null, drag.Final);
            if (TransitionExpressionControl.Range(drag.Hit.Item.Row, drag.Hit.Item.Edge.Identity, plan) is { } range)
            {
                double a = TransitionFrameX(drag.VM, _transitionPreparation.Part, range.Begin);
                double b = TransitionFrameX(drag.VM, _transitionPreparation.Part, range.End);
                _transitionDrawing.Range = new Rect(a, drag.Hit.Item.Y - 2, Math.Max(1, b - a), drag.Hit.Item.Height + 4);
                _transitionDrawing.Requested = null; _transitionDrawing.Reachable = [];
            }
        }
        else if (drag.Hit.Endpoint is { } side)
        {
            var endpoint = new TransitionEndpointRef(drag.Hit.Item.Edge.Identity, side);
            int initial = TransitionLayout.Position(drag.Hit.Item.Row, side);
            double delta = TransitionFrameAt(drag.VM, _transitionPreparation!.Part, drag.Point.X) - drag.DownFrame;
            double goal = initial + delta * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .2 : 1);
            _transitionPending = new(++_transitionRequestId, drag.Original, endpoint, goal, drag.Final);
            drag.VM.HorizontalDragScroll = drag.Point.X;
        }
        else
        {
            float db = (float)Math.Round(Math.Clamp(drag.Hit.Item.Decibels + (drag.Down.Y - drag.Point.Y) *
                ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? .05 : .25), -60, 12), 1);
            var plan = TransitionGain(drag.Original, drag.Hit.Item.Edge.Identity, db);
            _transitionPending = new(++_transitionRequestId, plan, null, null, drag.Final);
            _transitionExecution = drag.OriginalExecution with { Controls = plan.Compile(_transitionLayout!) };
            ShowTransitionTip(drag.Hit.Item with { Decibels = db }, drag.Point);
            RequestRefresh();
        }
        _transitionTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background,
            (_, _) => ProcessTransitionRequest(), _view.Dispatcher);
        _transitionTimer.Start();
    }
    private void EndTransitionDrag(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_transitionDrag is not { } drag) return;
        if (!TransitionPreparationValid()) { CancelTransitionDrag(); return; }
        if (!drag.Moved)
        { var point = e.GetPosition(_view); CancelTransitionDrag(); OpenTransitionPanel(point); return; }
        drag.Final = true; MoveTransitionDrag(e);
        if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        ShowTip(TransitionText("Validating"), e.GetPosition(_view));
        ProcessTransitionRequest();
    }
    private void QueueTransitionPlan(TransitionControlPlan plan)
    {
        if (!TransitionPreparationValid()) return;
        _transitionPending = new(++_transitionRequestId, plan, null, null, true);
        ProcessTransitionRequest();
    }
    private void ProcessTransitionRequest()
    {
        if (_transitionWork != null) return;
        if (_transitionPending is not { } request) { _transitionTimer?.Stop(); return; }
        if (!TransitionPreparationValid() || _transitionLayout == null || _transitionPreparation == null) { CancelTransitionDrag(); return; }
        _transitionPending = null;
        var prepared = _transitionPreparation; var layout = _transitionLayout;
        var job = _transitionJobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_transitionCancellation!.Token);
        var cancellation = job.Token; long generation = _transitionGeneration;
        _transitionWork = Task.Run(() => RetryTransition(() =>
        {
            TransitionReachableRange? range = null; var plan = request.Plan;
            if (request.Endpoint != null && request.Goal.HasValue)
            {
                range = prepared.Prepared.QueryTransitionRange(layout, request.Plan, request.Endpoint, prepared.Revision, cancellation, 0);
                int frame = range.ReachableFrames.OrderBy(f => Math.Abs(f - request.Goal.Value)).ThenBy(f => Math.Abs(f - range.Junction.CurrentFrame)).First();
                plan = request.Plan.WithJunction(layout, request.Endpoint, frame);
            }
            return (Plan: plan, Execution: prepared.Prepared.QueryTransitionControls(layout, plan, prepared.Revision, cancellation, 0), Range: range);
        }, cancellation));
        var work = (Task<(TransitionControlPlan Plan, TransitionExecutionPlan Execution, TransitionReachableRange? Range)>)_transitionWork;
        work.ContinueWith(done => PostTransition(() =>
        {
            if (ReferenceEquals(_transitionJobCancellation, job)) _transitionJobCancellation = null;
            job.Dispose();
            if (generation != _transitionGeneration) { _ = done.Exception; return; }
            _transitionWork = null;
            if (request.Id != _transitionRequestId) { _ = done.Exception; ProcessTransitionRequest(); return; }
            if (done.IsCanceled) return;
            if (done.IsFaulted) { TransitionFailure(done.Exception!.GetBaseException()); return; }
            if (!TransitionPreparationValid()) { InvalidateTransitionContext(); return; }
            _transitionError = null;
            var beforeControls = _transitionDrag?.Original ?? _transitionControls;
            var beforeExecution = _transitionDrag?.OriginalExecution ?? _transitionExecution;
            _transitionExecution = done.Result.Execution;
            if (done.Result.Range is { } range && _view.DataContext is MusicalEditorViewModel vm)
            {
                var item = _transitionDrag?.Hit.Item;
                if (item != null)
                {
                    double a = TransitionFrameX(vm, prepared.Part, range.Minimum), b = TransitionFrameX(vm, prepared.Part, range.Maximum);
                    _transitionDrawing.Range = new Rect(a, item.Y - 2, Math.Max(1, b - a), item.Height + 4);
                    _transitionDrawing.Reachable = range.ReachableFrames.Select(f => TransitionFrameX(vm, prepared.Part, f)).ToArray();
                    _transitionDrawing.Requested = request.Goal.HasValue ? TransitionFrameX(vm, prepared.Part, checked((int)Math.Round(request.Goal.Value))) : null;
                }
            }
            if (!request.Final && SelectedTransitionItem() is { } candidate)
                ShowTransitionTip(candidate, _transitionDrag?.Point ?? Mouse.GetPosition(_view), done.Result.Range?.ReachableFrames.Length == 1);
            if (request.Final)
            {
                if (_transitionDrag is { } drag) drag.VM.EditorMode.IsIdleMouseOperation = true;
                _transitionDrag = null; _transitionControls = done.Result.Plan;
                _transitionDrawing.Range = null; _transitionDrawing.Requested = null; _view.Cursor = null;
                try { SampledTimingHost.CommitTransitions(prepared, done.Result.Execution); }
                catch (Exception error)
                {
                    _transitionControls = beforeControls; _transitionExecution = beforeExecution;
                    TransitionFailure(error); RequestRefresh(); return;
                }
                ShowTip(TransitionText("Submitted"), Mouse.GetPosition(_view));
            }
            _updateTransitionPanel?.Invoke(); RequestRefresh(); ProcessTransitionRequest();
        }), TaskScheduler.Default);
    }
    private void CancelTransitionDrag()
    {
        _transitionRequestId++; _transitionPending = null; _transitionTimer?.Stop();
        _transitionJobCancellation?.Cancel();
        if (_transitionDrag is { } drag)
        {
            _transitionExecution = drag.OriginalExecution; _transitionControls = drag.Original;
            drag.VM.EditorMode.IsIdleMouseOperation = true;
            _transitionDrag = null;
            if (_view.IsMouseCaptured) _view.ReleaseMouseCapture();
        }
        _transitionDrawing.Range = null; _transitionDrawing.Requested = null; _transitionDrawing.Reachable = [];
        _view.Cursor = null;
        if (_transitionLayout != null) RequestRefresh();
    }

    private TransitionBandItem? SelectedTransitionItem()
    {
        if (_transitionSelected == null || _transitionLayout == null || _transitionExecution == null ||
            _view.DataContext is not MusicalEditorViewModel { ActivePart: { } part } vm) return null;
        int index = _transitionLayout.Edges.IndexOf(_transitionLayout.Edges.Single(e => e.Identity == _transitionSelected));
        var edge = _transitionLayout.Edges[index]; var row = _transitionExecution.Segments.Where(s => s.To != null).ElementAt(index);
        var phone = edge.Identity.Right ?? edge.Identity.Left!; var note = part.GetNote(checked((ulong)phone.Note.Note));
        if (note == null) return null;
        double height = TransitionBandHeight(vm);
        var expressionRange = TransitionExpressionControl.Range(row, edge.Identity, _transitionExecution.Controls.Coverages);
        return new(edge, row, new UIEmotionNote { Note = note }, TransitionFrameX(vm, part, row.Begin), TransitionFrameX(vm, part, row.End),
            TransitionFrameX(vm, part, row.Split), vm.CalcNoteNumberTopPosition(note.NoteNumber) + vm.OneKeyHeight - height - 1, height,
            _transitionExecution.Controls.Gains.FirstOrDefault(g => g.Edge == edge.Identity)?.Decibels ?? 0,
            PhaseX: expressionRange is { } range ? TransitionFrameX(vm, part, TransitionExpressionControl.HandleFrame(range,
                _transitionExecution.Controls.Phases.FirstOrDefault(p => p.Edge == edge.Identity)?.Amount ?? 0)) : null);
    }
    private void OpenTransitionPanel(Point point)
    {
        if (!TransitionMode) return;
        if (_transitionLayout == null || _transitionControls == null || !TransitionPreparationValid())
        { ShowTip(_transitionError ?? TransitionText("Preparing"), point); return; }
        if (_transitionPanel != null) { _transitionPanel.IsOpen = true; return; }
        var body = new StackPanel { Width = 300, Margin = new Thickness(12) };
        var title = new TextBlock { Text = TransitionText("Name"), FontWeight = FontWeights.SemiBold, Foreground = _view.Foreground };
        var choices = new ComboBox { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var edge in _transitionLayout.Edges)
        {
            string name = TransitionName(edge.Identity);
            if (_transitionLayout.Edges.Count(e => TransitionName(e.Identity) == name) > 1)
                name = TransitionText("Numbered", name, (edge.Identity.Right ?? edge.Identity.Left!).Note.Note + 1);
            choices.Items.Add(new ComboBoxItem { Content = name, Tag = edge.Identity });
        }
        TextBox Field(string key)
        {
            body.Children.Add(new TextBlock { Text = TransitionText(key), Margin = new Thickness(0, 5, 0, 2), Foreground = _view.Foreground });
            var input = new TextBox { Width = 110, HorizontalAlignment = HorizontalAlignment.Left }; body.Children.Add(input); return input;
        }
        body.Children.Add(title); body.Children.Add(choices);
        var coverageLabel = new TextBlock { Text = TransitionText("Coverage"), TextWrapping = TextWrapping.Wrap, MaxWidth = 275 };
        var coverage = new CheckBox { Content = coverageLabel, Foreground = _view.Foreground, Margin = new Thickness(0, 3, 0, 5),
            ToolTip = TransitionText("CoverageHint") };
        body.Children.Add(coverage);
        var resetExpression = new Button { Content = TransitionText("ResetExpression"), HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 2, 6, 2), ToolTip = TransitionText("ResetExpressionHint") };
        body.Children.Add(resetExpression);
        TextBox phase = Field("Phase"), left = Field("Left"), right = Field("Right"), gain = Field("Volume");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 5) };
        var apply = new Button { Content = TransitionText("Apply"), Padding = new Thickness(8, 3, 8, 3) };
        var reset = new Button { Content = TransitionText("Reset"), Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        var mute = new Button { Content = TransitionText("Mute"), Margin = new Thickness(7, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        actions.Children.Add(apply); actions.Children.Add(reset); actions.Children.Add(mute); body.Children.Add(actions);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = _view.Foreground, Margin = new Thickness(0, 6, 0, 0) };
        body.Children.Add(status);
        body.Children.Add(new TextBlock { Text = TransitionText("PhaseHint"), TextWrapping = TextWrapping.Wrap, Foreground = _view.Foreground, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) });
        var legacy = new TextBlock { Text = TransitionText("LegacyOnset"), TextWrapping = TextWrapping.Wrap, Foreground = _view.Foreground, Margin = new Thickness(0, 5, 0, 0) };
        body.Children.Add(legacy);
        var viewport = (_view.DataContext as MusicalEditorViewModel)?.PianorollViewer?.ViewportRect();
        var scroll = new ScrollViewer { Content = body, MaxHeight = Math.Max(80, (viewport?.Height ?? _view.ActualHeight) - 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var popup = new Popup { PlacementTarget = _view, Placement = PlacementMode.Relative, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = _view.TryFindResource("Brush_WindowBackground") as Brush ?? SystemColors.WindowBrush,
                BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), Child = scroll } };
        WpfTranslationPatch.MarkUntranslatable(body);
        bool updating = false;
        bool coverageDirty = false;
        var dirtyFields = new System.Collections.Generic.HashSet<TextBox>();
        foreach (var field in new[] { phase, left, right, gain })
            field.TextChanged += (_, _) => { if (!updating) dirtyFields.Add(field); };
        coverage.Checked += (_, _) => { if (!updating) coverageDirty = true; };
        coverage.Unchecked += (_, _) => { if (!updating) coverageDirty = true; };
        phase.TextChanged += (_, _) =>
        {
            if (!updating && !coverageDirty && coverage.IsEnabled)
            { updating = true; coverage.IsChecked = true; updating = false; }
        };
        void Update()
        {
            var item = SelectedTransitionItem();
            if (item == null || !TransitionPreparationValid()) { popup.IsOpen = false; return; }
            updating = true;
            title.Text = TransitionName(item.Edge.Identity);
            var current = TransitionControlPlan.FromCompiled(_transitionLayout!, _transitionExecution!.Controls);
            var range = TransitionExpressionControl.Range(item.Row, item.Edge.Identity, current);
            var coverageRange = TransitionExpressionControl.Range(item.Row, item.Edge.Identity, current, requireProgress: false);
            phase.IsEnabled = range != null;
            coverage.IsEnabled = coverageRange != null || current.Coverages.Any(c => c.Edge == item.Edge.Identity);
            if (!coverageDirty && !dirtyFields.Contains(phase)) coverage.IsChecked = current.Coverages.Any(c => c.Edge == item.Edge.Identity);
            var activeCoverage = current.Coverages.FirstOrDefault(c => c.Edge == item.Edge.Identity)?.Sides;
            string coverageText = activeCoverage == TransitionSourceSides.Both ? TransitionText("CoverageBoth") :
                coverageRange is { } interval ? TransitionText("CoverageSide", interval.Side == TransitionSourceSides.Left ? item.Row.From : item.Row.To!) : TransitionText("Coverage");
            coverageLabel.Text = coverageText;
            resetExpression.IsEnabled = current.Coverages.Any(c => c.Edge == item.Edge.Identity) || current.Phases.Any(p => p.Edge == item.Edge.Identity);
            if (!phase.IsKeyboardFocusWithin && !dirtyFields.Contains(phase)) phase.Text = TransitionNumber((_transitionExecution!.Controls.Phases.FirstOrDefault(p => p.Edge == item.Edge.Identity)?.Amount ?? 0) * 100);
            legacy.Visibility = _transitionExecution!.Controls.Onsets.Any(o => o.Edge == item.Edge.Identity) ? Visibility.Visible : Visibility.Collapsed;
            if (!left.IsKeyboardFocusWithin && !dirtyFields.Contains(left)) left.Text = TransitionNumber((item.Row.Split - item.Row.Begin) * 256000d / 44100);
            if (!right.IsKeyboardFocusWithin && !dirtyFields.Contains(right)) right.Text = TransitionNumber((item.Row.End - item.Row.Split) * 256000d / 44100);
            if (!gain.IsKeyboardFocusWithin && !dirtyFields.Contains(gain)) gain.Text = item.Decibels.ToString("0.#", CultureInfo.CurrentCulture);
            int selected = _transitionLayout!.Edges.IndexOf(item.Edge); choices.SelectedIndex = selected;
            var l = _transitionLayout.ConditionalJunction(new(item.Edge.Identity, TransitionEndpoint.Begin), _transitionControls!);
            var r = _transitionLayout.ConditionalJunction(new(item.Edge.Identity, TransitionEndpoint.End), _transitionControls!);
            left.IsEnabled = l.MinimumCandidate != l.MaximumCandidate; right.IsEnabled = r.MinimumCandidate != r.MaximumCandidate;
            status.Text = _transitionError ?? (left.IsEnabled && right.IsEnabled ? TransitionText("Shared") : TransitionText("Locked"));
            if (!phase.IsEnabled && _transitionError == null) status.Text += "\n" + TransitionText("PhaseLimited");
            updating = false;
        }
        void Apply()
        {
            if (!TransitionPreparationValid() || _transitionControls == null || _transitionLayout == null) { popup.IsOpen = false; return; }
            var item = SelectedTransitionItem(); if (item == null) return;
            bool Number(TextBox input, out double value) => double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
            if (!Number(phase, out double p) || !Number(left, out double l) || !Number(right, out double r) || !Number(gain, out double db) ||
                Math.Abs(p) > 100 || l < 0 || r < 0 || l > 24000 || r > 24000 || db is < -60 or > 12)
            { status.Text = TransitionText("Invalid"); return; }
            try
            {
                var plan = _transitionControls;
                // Unchanged extent fields must not freeze the implicit native
                // reference allocation when only source progress was edited.
                if (dirtyFields.Contains(left))
                    plan = plan.WithJunction(_transitionLayout, new(item.Edge.Identity, TransitionEndpoint.Begin),
                    checked(item.Row.Split - (int)Math.Round(l * 44100 / 256000, MidpointRounding.AwayFromZero)));
                if (dirtyFields.Contains(right))
                    plan = plan.WithJunction(_transitionLayout, new(item.Edge.Identity, TransitionEndpoint.End),
                    checked(item.Row.Split + (int)Math.Round(r * 44100 / 256000, MidpointRounding.AwayFromZero)));
                if (phase.IsEnabled && dirtyFields.Contains(phase)) plan = plan.WithPhase(item.Edge.Identity, (float)(p / 100));
                if (coverageDirty || (phase.IsEnabled && dirtyFields.Contains(phase)))
                    plan = TransitionExpressionControl.Source(plan, item.Row, item.Edge.Identity, coverage.IsChecked == true);
                _ = plan.Compile(_transitionLayout);
                if (dirtyFields.Contains(gain)) plan = TransitionGain(plan, item.Edge.Identity, (float)db);
                QueueTransitionPlan(plan); dirtyFields.Clear(); coverageDirty = false; status.Text = TransitionText("Validating");
            }
            catch (Exception error) when (error is NotSupportedException or InvalidOperationException or OverflowException)
            { status.Text = TransitionText("Invalid"); }
        }
        apply.Click += (_, _) => Apply();
        resetExpression.Click += (_, _) =>
        {
            if (_transitionSelected == null || _transitionControls == null) return;
            dirtyFields.Clear(); coverageDirty = false;
            QueueTransitionPlan(TransitionExpressionControl.Reset(_transitionControls, _transitionSelected));
        };
        foreach (var input in new[] { phase, left, right, gain }) input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        choices.SelectionChanged += (_, _) =>
        {
            if (updating || choices.SelectedItem is not ComboBoxItem { Tag: TransitionIdentity identity }) return;
            _transitionSelected = identity; dirtyFields.Clear(); coverageDirty = false; Update(); RequestRefresh();
        };
        reset.Click += (_, _) =>
        {
            if (_transitionSelected == null || _transitionControls == null || _transitionLayout == null) return;
            var plan = _transitionControls;
            foreach (var side in new[] { TransitionEndpoint.Begin, TransitionEndpoint.End })
            { var endpoint = new TransitionEndpointRef(_transitionSelected, side); plan = plan.WithJunction(_transitionLayout, endpoint, _transitionLayout.Junction(endpoint).CurrentFrame); }
            dirtyFields.Clear(); coverageDirty = false;
            QueueTransitionPlan(TransitionGain(TransitionExpressionControl.Reset(plan.WithOnset(_transitionSelected, 0), _transitionSelected), _transitionSelected, 0));
        };
        mute.Click += (_, _) => { if (_transitionSelected != null && _transitionControls != null) QueueTransitionPlan(TransitionGain(_transitionControls, _transitionSelected, -60)); };
        popup.Closed += (_, _) => { if (_transitionPanel == popup) { _transitionPanel = null; _updateTransitionPanel = null; } };
        body.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CancelTransitionDrag(); popup.IsOpen = false; e.Handled = true; } };
        _transitionPanel = popup; _updateTransitionPanel = Update;
        void Place()
        {
            popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            popup.HorizontalOffset = Math.Max(viewport?.Left ?? 0, Math.Min(point.X + 14, (viewport?.Right ?? _view.ActualWidth) - popup.Child.DesiredSize.Width));
            popup.VerticalOffset = Math.Max(viewport?.Top ?? 0, Math.Min(point.Y + 7, (viewport?.Bottom ?? _view.ActualHeight) - popup.Child.DesiredSize.Height));
        }
        popup.Opened += (_, _) => Place();
        Update(); Place(); popup.IsOpen = true;
    }
}
