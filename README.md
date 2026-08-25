# ⚡ GVK Physics Optimizer

**High-Performance Havok Physics Optimization & Suspension Stabilizer for Space Engineers Torch Servers**

* **Plugin Type**: Torch Dedicated Server Plugin (.NET Framework 4.8)  
* **Target Server**: GV - Deserts of Kharak (GVK)  
* **Package**: `GVK_PhysicsOptimizations.zip`  
* **Version**: 1.0.0  

---

## 1. Project Intent & Philosophy

In Space Engineers, Havok physics calculations consume up to 60–70% of server CPU time on rover-heavy and combat worlds. Server sim-speed craters due to non-critical physics workloads:
1. Active 60Hz physics calculations on motionless and drifting ships.
2. Continuous ground-raycasts, air-shock checks, and suspension math on parked rovers with handbrakes engaged.
3. Hundreds of individual floating ore boulders calculating ground collisions simultaneously after mining or combat.
4. Continuous constraint stress and micro-vibration calculations across resting mechanical subgrids (rotors/hinges/pistons).
5. Continuous collision detection (CCD/TOI) overhead on slow-moving grids.

**`GVK.PhysicsOptimizer`** eliminates these bottlenecks through non-destructive, intelligent deactivation and throttling, maintaining **60 TPS server sim-speed** while leaving vehicle handling, drifting, and combat 100% intact:
* 🏎️ **Consolidated Wheel & Suspension Physics**: Eliminates wheel well armor compound queries and sleeps 60Hz raycasts on parked rovers.
* 💤 **Aggressive Rigid Body Sleeping**: Actively forces motionless unpiloted dynamic grids into Havok sleep mode (`rigidBody.Deactivate()`).
* ⛏️ **Proximity Ore & Item Merging**: Automatically merges floating ore boulders and dropped components within proximity into single larger stacks, slashing active rigid body counts by up to 80%.
* 🦾 **Subgrid Constraint Stabilizer**: Dampens constraint tension and micro-vibrations across resting mechanical joints (rotors, hinges, pistons), banishing Clang feedback loops.
* 🚀 **Adaptive TOI Pruning**: Dynamically assigns discrete collision quality to low-speed cruising grids while preserving continuous TOI for high-speed grids and player-made missiles (PMWs).
* ⚡ **Zero Hot-Path Allocations**: Zero memory allocations in simulation loops with stepped evaluation intervals (30–120 frames).
* 🛡️ **GridDefender Harmony**: Clean architectural separation—GridDefender handles deformation protection & missile kinetics, while PhysicsOptimizer manages physics performance & suspension math.

---

## 2. System Architecture

```mermaid
graph TD
    subgraph Torch Server Host
        TorchGUI[Torch Server WPF Window] -->|Loads Tab via IWpfPlugin| UI[PhysicsOptimizerControl.xaml]
        UI -->|Data Binds to| Plugin[PhysicsOptimizerPlugin.cs]
        Plugin -->|Auto-Saves to| CfgFile[PhysicsOptimizer.cfg]
        Plugin -->|Maintains| Telemetry[OptimizationTelemetry.cs]
        Plugin -->|Commands| Commands[PhysicsOptimizerCommands.cs]
    end

    subgraph Optimization Modules
        Plugin --> M1[Module 1: Wheel & Suspension Optimizer]
        Plugin --> M2[Module 2: Rigid Body Sleep Manager]
        Plugin --> M3[Module 3: Floating Object & Ore Optimizer]
        Plugin --> M4[Module 4: Subgrid Constraint Stabilizer]
        Plugin --> M5[Module 5: Adaptive TOI / Collision Pruner]
    end

    subgraph Harmony Patches & Engine Hooks
        M1 --> P1[MotorSuspensionPatch.cs]
        M2 --> P2[CockpitInputWakePatch.cs]
        M2 --> P3[GridDamageWakePatch.cs]
        P1 --> GameSuspension[MyMotorSuspension.Update & CreateConstraint]
        P2 --> GameCockpit[MyShipController.MoveAndRotate]
        P3 --> GameDamage[MyCubeGrid.DoDamage]
    end
```

