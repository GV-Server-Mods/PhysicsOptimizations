using PhysicsOptimizer.Config;
using VRage.Game.Entity;

namespace PhysicsOptimizer.Modules
{
    public interface IPhysicsModule
    {
        string Name { get; }
        bool IsEnabled { get; }

        void Init(PhysicsOptimizerPlugin plugin);
        void Update(ulong frameCounter);
        void Dispose();
        void UpdateConfig(PhysicsOptimizerConfig config);
        void OnEntityAdded(MyEntity entity);
        void OnEntityRemoved(MyEntity entity);
    }
}
