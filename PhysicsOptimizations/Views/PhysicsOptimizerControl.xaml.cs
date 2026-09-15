using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PhysicsOptimizer.Views
{
    public partial class PhysicsOptimizerControl : UserControl
    {
        private PhysicsOptimizerPlugin Plugin { get; }
        private readonly DispatcherTimer _telemetryTimer;

        // 60-Second Rolling Graph Circular Buffers
        private const int HistorySamples = 60;
        private readonly float[] _simSpeedHistory = new float[HistorySamples];
        private readonly double[] _contactRateHistory = new double[HistorySamples];
        private readonly int[] _clangRateHistory = new int[HistorySamples];
        private int _historyCount;
        private int _historyHead;

        public PhysicsOptimizerControl()
        {
            InitializeComponent();

            int intervalMs = Plugin?.Config?.UiRefreshIntervalMs ?? 500;
            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(intervalMs)
            };
            _telemetryTimer.Tick += (s, e) =>
            {
                if (Plugin?.Config != null)
                {
                    int targetMs = Plugin.Config.UiRefreshIntervalMs;
                    if (targetMs > 0 && Math.Abs(_telemetryTimer.Interval.TotalMilliseconds - targetMs) > 10)
                    {
                        _telemetryTimer.Interval = TimeSpan.FromMilliseconds(targetMs);
                    }
                }
                Plugin?.Telemetry?.NotifyAllPropertiesChanged();
                Plugin?.DefenseStats?.NotifyAll();

                float speed = Plugin?.Telemetry?.ServerSimulationSpeed ?? 1.0f;
                double contactRate = Plugin?.DefenseStats?.ContactCallbacksPerSecond ?? 0.0;
                int topRate = Plugin?.DefenseStats?.TopOffender?.CurrentRate ?? 0;
                AddHistorySample(speed, contactRate, topRate);
                RedrawGraph();
            };

            Loaded += (s, e) => _telemetryTimer.Start();
            Unloaded += (s, e) => _telemetryTimer.Stop();
        }

        public PhysicsOptimizerControl(PhysicsOptimizerPlugin plugin) : this()
        {
            Plugin = plugin;
            DataContext = plugin;
        }

        private void SaveButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Plugin?.SaveConfig();
                MessageBox.Show("Physics Optimizer configuration saved successfully!", "Physics Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetCountersButton_OnClick(object sender, RoutedEventArgs e)
        {
            Plugin?.Telemetry?.ResetLiveCounters();
            Plugin?.DefenseStats?.Reset();
        }

        /// <summary>
        /// Runs physics-touching work on the game thread and reports the result on the UI thread.
        /// MyEntities/Havok calls from the WPF thread race the simulation loop.
        /// </summary>
        private void RunOnGameThread(string title, Func<string> gameThreadWork)
        {
            if (Sandbox.MySandboxGame.Static == null)
            {
                MessageBox.Show("No game session is loaded.", title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dispatcher = Dispatcher;
            Sandbox.MySandboxGame.Static.Invoke(() =>
            {
                string message;
                MessageBoxImage icon;
                try
                {
                    message = gameThreadWork();
                    icon = MessageBoxImage.Information;
                }
                catch (Exception ex)
                {
                    message = $"Error: {ex.Message}";
                    icon = MessageBoxImage.Error;
                }

                var msg = message;
                var img = icon;
                dispatcher.BeginInvoke(new Action(() =>
                    MessageBox.Show(msg, title, MessageBoxButton.OK, img)));
            }, "PhysicsOptimizer." + title);
        }

        private void SleepAllButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (Plugin?.RigidBodySleep == null)
            {
                MessageBox.Show("Rigid Body Sleep is not initialized.", "Sleep All Grids", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!Plugin.RigidBodySleep.IsEnabled)
            {
                MessageBox.Show(
                    "Rigid Body Sleep is currently disabled.\n\nEnable it from the toggle panel (Enable Rigid Body Sleep) or console (!phys toggle sleep), then click Sleep All Grids again.",
                    "Sleep All Grids", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            RunOnGameThread("Sleep All Grids", () =>
            {
                int slept = Plugin.RigidBodySleep.ForceSleepAllIdleGrids();
                return $"Successfully forced {slept} idle dynamic grids into Havok SLEEP mode.";
            });
        }

        private void WakeAllButton_OnClick(object sender, RoutedEventArgs e)
        {
            RunOnGameThread("Wake All Grids", () =>
            {
                int count = 0;
                var entities = Sandbox.Game.Entities.MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is Sandbox.Game.Entities.MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    {
                        if (!grid.Physics.RigidBody.IsActive)
                        {
                            Plugin?.RigidBodySleep?.WakeGrid(grid, "UI Wake All button");
                            Plugin?.WheelOptimizer?.WakeRover(grid.EntityId, "UI Wake All button");
                            count++;
                        }
                    }
                }
                return $"Successfully woke {count} sleeping dynamic grids/rovers.";
            });
        }

        private void MergeOreButton_OnClick(object sender, RoutedEventArgs e)
        {
            RunOnGameThread("Ore Merge", () =>
            {
                int eliminated = Plugin?.OreMerge?.MergeProximityFloatingObjects() ?? 0;
                return $"Proximity merge completed. Eliminated {eliminated} redundant floating entities.";
            });
        }

        private void CopyDiagnosticsButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Plugin?.Telemetry != null)
                {
                    string summary = Plugin.Telemetry.GetDiagnosticSummary();
                    if (Plugin?.DefenseStats != null)
                    {
                        summary += "\n\n=== COLLISION DEFENSE TELEMETRY ===\n" + Plugin.DefenseStats.GetDiagnosticSummary();
                    }
                    Clipboard.SetText(summary);
                    MessageBox.Show("Diagnostic summary copied to clipboard.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy diagnostics: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DampenAllClangersButton_OnClick(object sender, RoutedEventArgs e)
        {
            RunOnGameThread("Dampen Clangers", () =>
            {
                int count = Modules.GridDefender.DampenAllClangers();
                return count > 0
                    ? $"Successfully dampened velocities and arrested physics on {count} active clanger constructs."
                    : "No active clangers detected above the threshold.";
            });
        }

        private void CopyClangersGpsButton_OnClick(object sender, RoutedEventArgs e)
        {
            var clangers = Modules.GridDefender.GetActiveClangers(minRate: 1, maxResults: 5);
            if (clangers == null || clangers.Count == 0)
            {
                MessageBox.Show("No active clangers detected.", "Clangers GPS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var c in clangers)
            {
                if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(c.Id, out VRage.Game.Entity.MyEntity ent) && ent != null)
                {
                    var pos = ent.PositionComp.GetPosition();
                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "GPS:CLANG_{0}_{1}s:{2:F2}:{3:F2}:{4:F2}:#FF5252:",
                        c.Name.Replace(":", "_"), c.Rate, pos.X, pos.Y, pos.Z));
                }
            }

            if (sb.Length > 0)
            {
                Clipboard.SetText(sb.ToString());
                MessageBox.Show("Clanger GPS marker(s) copied to clipboard!\n\n" + sb.ToString(), "Clangers GPS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Could not resolve world positions for active clangers.", "Clangers GPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #region 60-Second Real-Time Graph

        private void AddHistorySample(float simSpeed, double contactRate, int clangRate)
        {
            _simSpeedHistory[_historyHead] = simSpeed;
            _contactRateHistory[_historyHead] = contactRate;
            _clangRateHistory[_historyHead] = clangRate;
            _historyHead = (_historyHead + 1) % HistorySamples;
            if (_historyCount < HistorySamples) _historyCount++;
        }

        private void TelemetryGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawGraph();
        }

        private void RedrawGraph()
        {
            if (TelemetryGraphCanvas == null || SimSpeedPolyline == null || ContactLoadPolyline == null || ClangSpikesPolyline == null)
                return;

            double width = TelemetryGraphCanvas.ActualWidth;
            double height = TelemetryGraphCanvas.ActualHeight;
            if (width <= 20 || height <= 20 || _historyCount < 2)
                return;

            var simPoints = new PointCollection(_historyCount);
            var contactPoints = new PointCollection(_historyCount);
            var clangPoints = new PointCollection(_historyCount);

            double stepX = width / (HistorySamples - 1);
            int startIndex = (_historyHead - _historyCount + HistorySamples) % HistorySamples;

            // Find peak contact rate for dynamic scaling
            double maxContact = 100.0;
            for (int i = 0; i < _historyCount; i++)
            {
                int idx = (startIndex + i) % HistorySamples;
                if (_contactRateHistory[idx] > maxContact) maxContact = _contactRateHistory[idx];
            }

            for (int i = 0; i < _historyCount; i++)
            {
                int idx = (startIndex + i) % HistorySamples;
                double x = (HistorySamples - _historyCount + i) * stepX;

                // Sim Speed: 1.02 TPS at Y=10, 0.0 TPS at Y=height-10
                float speed = Math.Max(0f, Math.Min(1.02f, _simSpeedHistory[idx]));
                double simY = (height - 20) * (1.0 - (speed / 1.02)) + 10;
                simPoints.Add(new Point(x, simY));

                // Contact Load: scaled to maxContact (spans 0% to 75% height)
                double contactFrac = Math.Max(0.0, Math.Min(1.0, _contactRateHistory[idx] / maxContact));
                double contactY = (height - 10) - (contactFrac * (height - 25));
                contactPoints.Add(new Point(x, contactY));

                // Clang Spikes: 0 rate at bottom, 50/s reaches near top
                double clangFrac = Math.Min(1.0, _clangRateHistory[idx] / 50.0);
                double clangY = (height - 10) - (clangFrac * (height - 20));
                clangPoints.Add(new Point(x, clangY));
            }

            SimSpeedPolyline.Points = simPoints;
            ContactLoadPolyline.Points = contactPoints;
            ClangSpikesPolyline.Points = clangPoints;
        }

        #endregion

        #region DEFCON Banner Actions

        private void BannerFreezeTopOffender_Click(object sender, RoutedEventArgs e)
        {
            var top = Plugin?.DefenseStats?.TopOffender;
            if (top == null) return;
            FreezeOffender(top);
        }

        private void BannerNudgeTopOffender_Click(object sender, RoutedEventArgs e)
        {
            var top = Plugin?.DefenseStats?.TopOffender;
            if (top == null) return;
            NudgeOffender(top);
        }

        private void BannerAnchorTopOffender_Click(object sender, RoutedEventArgs e)
        {
            var top = Plugin?.DefenseStats?.TopOffender;
            if (top == null) return;
            AnchorOffender(top);
        }

        private void BannerCopyGpsTopOffender_Click(object sender, RoutedEventArgs e)
        {
            var top = Plugin?.DefenseStats?.TopOffender;
            if (top == null) return;
            CopyOffenderGps(top);
        }

        #endregion

        #region Session Wall of Shame Actions

        private void OffenderGps_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Modules.GridDefender.ClangOffender offender)
            {
                CopyOffenderGps(offender);
            }
        }

        private void OffenderFreeze_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Modules.GridDefender.ClangOffender offender)
            {
                FreezeOffender(offender);
            }
        }

        private void OffenderNudge_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Modules.GridDefender.ClangOffender offender)
            {
                NudgeOffender(offender);
            }
        }

        private void OffenderAnchor_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Modules.GridDefender.ClangOffender offender)
            {
                AnchorOffender(offender);
            }
        }

        private void OffenderDepower_OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Modules.GridDefender.ClangOffender offender)
            {
                DepowerOffender(offender);
            }
        }

        private void ClearWallOfShame_OnClick(object sender, RoutedEventArgs e)
        {
            Modules.GridDefender.ClearClangRecords();
            Plugin?.DefenseStats?.NotifyAll();
        }

        private void CopyDiscordReport_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Plugin?.DefenseStats != null)
                {
                    string report = Plugin.DefenseStats.GenerateDiscordIncidentReport();
                    Clipboard.SetText(report);
                    MessageBox.Show("Discord Incident Report copied to clipboard!", "Discord Report", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy Discord report: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Offender Rescue Helpers

        private void FreezeOffender(Modules.GridDefender.ClangOffender offender)
        {
            RunOnGameThread("Freeze Construct", () =>
            {
                bool success = Modules.GridDefender.DampenConstruct(offender.ConstructId);
                return success
                    ? $"Successfully arrested physics and dampened velocities on '{offender.DisplayName}'."
                    : $"Could not find active construct '{offender.DisplayName}' (ID: {offender.ConstructId}). It may have been closed or despawned.";
            });
        }

        private void NudgeOffender(Modules.GridDefender.ClangOffender offender)
        {
            RunOnGameThread("Nudge Construct", () =>
            {
                bool success = Modules.GridDefender.NudgeConstruct(offender.ConstructId, 1.0f);
                return success
                    ? $"Successfully nudged '{offender.DisplayName}' +1.0m upward along local gravity."
                    : $"Could not nudge '{offender.DisplayName}' (ID: {offender.ConstructId}). It may not be trapped or is no longer in world.";
            });
        }

        private void AnchorOffender(Modules.GridDefender.ClangOffender offender)
        {
            var result = MessageBox.Show(
                $"Are you sure you want to convert '{offender.DisplayName}' to a STATIC STATION?\n\nThis will permanently freeze the construct in place.",
                "Confirm Anchor",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            RunOnGameThread("Anchor Construct", () =>
            {
                bool success = Modules.GridDefender.AnchorConstruct(offender.ConstructId);
                return success
                    ? $"Successfully converted base grid of '{offender.DisplayName}' to a Static Station."
                    : $"Could not convert '{offender.DisplayName}' (ID: {offender.ConstructId}) to station.";
            });
        }

        private void DepowerOffender(Modules.GridDefender.ClangOffender offender)
        {
            RunOnGameThread("Depower Construct", () =>
            {
                bool success = Modules.GridDefender.DepowerConstruct(offender.ConstructId);
                return success
                    ? $"Successfully shut down all power producers across '{offender.DisplayName}'."
                    : $"Could not find active power systems on '{offender.DisplayName}' (ID: {offender.ConstructId}).";
            });
        }

        private void CopyOffenderGps(Modules.GridDefender.ClangOffender offender)
        {
            try
            {
                var pos = offender.LastPosition;
                if (Sandbox.Game.Entities.MyEntities.TryGetEntityById(offender.ConstructId, out VRage.Game.Entity.MyEntity ent) && ent != null)
                {
                    pos = ent.PositionComp.GetPosition();
                }

                string safeName = (offender.DisplayName ?? "Clanger").Replace(":", "_");
                string gps = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "GPS:CLANG_{0}:{1:F2}:{2:F2}:{3:F2}:#FF5252:",
                    safeName, pos.X, pos.Y, pos.Z);

                Clipboard.SetText(gps);
                MessageBox.Show($"GPS copied to clipboard:\n\n{gps}", "Clanger GPS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy GPS: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}