### Project Structure & Key Components

| Component | File | Purpose |
| :--- | :--- | :--- |
| **Plugin Entry** | [`PhysicsOptimizerPlugin.cs`](PhysicsOptimizations/PhysicsOptimizerPlugin.cs) | Main lifecycle controller (`TorchPluginBase`, `IWpfPlugin`). Manages persistent config, telemetry, module updates, and PatchManager. |
| **Config Model** | [`Config/PhysicsOptimizerConfig.cs`](PhysicsOptimizations/Config/PhysicsOptimizerConfig.cs) | Persistent ViewModel containing all configurable thresholds and toggles with Torch `[Display]` annotations. |
| **Telemetry** | [`Services/OptimizationTelemetry.cs`](PhysicsOptimizations/Services/OptimizationTelemetry.cs) | Thread-safe live gauges (Active/Sleeping bodies, Parked rovers, Sleeping wheels, Merged ore, Stabilized joints). |
| **Module 1 (Wheels)** | [`Modules/WheelOptimizerModule.cs`](PhysicsOptimizations/Modules/WheelOptimizerModule.cs) | Symmetrical broadphase collision masking (`subSystemDontCollideWith = 3`) and parked rover suspension sleeping. |
| **Module 2 (Sleep)** | [`Modules/RigidBodySleepModule.cs`](PhysicsOptimizations/Modules/RigidBodySleepModule.cs) | Stepped evaluator forcing idle unpiloted dynamic grids into Havok sleep mode (`rigidBody.Deactivate()`). |
| **Module 3 (Ore)** | [`Modules/FloatingObjectModule.cs`](PhysicsOptimizations/Modules/FloatingObjectModule.cs) | Proximity spatial clustering merging nearby matching ore/item stacks within configured radius. |
| **Module 4 (Subgrids)**| [`Modules/SubgridStabilizerModule.cs`](PhysicsOptimizations/Modules/SubgridStabilizerModule.cs) | Stabilizes constraint solver damping on resting mechanical subgrid joints (rotors, hinges, pistons). |
| **Module 5 (TOI)** | [`Modules/AdaptiveCollisionModule.cs`](PhysicsOptimizations/Modules/AdaptiveCollisionModule.cs) | Switches slow-moving cruising grids to discrete collision quality to eliminate continuous TOI pairs. |
| **Wheel Patch** | [`Patches/MotorSuspensionPatch.cs`](PhysicsOptimizations/Patches/MotorSuspensionPatch.cs) | Hooks `CreateConstraint`, `CubeGrid_OnPhysicsChanged`, and skips `Update()` when parked rover is sleeping. |
| **Cockpit Patch** | [`Patches/CockpitInputWakePatch.cs`](PhysicsOptimizations/Patches/CockpitInputWakePatch.cs) | Postfix on `MyShipController.MoveAndRotate` waking sleeping grids/rovers instantly upon pilot control input. |
| **Damage Patch** | [`Patches/GridDamageWakePatch.cs`](PhysicsOptimizations/Patches/GridDamageWakePatch.cs) | Postfix on `MyCubeGrid.DoDamage` waking sleeping rigid bodies upon receiving damage or projectile impact. |
| **Commands** | [`Commands/PhysicsOptimizerCommands.cs`](PhysicsOptimizations/Commands/PhysicsOptimizerCommands.cs) | In-game and Torch console admin commands under the `!phys` prefix. |
| **WPF GUI View** | [`Views/PhysicsOptimizerControl.xaml`](PhysicsOptimizations/Views/PhysicsOptimizerControl.xaml) | Dark-themed WPF interface with **Configuration** and **Live Telemetry** tabs. |

---

## 3. Core Optimization Modules Deep-Dive

