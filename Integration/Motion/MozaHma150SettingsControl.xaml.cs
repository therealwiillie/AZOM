using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MozaPlugin.Devices.Motion;
using MozaPlugin.Protocol;
using MozaPlugin.Resources;

namespace MozaPlugin.Integration.Motion
{
    /// <summary>
    /// HMA150 controller setup, shown inside SimHub Motion's controller settings: live
    /// status, port → corner assignment with identify and the port self-test, travel
    /// self-check, Max Available Stroke, rig dimensions, the Motion Effects sliders and
    /// lighting, all stored on the controller; the motion source is the one SimHub output setting. Holds the
    /// lane's port open while loaded; refreshes every 500 ms.
    /// </summary>
    public partial class MozaHma150SettingsControl : UserControl
    {
        private static readonly MotionCorner[] Corners =
            { MotionCorner.FrontLeft, MotionCorner.FrontRight, MotionCorner.RearLeft, MotionCorner.RearRight };

        // Lighting: global brightness is zone 3 param 1; zones 4–6 carry their colour in params 3–6.
        private const byte BrightnessZone = 3, BrightnessParam = 1;
        private static readonly byte[] ColorZones = { 4, 5, 6 };
        private const byte ColorParamFirst = 3, ColorParamLast = 6;

        private readonly DispatcherTimer _timer;
        private readonly Dictionary<int, ComboBox> _cornerCombos = new Dictionary<int, ComboBox>();
        private readonly Dictionary<int, Button> _identifyButtons = new Dictionary<int, Button>();
        private readonly Dictionary<byte, Border> _zoneSwatches = new Dictionary<byte, Border>();
        private readonly Dictionary<byte, Button> _zoneButtons = new Dictionary<byte, Button>();
        private readonly List<(Slider Intensity, Slider Smoothness)> _effectSliders = new List<(Slider, Slider)>();
        private Slider? _engineSlider, _tactileSlider;
        private bool _tactileKnown;
        private bool _showingEffects;
        private readonly DispatcherTimer _effectsPush;
        private string _portLayout = string.Empty;
        private bool _setupLoaded;
        private bool _effectsLoaded;
        private bool _readsRequested;
        private MozaMotionController? _held;
        private readonly MozaHma150Output? _output;

        public MozaHma150SettingsControl(MozaHma150Output? output = null)
        {
            _output = output;
            InitializeComponent();
            BuildSourceItems();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, __) => Refresh();
            // Slider moves push live, like Motion Manager's, at most every 250 ms while dragging.
            _effectsPush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _effectsPush.Tick += (_, __) => { _effectsPush.Stop(); PushEffects(); };
            Loaded += (_, __) => { Hold(); BuildZoneRows(); BuildEffectRows(); Refresh(); _timer.Start(); };
            Unloaded += (_, __) =>
            {
                _timer.Stop();
                if (_effectsPush.IsEnabled) { _effectsPush.Stop(); PushEffects(); }
                Unhold();
            };
        }

        private void Hold()
        {
            if (_held != null) return;
            _held = Lane;
            _held?.Acquire();
            _readsRequested = _setupLoaded = _effectsLoaded = false;
        }

        private void Unhold()
        {
            _held?.Release();
            _held = null;
        }

        private static MozaMotionController? Lane => MozaPlugin.Instance?.MotionController;

        private void RequestReads()
        {
            var lane = Lane;
            if (lane == null) return;
            lane.ReadLighting(BrightnessZone, BrightnessParam);
            foreach (var zone in ColorZones) lane.ReadLighting(zone, ColorParamFirst);
        }

