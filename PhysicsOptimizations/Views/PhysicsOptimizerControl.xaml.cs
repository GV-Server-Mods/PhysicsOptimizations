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

        private void SleepAllButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int slept = Plugin?.Sleep?.ForceSleepAllIdleGrids() ?? 0;
                MessageBox.Show($"Successfully forced {slept} idle dynamic grids into Havok SLEEP mode.", "Sleep All Grids", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sleeping grids: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void WakeAllButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int count = 0;
                var entities = Sandbox.Game.Entities.MyEntities.GetEntities();
                foreach (var entity in entities)
                {
                    if (entity is Sandbox.Game.Entities.MyCubeGrid grid && !grid.IsStatic && !grid.MarkedForClose && grid.Physics?.RigidBody != null)
                    {
                        if (!grid.Physics.RigidBody.IsActive)
                        {
                            Plugin?.Sleep?.WakeGrid(grid, "UI Wake All button");
                            Plugin?.WheelOptimizer?.WakeRover(grid.EntityId, "UI Wake All button");
                            count++;
                        }
                    }
                }
                MessageBox.Show($"Successfully woke {count} sleeping dynamic grids/rovers.", "Wake All Grids", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error waking grids: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MergeOreButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int eliminated = Plugin?.OreMerge?.MergeProximityFloatingObjects() ?? 0;
                MessageBox.Show($"Proximity merge completed. Eliminated {eliminated} redundant floating entities.", "Ore Merge", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running ore merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