### 🏎️ Module 1: Wheel & Suspension Physics Optimizer
* **Broadphase Collision Masking**: Space Engineers by default sets asymmetrical sub-system collision masks (`1, 1` on wheels vs `0, 0` on chassis). This causes Havok to perform high-frequency AABB compound shape checks against nearby armor blocks (fenders, wheel skirts, wheel wells) every single tick. The optimizer applies **symmetrical sub-system masking** (`subSystemDontCollideWith = 3` bits 0 and 1), completely eliminating redundant compound tree queries while leaving native wheel-to-ground physics 100% intact.
* **Parked Rover Suspension Sleep**: When a rover has its handbrake engaged and remains stationary ($< 0.1 \text{ m/s}$ linear, $< 0.02 \text{ rad/s}$ angular) for $\ge 2.0\text{s}$:
  * Suspends per-frame `MyMotorSuspension.Update()` calls, 60Hz ground raycasts, air-shock checks, and artificial braking impulses.
* **Instant Wakeup**: Wakes up immediately on handbrake release, pilot movement input (WASD/Space/C), or external impact.

### 💤 Module 2: Rigid Body Sleep Manager (Idle Grid Deactivator)
* **Stepped Scan**: Evaluates dynamic grids every 60 frames (1 second) without hot-path allocations.
* **Sleep Conditions**: If an unpiloted dynamic grid maintains linear velocity $< 0.05 \text{ m/s}$ and angular velocity $< 0.01 \text{ rad/s}$ for $\ge 3\text{ seconds}$, calls `grid.Physics.RigidBody.Deactivate()` to put the Havok rigid body to sleep.
* **Zero-Lag Wake Triggers**:
  * Pilot entering a cockpit or issuing movement/rotation controls.
  * Grid taking block damage or projectile impacts (`MyCubeGrid.DoDamage`).
  * Thruster firings or external collisions.

### ⛏️ Module 3: Floating Object & Ore Optimizer
* **Proximity Stack Merging**: Mining drills and rover combat scatter dozens of small ore rocks and dropped items into the world, each calculating independent Havok voxel collisions.
* **Clustering Algorithm**: Runs every 120 ticks (2 seconds), grouping floating objects of matching item definitions within a configured radius (default: $3.0\text{m}$).
* **Entity Elimination**: Merges item amounts into a single primary stack entity and closes redundant floating entities, reducing debris physics overhead by up to 80%.

### 🦾 Module 4: Subgrid Constraint Stabilizer at Rest
* **Mechanical Joint Evaluation**: Monitors mechanical connection blocks (`MyMechanicalConnectionBlockBase`: rotors, hinges, pistons).
* **Rest Detection**: When no target velocity is commanded (`TargetVelocityRPM == 0`, `Velocity == 0`) and relative joint movement is $< 0.005 \text{ rad/s}$ for $> 60\text{ frames}$:
  * Stabilizes constraint solver damping to lock the assembly as a unified rigid construct.
  * Stops micro-jitter and eliminates Havok constraint solver tension loops across cranes, arms, and rover subparts.

### 🚀 Module 5: Adaptive TOI (Continuous Collision) Pruning
* **Broadphase TOI Optimization**: Continuous collision detection (CCD/TOI) is computationally expensive in Havok broadphase.
* **Discrete Collision for Slow Grids**: Grids cruising below $15.0 \text{ m/s}$ are set to discrete `Debris` collision quality, bypassing continuous TOI pair generation.
* **Continuous TOI for Fast Grids & Missiles**: High-speed grids ($> 40.0 \text{ m/s}$) and small missiles retain continuous TOI collision to guarantee zero tunneling through voxels or armor.

---

## 4. Engineering Particularities & Edge Cases

### A. Non-Conflict Harmony with GridDefender
* **Separation of Concerns**: All wheel broadphase filter masking and suspension update throttling live exclusively inside `PhysicsOptimizer`. `GridDefender` focuses solely on deformation damage filtering and anti-clang push-apart routines.
* **Zero Collision**: Neither plugin attempts to overwrite or fight the other's Havok hooks.