        private void Refresh()
        {
            var lane = Lane;
            var s = lane?.Snapshot();
            bool connected = s?.Connected == true;
            // Lighting reads need the port, which may open only after this panel took its hold.
            if (connected && !_readsRequested && s!.Phase != MotionLanePhase.Enrolling)
            {
                _readsRequested = true;
                RequestReads();
            }

            ConnectionValue.Text = connected ? s!.PortName ?? "" : Strings.Motion_Panel_NotConnected;
            StateValue.Text = s == null ? "" : PhaseLabel(s.Phase)
                + (s.FaultReason != null ? " — " + s.FaultReason : "");
            ModeValue.Text = s == null || s.ModeCurrent < 0 ? "" : $"{s.ModeCurrent} (0x{s.Flags:X2})";
            FirmwareValue.Text = s == null || s.FirmwareVersion.Length == 0 ? "" : $"{s.ModelName} {s.FirmwareVersion}";
            ActuatorsValue.Text = s == null ? "" : $"{s.ActuatorsPresent} / {Math.Max(s.SupportedActuators, MozaMotionProtocol.SupportedActuators)}";

            var problems = new List<string>();
            if (lane != null) problems.AddRange(lane.GetConfigurationErrors().Select(MozaHma150Output.FormatIssue));
            if (s != null && s.ActiveErrorCodes.Count > 0) problems.Add(string.Join(", ", s.ActiveErrorCodes));
            ErrorsValue.Text = problems.Count == 0 ? Strings.Motion_Panel_None : string.Join("\n", problems);

            bool idle = connected && (s!.Phase == MotionLanePhase.Ready || s.Phase == MotionLanePhase.Faulted);
            RefreshPorts(s, idle);
            PortSelfTestButton.IsEnabled = idle && _cornerCombos.Count > 0;
            TravelSelfCheckButton.IsEnabled = idle;
            RigApply.IsEnabled = BrightnessApply.IsEnabled = idle;
            // Max Available Stroke is live-tunable, as Motion Manager's home-page slider is.
            MaxStrokeApply.IsEnabled = connected && s!.Phase != MotionLanePhase.Enrolling;
            foreach (var b in _zoneButtons.Values) b.IsEnabled = idle;
            // Effects are live-tunable like Motion Manager's sliders, also while running.
            bool effectsLive = connected && s!.Phase != MotionLanePhase.Enrolling && _effectsLoaded;
            foreach (var (intensity, smoothness) in _effectSliders) intensity.IsEnabled = smoothness.IsEnabled = effectsLive;
            if (_engineSlider != null) _engineSlider.IsEnabled = effectsLive;
            if (_tactileSlider != null) _tactileSlider.IsEnabled = effectsLive;

            if (s != null && connected && !_setupLoaded && s.MaxStroke.HasValue)
            {
                _setupLoaded = true;
                MaxStrokeBox.Text = (s.MaxStroke.Value * 100).ToString("0", CultureInfo.CurrentCulture);
                if (s.RigLengthMm.HasValue) RigLengthBox.Text = s.RigLengthMm.Value.ToString("0", CultureInfo.CurrentCulture);
                if (s.RigWidthMm.HasValue) RigWidthBox.Text = s.RigWidthMm.Value.ToString("0", CultureInfo.CurrentCulture);
            }
            // After the enrol: it decides whether defaults replace an all-zero controller.
            if (s != null && connected && !_effectsLoaded && s.Phase != MotionLanePhase.Enrolling
                && s.RoadDetail.HasValue && s.Effects.All(e => e.HasValue))
            {
                // The active profile's values where it has them, else the lane's defaults, else what the controller holds.
                _effectsLoaded = true;
                var profileEffects = MozaPlugin.Instance?.Settings?.ProfileStore?.CurrentProfile?.MotionEffects;
                var desired = MozaPlugin.ToDesiredEffects(profileEffects);
                int engine = profileEffects?.EngineVibrationStrength ?? -1;
                int tactile = profileEffects?.TactileIntensity ?? -1;
                _showingEffects = true;
                try
                {
                    for (int i = 0; i < _effectSliders.Count; i++)
                    {
                        var held = i < s.Effects.Length ? s.Effects[i]!.Value : s.RoadDetail.Value;
                        var e = desired[i] ?? lane!.SeededEffect(i) ?? held;
                        _effectSliders[i].Intensity.Value = Math.Round(e.Intensity * 100);
                        _effectSliders[i].Smoothness.Value = Math.Round(e.Smoothness * 100);
                    }
                    _engineSlider!.Value = engine >= 0 ? engine : MozaMotionController.DefaultEngineStrengthPercent;
                    _tactileKnown = tactile >= 0 || s.HapticIntensity.HasValue;
                    _tactileSlider!.Value = tactile >= 0 ? tactile
                        : s.HapticIntensity.HasValue ? Math.Round(s.HapticIntensity.Value * 100) : 0;
                }
                finally { _showingEffects = false; }
            }
            RefreshLighting(lane);
        }

