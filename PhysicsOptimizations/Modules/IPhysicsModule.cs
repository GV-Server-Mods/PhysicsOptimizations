using GVK.PhysicsOptimizations.Config;

namespace GVK.PhysicsOptimizations.Modules
{
    public interface IPhysicsModule
    {
        string Name { get; }
        bool IsEnabled { get; }

        void Init(PhysicsOptimizerPlugin plugin);
        void Update(ulong frameCounter);
        void Dispose();
        void UpdateConfig(PhysicsOptimizerConfig config);
    }
}