### B. Terrain Phasing & Pertam Gravity
* High-speed rover drifting on Pertam dunes can occasionally cause physics jitter against voxel meshes. The combination of parked suspension sleep and subgrid stabilization stops Havok solver feedback loops from cratering sim-speed.

### C. Zero Allocations in Hot Paths
* The plugin strictly avoids memory allocations (`new`, LINQ, closures) inside `Update()` and Harmony hooks, relying on pre-allocated buffers and object pools to guarantee zero GC pauses.

---

## 5. In-Game & Console Admin Commands (`!phys`)

All commands require `Admin` permission level.

| Command | Description | Example |
| :--- | :--- | :--- |
| `!phys status` | Displays the status of all 5 optimization modules and current thresholds. | `!phys status` |
| `!phys stats` | Outputs real-time telemetry (Active/Sleeping bodies, Parked rovers asleep, Ore merged, Stabilized joints). | `!phys stats` |
| `!phys sleepall` | Forces all eligible idle dynamic grids on the server into Havok sleep mode immediately. | `!phys sleepall` |
| `!phys mergeore` | Triggers an instant proximity merge sweep across all floating ores and dropped items. | `!phys mergeore` |
| `!phys toggle <module>` | Toggles an individual optimization module on or off on the fly (`all`, `wheels`, `mask`, `parkedsleep`, `sleep`, `ore`, `subgrids`, `toi`, `debug`). | `!phys toggle parkedsleep` |
| `!phys reload` | Reloads configuration from `PhysicsOptimizer.cfg` on disk. | `!phys reload` |
| `!phys resetstats` | Resets live telemetry counters to zero. | `!phys resetstats` |

---

## 6. Configuration Reference (`PhysicsOptimizer.cfg`)

The configuration file is saved automatically to `Torch\Plugins\Storage\PhysicsOptimizer\PhysicsOptimizer.cfg` (or in the plugin directory).

### Configuration Options Table

| Setting | Type | Default | Description |
| :--- | :---: | :---: | :--- |
| `Enabled` | `bool` | `true` | Master plugin toggle. |
| `EnableDebugLogging` | `bool` | `false` | Enables verbose trace logging in Torch console. |
| `EnableWheelOptimization` | `bool` | `true` | Master toggle for Module 1 (Wheel & Suspension). |
| `EnableWheelCollisionFilter` | `bool` | `true` | Symmetrical sub-system masking for wheel wells. |
| `SleepParkedRovers` | `bool` | `true` | Pauses 60Hz raycasts & suspension math when parked. |
| `RoverSleepDelaySeconds` | `float` | `2.0` | Motionless seconds before parked rover suspension enters sleep. |
| `EnableAggressiveSleeping` | `bool` | `true` | Master toggle for Module 2 (Rigid Body Sleep). |
| `SleepLinearVelocityThreshold` | `float` | `0.05` | Linear speed (m/s) below which an unpiloted grid is eligible for sleep. |
| `SleepAngularVelocityThreshold` | `float` | `0.01` | Angular speed (rad/s) below which an unpiloted grid is eligible for sleep. |
| `IdleSecondsBeforeSleep` | `int` | `3` | Motionless seconds required before deactivating grid rigid body. |
| `EnableFloatingObjectOptimizer` | `bool` | `true` | Master toggle for Module 3 (Floating Objects & Ore). |
| `AutoMergeNearbyOre` | `bool` | `true` | Automatically merges matching floating ores/items within proximity. |
| `OreMergeRadiusMeters` | `float` | `3.0` | Spatial radius (meters) for proximity ore clustering. |
| `OreMergeIntervalTicks` | `int` | `120` | Simulation ticks between proximity merge sweeps (60 = 1 sec). |
| `MaxSectorFloatingObjects` | `int` | `64` | Local area floating object threshold. |
| `EnableSubgridStabilization` | `bool` | `true` | Master toggle for Module 4 (Subgrid Constraint Stabilizer). |
| `SubgridRestVelocityThreshold` | `float` | `0.005` | Joint angular velocity threshold (rad/s) to consider subgrid at rest. |
| `SubgridRestFramesThreshold` | `int` | `60` | Consecutive rest frames (~1 sec) before locking constraint damping. |
| `EnableAdaptiveTOI` | `bool` | `true` | Master toggle for Module 5 (Adaptive TOI Collision Pruning). |
| `ContinuousCollisionSpeedThreshold` | `float` | `40.0` | Speed (m/s) above which grids retain continuous TOI collision. |
| `DiscreteCollisionSpeedThreshold` | `float` | `15.0` | Speed (m/s) below which grids use discrete collision quality. |