        // ------------------------------------------------------------ source

        private void BuildSourceItems()
        {
            SourceCombo.Items.Add(new ComboBoxItem { Content = Strings.Motion_Source_GameTelemetry, Tag = MozaHma150Source.GameTelemetry });
            SourceCombo.Items.Add(new ComboBoxItem { Content = Strings.Motion_Source_SimHubPose, Tag = MozaHma150Source.SimHubPose });
            var settings = _output?.Settings;
            SourceCombo.IsEnabled = settings != null;
            SourceCombo.SelectedIndex = settings?.Source == MozaHma150Source.SimHubPose ? 1 : 0;
            CornerHapticsCheck.IsChecked = settings?.IncludeCornerHaptics ?? true;
            PassThroughCheck.IsChecked = settings?.PassThroughEffects ?? true;
            UpdateCornerHapticsEnabled();
            SourceCombo.SelectionChanged += (_, __) =>
            {
                var s = _output?.Settings;
                if (s != null && SourceCombo.SelectedItem is ComboBoxItem item && item.Tag is MozaHma150Source source)
                    s.Source = source;
                UpdateCornerHapticsEnabled();
            };
            CornerHapticsCheck.Click += (_, __) =>
            {
                var s = _output?.Settings;
                if (s != null) s.IncludeCornerHaptics = CornerHapticsCheck.IsChecked == true;
            };
            PassThroughCheck.Click += (_, __) =>
            {
                var s = _output?.Settings;
                if (s != null) s.PassThroughEffects = PassThroughCheck.IsChecked == true;
            };
        }

        /// <summary>The haptics and pass-through choices only apply to the pose source.</summary>
        private void UpdateCornerHapticsEnabled()
            => CornerHapticsCheck.IsEnabled = PassThroughCheck.IsEnabled
                = _output?.Settings?.Source == MozaHma150Source.SimHubPose;

        // ------------------------------------------------------------ motion effects

        private static string[] EffectLabels() => new[]
        {
            Strings.Motion_Effect_Pitch, Strings.Motion_Effect_Roll, Strings.Motion_Effect_Heave,
            Strings.Motion_Effect_AccelPitch, Strings.Motion_Effect_AccelRoll, Strings.Motion_Effect_RoadDetail,
        };

        /// <summary>One row per <see cref="MozaMotionProtocol.EffectSlots"/> entry, then road detail.</summary>
        private void BuildEffectRows()
        {
            if (EffectRows.Children.Count > 0) return;
            foreach (var label in EffectLabels())
            {
                var row = NewEffectRow(label);
                var intensity = AddSlider(row);
                var smoothness = AddSlider(row);
                EffectRows.Children.Add(row);
                _effectSliders.Add((intensity, smoothness));
            }
            _engineSlider = AddSingleRow(Strings.Motion_Effect_EngineVibration);
            _tactileSlider = AddSingleRow(Strings.Motion_Effect_TactileFeedback);
            _tactileSlider.ValueChanged += (_, __) => { if (!_showingEffects) _tactileKnown = true; };
        }

