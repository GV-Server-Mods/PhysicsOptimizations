using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PhysicsOptimizer.Views
{
    public partial class PhysicsOptimizerControl : UserControl
    {
        private PhysicsOptimizerPlugin Plugin { get; }
        private readonly DispatcherTimer _telemetryTimer;

        public PhysicsOptimizerControl()
        {
            InitializeComponent();

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _telemetryTimer.Tick += (s, e) =>
            {
                Plugin?.Telemetry?.NotifyAllPropertiesChanged();
                Plugin?.DefenseStats?.NotifyAll();
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
    }
}

