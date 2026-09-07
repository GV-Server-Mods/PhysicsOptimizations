using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using Torch.Managers.PatchManager;

namespace PhysicsOptimizer.Utils
{
    /// <summary>
    /// One-shot startup audit of every game method this plugin patches.
    /// Checks Torch PatchManager rewrite patterns for detours from foreign assemblies and, when a
    /// raw-Harmony plugin is loaded (e.g. se-performance-improvements), Harmony patch ownership too.
    /// </summary>
    public static class PatchConflictAudit
    {
        private const string LogSource = "PatchAudit";

        private static readonly List<MethodBase> Targets = new();
        private static PatchManager _patchManager;
        private static bool _ran;

        /// <summary>Registers a game method this plugin patches, for the startup conflict audit.</summary>
        public static void RegisterTarget(MethodBase method)
        {
            if (method != null && !Targets.Contains(method))
                Targets.Add(method);
        }

        /// <summary>Captures the Torch PatchManager so rewrite patterns can be inspected on the first tick.</summary>
        public static void CapturePatchManager(PatchManager patchManager)
        {
            _patchManager = patchManager;
        }

        /// <summary>
        /// Runs once on the first session tick, when every plugin has registered its patches.
        /// </summary>
        public static void RunOnce()
        {
            if (_ran) return;
            _ran = true;
            try
            {
                AuditTorchPatterns();
                AuditRawHarmony();
                LogKnownInteropPlugins();
            }
            catch (Exception ex)
            {
                Log.Warn(ex, LogSource, "Audit failed; patch ownership unverified this boot.");
            }
        }

        private static void AuditTorchPatterns()
        {
            if (_patchManager == null)
            {
                Log.Warn(LogSource, "PatchManager unavailable; Torch detour conflict check skipped.");
                return;
            }

            var foreignDetours = new List<string>();
            foreach (var target in Targets)
            {
                var pattern = _patchManager.GetPattern(target);
                var detours = pattern.Prefixes
                    .Concat(pattern.Transpilers)
                    .Concat(pattern.PostTranspilers)
                    .Concat(pattern.Suffixes)
                    .ToList();

                if (detours.Count == 0)
                {
                    Log.Info(LogSource, $"{Describe(target)}: no Torch detours registered (our patch registration may have failed).");
                    continue;
                }

                foreach (var detour in detours.Where(d => d.DeclaringType?.Assembly != typeof(PatchConflictAudit).Assembly))
                {
                    var owner = detour.DeclaringType?.Assembly.GetName().Name ?? "<unknown>";
                    foreignDetours.Add(Describe(target));
                    Log.Warn(LogSource, $"{Describe(target)}: FOREIGN TORCH DETOUR {Describe(detour)} from '{owner}'.");
                }
            }

            Log.Info(LogSource, foreignDetours.Count == 0
                ? $"Torch detour check clean: {Targets.Count} targets, all detours from our assembly only."
                : $"{foreignDetours.Count} foreign Torch detours detected on our patch targets.");
        }

        private static void AuditRawHarmony()
        {
            var harmonyType = FindHarmonyType();
            if (harmonyType == null)
            {
                Log.Info(LogSource, "No raw-Harmony runtime loaded; external Harmony conflict check not applicable.");
                return;
            }

            const BindingFlags staticPublic = BindingFlags.Public | BindingFlags.Static;
            var getPatchInfo = harmonyType.GetMethod("GetPatchInfo", staticPublic, null, [typeof(MethodBase)], null);
            var getAllPatched = harmonyType.GetMethods(staticPublic)
                .FirstOrDefault(m => m.Name == "GetAllPatchedMethods" && m.GetParameters().Length == 0);
            if (getPatchInfo == null || getAllPatched == null)
            {
                Log.Info(LogSource, "Harmony API surface not recognized; skipping external Harmony audit.");
                return;
            }

            var ownersByAssembly = new Dictionary<string, int>();
            var conflictingTargets = new List<string>();

            foreach (var target in Targets)
            {
                var info = getPatchInfo.Invoke(null, [target]);
                if (info == null) continue;

                var ownerList = GetOwners(info).ToArray();
                if (ownerList.Length == 0) continue;

                var detail = $"owners=[{string.Join(", ", ownerList)}] prefixes={GetCount(info, "Prefixes")} postfixes={GetCount(info, "Postfixes")} transpilers={GetCount(info, "Transpilers")}";
                conflictingTargets.Add(Describe(target));
                Log.Warn(LogSource, $"{Describe(target)}: EXTERNAL HARMONY PATCHER DETECTED ({detail}).");
            }

            foreach (var patched in (IEnumerable<MethodBase>)getAllPatched.Invoke(null, null))
            {
                var info = getPatchInfo.Invoke(null, [patched]);
                if (info == null) continue;

                foreach (var owner in GetOwners(info))
                {
                    var assembly = owner;
                    ownersByAssembly[assembly] = ownersByAssembly.TryGetValue(assembly, out var count) ? count + 1 : 1;
                }
            }

            var inventory = ownersByAssembly.Count == 0
                ? "none"
                : string.Join(", ", ownersByAssembly.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value} methods"));
            Log.Info(LogSource, $"Raw-Harmony patchers loaded: {inventory}.");

            if (conflictingTargets.Count > 0)
            {
                Log.Warn(LogSource, $"{conflictingTargets.Count}/{Targets.Count} of our targets also carry raw-Harmony patches. Two independent IL rewriters on one method is undefined territory - review before trusting combined behavior.");
            }
        }

        /// <summary>
        /// Boot log marker for interop plugins whose compatibility was manually audited (README Design Notes).
        /// </summary>
        private static void LogKnownInteropPlugins()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                if (string.Equals(assembly.GetName().Name, "Concealment", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info(LogSource, "Concealment detected - interop audited compatible (patch surfaces disjoint; concealment operates on the update-registration layer only). Re-audit if either plugin updates.");
                    return;
                }
            }
        }

        private static Type FindHarmonyType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;

                if (TryFindHarmonyType(assembly, out var harmonyType)) return harmonyType;
            }

            return null;
        }

        private static bool TryFindHarmonyType(Assembly assembly, out Type harmonyType)
        {
            try
            {
                harmonyType = assembly.GetType("HarmonyLib.Harmony", false);
            }
            catch (ReflectionTypeLoadException)
            {
                harmonyType = null;
            }

            return harmonyType != null;
        }

        private static IEnumerable<string> GetOwners(object patchInfo)
        {
            var owners = patchInfo.GetType().GetProperty("Owners")?.GetValue(patchInfo) as IEnumerable<string>;
            return owners ?? [];
        }

        private static int GetCount(object patchInfo, string propertyName)
        {
            return (patchInfo.GetType().GetProperty(propertyName)?.GetValue(patchInfo) as System.Collections.ICollection)?.Count ?? 0;
        }

        private static string Describe(MethodBase method)
        {
            return $"{method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "<unknown>"}.{method.Name}";
        }
    }
}