        private static StackPanel NewEffectRow(string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 260, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        /// <summary>A row with only the intensity column.</summary>
        private Slider AddSingleRow(string label)
        {
            var row = NewEffectRow(label);
            var slider = AddSlider(row);
            EffectRows.Children.Add(row);
            return slider;
        }

        /// <summary>A 0–100 % slider in whole steps with its value beside it, as in Motion Manager.</summary>
        private Slider AddSlider(Panel row)
        {
            var slider = new Slider
            {
                Minimum = 0, Maximum = 100, Width = 200, IsEnabled = false,
                SmallChange = 1, LargeChange = 5, TickFrequency = 1,
                IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new TextBlock { Text = "0", Width = 40, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, __) =>
            {
                value.Text = slider.Value.ToString("0", CultureInfo.CurrentCulture);
                if (_showingEffects || !_effectsLoaded) return;
                if (!_effectsPush.IsEnabled) _effectsPush.Start();
            };
            row.Children.Add(slider);
            row.Children.Add(value);
            return slider;
        }

        /// <summary>Saves every slider into the active profile, which pushes it to the controller.</summary>
        private void PushEffects()
        {
            if (!_effectsLoaded) return;
            var values = new Dictionary<int, (int Intensity, int Smoothness)>();
            for (int i = 0; i < _effectSliders.Count; i++)
                values[i] = ((int)Math.Round(_effectSliders[i].Intensity.Value), (int)Math.Round(_effectSliders[i].Smoothness.Value));
            int? engine = _engineSlider == null ? (int?)null : (int)Math.Round(_engineSlider.Value);
            int? tactile = _tactileSlider == null || !_tactileKnown ? (int?)null : (int)Math.Round(_tactileSlider.Value);
            MozaPlugin.Instance?.SetMotionEffects(values, engine, tactile);
        }

        private void RefreshPorts(MotionControllerSnapshot? s, bool idle)
        {
            var populated = s?.Ports.Where(p => p.Present == true).ToList() ?? new List<MotionPortSnapshot>();
            string layout = string.Join(",", populated.Select(p => p.Port));
            if (layout != _portLayout)
            {
                _portLayout = layout;
                PortRows.Children.Clear();
                _cornerCombos.Clear();
                _identifyButtons.Clear();
                foreach (var p in populated)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    row.Children.Add(new TextBlock
                    {
                        Text = string.Format(Strings.Motion_Panel_PortFmt, p.Port),
                        Width = 260,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var combo = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
                    foreach (var c in Corners)
                        combo.Items.Add(new ComboBoxItem { Content = MozaHma150Output.CornerLabel(c), Tag = c });
                    combo.SelectedIndex = Array.IndexOf(Corners, p.Role ?? MotionCorner.None);
                    row.Children.Add(combo);
                    int port = p.Port;
                    var identify = new Button { Content = Strings.Motion_Panel_Identify };
                    identify.Click += (_, __) => Lane?.IdentifyPort(port);
                    row.Children.Add(identify);
                    PortRows.Children.Add(row);
                    _cornerCombos[port] = combo;
                    _identifyButtons[port] = identify;
                }
            }
            foreach (var kv in _cornerCombos) kv.Value.IsEnabled = idle;
            foreach (var kv in _identifyButtons) kv.Value.IsEnabled = idle;
        }

        private void BuildZoneRows()
        {
            if (ZoneRows.Children.Count > 0) return;
            foreach (var zone in ColorZones)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = string.Format(Strings.Motion_Panel_ZoneFmt, zone),
                    Width = 260,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var swatch = new Border
                {
                    Width = 40, Height = 20, Margin = new Thickness(0, 0, 8, 0),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                    Background = Brushes.Transparent,
                };
                row.Children.Add(swatch);
                byte z = zone;
                var change = new Button { Content = Strings.Motion_Panel_ChangeColor };
                change.Click += (_, __) => PickZoneColor(z);
                row.Children.Add(change);
                ZoneRows.Children.Add(row);
                _zoneSwatches[zone] = swatch;
                _zoneButtons[zone] = change;
            }
        }

        private void RefreshLighting(MozaMotionController? lane)
        {
            if (lane == null) return;
            foreach (var kv in _zoneSwatches)
            {
                var v = lane.GetLighting(kv.Key, ColorParamFirst);
                if (v != null && v.Length >= 3)
                    kv.Value.Background = Devices.Ui.WheelUiHelpers.GetCachedBrush(v[0], v[1], v[2]);
            }
            if (!BrightnessBox.IsKeyboardFocusWithin)
            {
                var b = lane.GetLighting(BrightnessZone, BrightnessParam);
                if (b != null && b.Length >= 1)
                {
                    string text = b[0].ToString(CultureInfo.CurrentCulture);
                    if (BrightnessBox.Text != text) BrightnessBox.Text = text;
                }
            }
        }

        private void PickZoneColor(byte zone)
        {
            var lane = Lane;
            if (lane == null) return;
            var current = lane.GetLighting(zone, ColorParamFirst) ?? new byte[] { 0, 0, 0 };
            var dialog = new UI.ColorPickerDialog(current[0], current[1], current[2]) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;
            var rgb = new[] { dialog.SelectedR, dialog.SelectedG, dialog.SelectedB };
            for (byte p = ColorParamFirst; p <= ColorParamLast; p++)
                lane.WriteLighting(zone, p, rgb);
        }

        private void PortSelfTest_Click(object sender, RoutedEventArgs e)
        {
            var roles = new Dictionary<int, MotionCorner>();
            foreach (var kv in _cornerCombos)
            {
                if (kv.Value.SelectedItem is ComboBoxItem item && item.Tag is MotionCorner corner)
                    roles[kv.Key] = corner;
                else
                    return;
            }
            if (roles.Values.Distinct().Count() != roles.Count) return;
            Lane?.RunPortSelfTest(roles);
        }

        private void TravelSelfCheck_Click(object sender, RoutedEventArgs e) => Lane?.RunTravelSelfCheck();

        private void MaxStrokeApply_Click(object sender, RoutedEventArgs e)
        {
            if (TryParse(MaxStrokeBox.Text, out double pct) && pct > 0 && pct <= 100)
                Lane?.WriteMaxStroke((float)(pct / 100.0));
        }

        private void RigApply_Click(object sender, RoutedEventArgs e)
        {
            if (TryParse(RigLengthBox.Text, out double length) && TryParse(RigWidthBox.Text, out double width)
                && length > 0 && width > 0)
                Lane?.WriteRigDimensions((float)length, (float)width);
        }

        private void BrightnessApply_Click(object sender, RoutedEventArgs e)
        {
            if (TryParse(BrightnessBox.Text, out double pct) && pct >= 0 && pct <= 100)
                Lane?.WriteLighting(BrightnessZone, BrightnessParam, new[] { (byte)Math.Round(pct) });
        }

        private static bool TryParse(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
               || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        internal static string PhaseLabel(MotionLanePhase phase) => phase switch
        {
            MotionLanePhase.Disconnected => Strings.Motion_Phase_Disconnected,
            MotionLanePhase.Enrolling => Strings.Motion_Phase_Enrolling,
            MotionLanePhase.Ready => Strings.Motion_Phase_Ready,
            MotionLanePhase.SelfChecking => Strings.Motion_Phase_SelfChecking,
            MotionLanePhase.EnteringGame => Strings.Motion_Phase_EnteringGame,
            MotionLanePhase.Game => Strings.Motion_Phase_Game,
            MotionLanePhase.Disengaging => Strings.Motion_Phase_Disengaging,
            MotionLanePhase.PortSelfTest => Strings.Motion_Phase_PortSelfTest,
            MotionLanePhase.Faulted => Strings.Motion_Phase_Faulted,
            _ => phase.ToString(),
        };
    }
}
