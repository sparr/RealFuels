using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RealFuels
{
    /// <summary>
    /// Makes MechJeb's fuel flow simulation report how long each engine burns.
    /// MechJeb only publishes burn time per segment of a stage, so a transpiler adds a
    /// call to <see cref="RecordStep"/> at the point in <c>SimulateStage</c> where the
    /// simulation advances time, and the active engines are credited with that step.
    /// <para>
    /// The simulation runs on a worker thread. Results are accumulated in a dictionary
    /// private to the run and published when the run ends. They are keyed by MechJeb's
    /// pooled sim objects, so they are discarded whenever MechJeb rebuilds its sim vessel.
    /// </para>
    /// </summary>
    internal static class MechJebEngineBurnTimes
    {
        private class Record
        {
            public Dictionary<object, double> working;
            public volatile Dictionary<object, double> completed;
        }

        private static readonly ConditionalWeakTable<object, Record> _records = new ConditionalWeakTable<object, Record>();

        private static FieldInfo _activeEngines;
        private static FieldInfo _managerSimulation;
        private static FieldInfo _managerModuleMapping;

        private static bool _attempted = false;
        private static bool _injected = false;

        /// <summary>True once the patches are applied.</summary>
        public static bool Patched { get; private set; } = false;

        /// <summary>Applies the patches on first use. Must be called from the main thread.</summary>
        public static bool EnsurePatched()
        {
            if (_attempted) return Patched;
            _attempted = true;

            try
            {
                Type simType = Type.GetType("MechJebLib.FuelFlowSimulation.FuelFlowSimulation, MechJebLib", false);
                Type vesselType = Type.GetType("MechJebLib.FuelFlowSimulation.SimVessel, MechJebLib", false);
                Type managerType = Type.GetType("MechJebLibBindings.FuelFlowSimulation.SimVesselManager, MechJebLibBindings", false);
                if (simType == null || vesselType == null || managerType == null) return false;

                MethodInfo simulateStage = AccessTools.Method(simType, "SimulateStage", new[] { vesselType });
                MethodInfo run = AccessTools.Method(simType, "Run");
                MethodInfo build = AccessTools.Method(managerType, "Build");
                _activeEngines = AccessTools.Field(vesselType, "ActiveEngines");
                _managerSimulation = AccessTools.Field(managerType, "FuelFlowSimulation");
                _managerModuleMapping = AccessTools.Field(managerType, "_inversePartModuleMapping");
                if (simulateStage == null || run == null || build == null || _activeEngines == null
                    || _managerSimulation == null || _managerModuleMapping == null)
                    return false;

                var harmony = new Harmony("RealFuels.MechJebEngineBurnTimes");
                harmony.Patch(simulateStage, transpiler: new HarmonyMethod(typeof(MechJebEngineBurnTimes), nameof(SimulateStageTranspiler)));
                if (!_injected)
                {
                    Debug.LogWarning("[RealFuels] Could not hook MechJeb's fuel flow simulation, engine burn times will be stage estimates.");
                    return false;
                }
                harmony.Patch(run,
                    prefix: new HarmonyMethod(typeof(MechJebEngineBurnTimes), nameof(RunPrefix)),
                    postfix: new HarmonyMethod(typeof(MechJebEngineBurnTimes), nameof(RunPostfix)));
                harmony.Patch(build, prefix: new HarmonyMethod(typeof(MechJebEngineBurnTimes), nameof(BuildPrefix)));
                Patched = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RealFuels] Could not patch MechJeb's fuel flow simulation, engine burn times will be stage estimates: {e}");
            }
            return Patched;
        }

        /// <summary>
        /// Burn time of the engine module <paramref name="engine"/> in the last completed
        /// run of <paramref name="manager"/>'s simulation. Zero for an engine that never
        /// burns. Returns false when there is no current result, or when MechJeb does not
        /// simulate that module as an engine.
        /// </summary>
        public static bool TryGetBurnTime(object manager, PartModule engine, out double burnTime)
        {
            burnTime = 0d;
            if (!Patched || manager == null || engine == null) return false;

            object sim = _managerSimulation.GetValue(manager);
            if (sim == null || !_records.TryGetValue(sim, out Record record)) return false;
            Dictionary<object, double> completed = record.completed;
            if (completed == null) return false;

            // The results are keyed by sim engine module. Only the ones that burned are
            // present, so find the one MechJeb built for this engine among them.
            if (!(_managerModuleMapping.GetValue(manager) is IDictionary mapping)) return false;

            bool simulated = false;
            foreach (DictionaryEntry entry in mapping)
            {
                if (!ReferenceEquals(entry.Value, engine)) continue;
                simulated = true;
                if (completed.TryGetValue(entry.Key, out double time))
                {
                    burnTime = time;
                    break;
                }
            }
            return simulated;
        }

        // Inserts RecordStep(this, vessel, dt) after the ApplyResourceDrains(dt) call.
        private static IEnumerable<CodeInstruction> SimulateStageTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            for (int i = 1; i < code.Count; i++)
            {
                if ((code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt)
                    && code[i].operand is MethodInfo method && method.Name == "ApplyResourceDrains"
                    && code[i - 1].IsLdloc())
                {
                    code.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldarg_1),
                        new CodeInstruction(code[i - 1].opcode, code[i - 1].operand),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MechJebEngineBurnTimes), nameof(RecordStep)))
                    });
                    _injected = true;
                    break;
                }
            }
            return code;
        }

        // Runs on MechJeb's worker thread, inside its simulation loop. Must not log or throw.
        public static void RecordStep(object simulation, object vessel, double dt)
        {
            try
            {
                if (!_records.TryGetValue(simulation, out Record record)) return;
                Dictionary<object, double> working = record.working;
                if (working == null || !(_activeEngines.GetValue(vessel) is IList engines)) return;

                for (int i = 0; i < engines.Count; i++)
                {
                    object engine = engines[i];
                    working.TryGetValue(engine, out double time);
                    working[engine] = time + dt;
                }
            }
            catch (Exception) { }
        }

        private static void RunPrefix(object __instance)
        {
            _records.GetOrCreateValue(__instance).working = new Dictionary<object, double>();
        }

        // Skipped when the run throws, which leaves the previous result in place.
        private static void RunPostfix(object __instance)
        {
            if (!_records.TryGetValue(__instance, out Record record)) return;
            record.completed = record.working;
            record.working = null;
        }

        // A rebuild returns the sim objects to their pools, so results keyed by them are void.
        private static void BuildPrefix(object __instance)
        {
            object sim = _managerSimulation.GetValue(__instance);
            if (sim != null && _records.TryGetValue(sim, out Record record))
                record.completed = null;
        }
    }
}