---

## 7. Torch WPF Server Interface

The GUI integrates into the Torch Server window, matching the standard dark-themed layout of `GridDefender` and `TorchRemoteCleanupPlugin`:

1. **Configuration Tab**:
   * Quick overview banner highlighting active optimization systems.
   * Granular module toggles, threshold textboxes, and sliders.
   * Quick **"Save Configuration"** button.
2. **Live Telemetry Tab**:
   * **4-Column Metric Card**: Active Bodies (Red), Sleeping Bodies (Green), Rovers Asleep (Blue), and Sleeping Wheels (Green).
   * **Detailed Telemetry Breakdown**: Merged ore stacks, eliminated entities, forced sleep events, stabilized subgrid joints, and discrete TOI grid counts.
   * **Manual Action Controls**: `Force Sleep All Idle Grids`, `Run Ore Proximity Merge Now`, and `Reset Counters`.

---

## 8. Building & Deployment

### 1. Configure Torch Directory Path
Open [`PhysicsOptimizations.csproj`](PhysicsOptimizations/PhysicsOptimizations.csproj) and verify `<TorchDir>`:
```xml
<TorchDir>C:\SE_GVK_S10</TorchDir>
```

### 2. Build the Solution
```bash
dotnet build -c Release PhysicsOptimizations\PhysicsOptimizations.csproj
```
The build automatically creates and deploys the zip archive:
```
C:\SE_GVK_S10\Plugins\GVK_PhysicsOptimizations.zip
├── GVK.PhysicsOptimizations.dll
├── GVK.PhysicsOptimizations.pdb
└── manifest.xml
```

---

## 9. In-Game Testing Checklist

1. **GUI & Persistence Verification**:
   - [ ] Start Torch Server $\rightarrow$ Open **GVK Physics Optimizer** tab.
   - [ ] Verify both **Configuration** and **Live Telemetry** tabs are populated.
   - [ ] Modify a threshold (e.g. Rover Sleep Delay) and click **Save Configuration**.
2. **Module 1 (Wheels) Verification**:
   - [ ] Spawn a rover and drive it onto terrain.
   - [ ] Engage the handbrake and let it sit for $> 2$ seconds.
   - [ ] Check **Live Telemetry** $\rightarrow$ Verify **Rovers Asleep** and **Sleeping Wheels** increment.
   - [ ] Press `W` (throttle) $\rightarrow$ Verify suspension wakes up immediately with zero input lag.
3. **Module 2 (Rigid Body Sleep) Verification**:
   - [ ] Spawn an unpiloted dynamic ship and let it come to a complete stop.
   - [ ] Verify **Sleeping Bodies** increments after 3 seconds.
   - [ ] Shoot the ship $\rightarrow$ Verify it immediately wakes up and responds to physics forces.
4. **Module 3 (Ore Merging) Verification**:
   - [ ] Drop or mine multiple ore boulders within 3 meters of each other.
   - [ ] Verify the boulders merge into a single larger stack and the redundant entities are removed.
5. **Module 4 (Subgrids) Verification**:
   - [ ] Spawn a ship with a multi-joint rotor/piston crane at rest.
   - [ ] Verify **Stabilized Subgrid Joints** increments and joint micro-jitter stops.

---

## 10. License & Credits

* **Author**: GVK Modding Team
* **Target Server**: GV - Deserts of Kharak (GVK)
* **License**: MIT

