using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GVK.PhysicsOptimizations.Views
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
                MessageBox.Show("GVK Physics Optimizer configuration saved successfully!", "Physics Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SleepAllButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int slept = Plugin?.SleepManager?.ForceSleepAllIdleGrids() ?? 0;
                MessageBox.Show($"Successfully forced {slept} idle dynamic grids into Havok SLEEP mode.", "Sleep All Grids", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sleeping grids: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MergeOreButton_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int eliminated = Plugin?.OreOptimizer?.MergeProximityFloatingObjects() ?? 0;
                MessageBox.Show($"Proximity merge completed. Eliminated {eliminated} redundant floating entities.", "Ore Merge", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running ore merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetCountersButton_OnClick(object sender, RoutedEventArgs e)
        {
            Plugin?.Telemetry?.ResetLiveCounters();
        }
    }
}

