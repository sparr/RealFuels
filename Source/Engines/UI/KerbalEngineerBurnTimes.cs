using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealFuels
{
    /// <summary>
    /// Makes Kerbal Engineer Redux's vessel simulation report how long each engine burns.
    /// KER only publishes burn time per stage, so a transpiler adds a call to
    /// <see cref="RecordStep"/> at the point in <c>RunSimulation</c> where the stage
    /// time is advanced, and the active engines are credited with that step.
    /// <para>
    /// KER has a single simulation, prepared on the main thread and run on a worker
    /// thread, never both at once. Its engine sims do not remember the engine module
    /// they were made from, so that is captured as they are created during preparation
    /// and used to key the results by engine module when the run ends.
    /// </para>
    /// </summary>
    internal static class KerbalEngineerBurnTimes
    {
        // Steps longer than this are engines that never run dry, which KER reports as zero time.
        private const double MaxStep = 86400d;

        private static readonly Dictionary<object, PartModule> _engineModules = new Dictionary<object, PartModule>();
        private static Dictionary<object, double> _working;
        private static volatile Dictionary<PartModule, double> _completed;

        private static FieldInfo _activeEngines;

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
                Type simType = Type.GetType("KerbalEngineer.VesselSimulator.Simulation, KerbalEngineer", false);
                Type engineSimType = Type.GetType("KerbalEngineer.VesselSimulator.EngineSim, KerbalEngineer", false);
                if (simType == null || engineSimType == null) return false;

                MethodInfo prepareSimulation = AccessTools.Method(simType, "PrepareSimulation");
                MethodInfo runSimulation = AccessTools.Method(simType, "RunSimulation");
                MethodInfo newEngineSim = AccessTools.Method(engineSimType, "New");
                _activeEngines = AccessTools.Field(simType, "activeEngines");
                if (prepareSimulation == null || runSimulation == null || newEngineSim == null || _activeEngines == null)
                    return false;

                var harmony = new Harmony("RealFuels.KerbalEngineerBurnTimes");
                harmony.Patch(runSimulation,
                    prefix: new HarmonyMethod(typeof(KerbalEngineerBurnTimes), nameof(RunSimulationPrefix)),
                    postfix: new HarmonyMethod(typeof(KerbalEngineerBurnTimes), nameof(RunSimulationPostfix)),
                    transpiler: new HarmonyMethod(typeof(KerbalEngineerBurnTimes), nameof(RunSimulationTranspiler)));
                if (!_injected)
                {
                    harmony.UnpatchAll(harmony.Id);
                    Debug.LogWarning("[RealFuels] Could not hook Kerbal Engineer's simulation, engine burn times will be stage estimates.");
                    return false;
                }
                harmony.Patch(prepareSimulation, prefix: new HarmonyMethod(typeof(KerbalEngineerBurnTimes), nameof(PrepareSimulationPrefix)));
                harmony.Patch(newEngineSim, postfix: new HarmonyMethod(typeof(KerbalEngineerBurnTimes), nameof(NewEngineSimPostfix)));
                Patched = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RealFuels] Could not patch Kerbal Engineer's simulation, engine burn times will be stage estimates: {e}");
            }
            return Patched;
        }

        /// <summary>
        /// Burn time of the engine module <paramref name="engine"/> in the last completed
        /// simulation run. Zero for an engine that never burns. Returns false when there
        /// is no result yet.
        /// </summary>
        public static bool TryGetBurnTime(PartModule engine, out double burnTime)
        {
            burnTime = 0d;
            if (!Patched || engine == null) return false;

            Dictionary<PartModule, double> completed = _completed;
            if (completed == null) return false;

            completed.TryGetValue(engine, out burnTime);
            return true;
        }

        // Inserts RecordStep(this, resourceDrainTime) after "stageTime += resourceDrainTime".
        private static IEnumerable<CodeInstruction> RunSimulationTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            for (int i = 2; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Stfld && code[i].operand is FieldInfo field && field.Name == "stageTime"
                    && code[i - 1].opcode == OpCodes.Add && code[i - 2].IsLdloc())
                {
                    code.InsertRange(i + 1, new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(code[i - 2].opcode, code[i - 2].operand),
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(KerbalEngineerBurnTimes), nameof(RecordStep)))
                    });
                    _injected = true;
                    break;
                }
            }
            return code;
        }

        // Runs on KER's worker thread, inside its simulation loop. Must not log or throw.
        public static void RecordStep(object simulation, double dt)
        {
            try
            {
                Dictionary<object, double> working = _working;
                if (working == null || !(dt > 0d) || dt > MaxStep) return;
                if (!(_activeEngines.GetValue(simulation) is IList engines)) return;

                for (int i = 0; i < engines.Count; i++)
                {
                    object engine = engines[i];
                    working.TryGetValue(engine, out double time);
                    working[engine] = time + dt;
                }
            }
            catch (Exception) { }
        }

        private static void PrepareSimulationPrefix()
        {
            _engineModules.Clear();
        }

        private static void NewEngineSimPostfix(ModuleEngines engineMod, object __result)
        {
            if (__result != null && engineMod != null)
                _engineModules[__result] = engineMod;
        }

        private static void RunSimulationPrefix()
        {
            _working = new Dictionary<object, double>();
        }

        // Skipped when the run throws, which leaves the previous result in place.
        private static void RunSimulationPostfix()
        {
            Dictionary<object, double> working = _working;
            _working = null;
            if (working == null) return;

            var completed = new Dictionary<PartModule, double>();
            foreach (KeyValuePair<object, PartModule> entry in _engineModules)
            {
                working.TryGetValue(entry.Key, out double time);
                completed[entry.Value] = time;
            }
            _completed = completed;
        }
    }
}
