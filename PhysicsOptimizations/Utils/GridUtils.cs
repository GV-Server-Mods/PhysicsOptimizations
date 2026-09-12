using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace PhysicsOptimizer.Utils
{
    /// <summary>
    /// Utility methods for grid queries, grouping checks, and speed evaluation.
    /// </summary>
    public static class GridUtils
    {
        /// <summary>
        /// Returns true if the grid is a large grid.
        /// </summary>
        public static bool IsLargeGrid(this IMyCubeGrid grid)
        {
            return grid != null && grid.GridSizeEnum == MyCubeSize.Large;
        }

        /// <summary>
        /// Returns true if the grid is a small grid.
        /// </summary>
        public static bool IsSmallGrid(this IMyCubeGrid grid)
        {
            return grid != null && grid.GridSizeEnum == MyCubeSize.Small;
        }

        /// <summary>
        /// Gets the current linear speed of the grid in m/s without heap allocations.
        /// </summary>
        public static float GetSpeed(this IMyCubeGrid grid)
        {
            if (grid?.Physics == null) return 0f;
            return grid.Physics.LinearVelocity.Length();
        }

        /// <summary>
        /// Gets the current squared linear speed of the grid without square root calculation.
        /// </summary>
        public static float GetSpeedSquared(this IMyCubeGrid grid)
        {
            if (grid?.Physics == null) return 0f;
            return grid.Physics.LinearVelocity.LengthSquared();
        }

        /// <summary>
        /// Checks if two grids belong to the same mechanical group (e.g. connected via rotors, pistons, hinges, or suspension wheel attachments).
        /// </summary>
        public static bool AreInSameMechanicalGroup(IMyCubeGrid gridA, IMyCubeGrid gridB)
        {
            if (gridA == null || gridB == null) return false;
            if (ReferenceEquals(gridA, gridB)) return true;
            if (gridA is MyCubeGrid concreteA && gridB is MyCubeGrid concreteB)
            {
                return MyCubeGridGroups.Static?.Mechanical?.HasSameGroup(concreteA, concreteB) ?? false;
            }
            return false;
        }

        /// <summary>
        /// Checks if two grids belong to the same logical group (e.g. connected via connectors or landing gear).
        /// </summary>
        public static bool AreInSameLogicalGroup(IMyCubeGrid gridA, IMyCubeGrid gridB)
        {
            if (gridA == null || gridB == null) return false;
            if (ReferenceEquals(gridA, gridB)) return true;
            if (gridA is MyCubeGrid concreteA && gridB is MyCubeGrid concreteB)
            {
                return MyCubeGridGroups.Static?.Logical?.HasSameGroup(concreteA, concreteB) ?? false;
            }
            return false;
        }

        /// <summary>
        /// Fills results with every grid in the grid's mechanical group (wheels, rotor subgrids),
        /// including the grid itself. Reuses the caller's list to avoid cold-path allocations.
        /// </summary>
        public static void GetMechanicalGroupMembers(MyCubeGrid grid, List<MyCubeGrid> results)
        {
            results.Clear();
            if (grid == null || grid.MarkedForClose || grid.Closed) return;

            var group = MyCubeGridGroups.Static?.Mechanical?.GetGroup(grid);
            if (group?.Nodes == null)
            {
                results.Add(grid);
                return;
            }

            foreach (var node in group.Nodes)
            {
                var g = node?.NodeData;
                if (g != null && !g.MarkedForClose && !g.Closed)
                {
                    results.Add(g);
                }
            }
        }

        /// <summary>
        /// Gets the primary grid in a mechanical group (typically the main chassis or grid with the most blocks).
        /// Falls back to the provided grid if grouping is unavailable.
        /// </summary>
        public static MyCubeGrid GetMainGrid(MyCubeGrid grid)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed) return grid;
            var group = MyCubeGridGroups.Static?.Mechanical?.GetGroup(grid);
            if (group?.Nodes == null || group.Nodes.Count <= 1) return grid;

            MyCubeGrid main = grid;
            int maxBlocks = -1;
            foreach (var node in group.Nodes)
            {
                var g = node?.NodeData;
                if (g != null && !g.MarkedForClose && !g.Closed)
                {
                    int count = g.BlocksCount;
                    if (count > maxBlocks)
                    {
                        maxBlocks = count;
                        main = g;
                    }
                }
            }
            return main;
        }
    }
}

