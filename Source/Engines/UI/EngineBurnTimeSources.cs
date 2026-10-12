using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RealFuels
{
    /// <summary>
    /// Sources of calculated burn time for the "Calc → %" simulation mode.
    /// Each source is another mod's staging simulation, read through reflection so that
    /// RealFuels has no hard dependency on it.
    /// <para>
    /// The sources publish burn time per stage, not per engine, so each is patched to
    /// record it per engine as it simulates. If a patch cannot be applied, the engine's
    /// burn time is estimated as the sum of the stages from the one that ignites it
    /// (<see cref="Part.inverseStage"/>) up to, but excluding, the one that drops it
    /// (<see cref="Part.separationIndex"/>). This overestimates an engine that flames
    /// out while other engines keep the same stage burning.
    /// </para>
    /// </summary>
    public static class EngineBurnTimeSources
    {
        public struct StageTime
        {
            public int stage;
            public double time;
        }

        public abstract class Source
        {
            public abstract string Name { get; }

            /// <summary>
            /// Burn time of the engine module <paramref name="engine"/> of
            /// <paramref name="part"/>. <paramref name="isEstimate"/> is set when it is
            /// derived from stage times, which cannot tell the engine modules of one part
            /// apart. Returns false when no results are available.
            /// </summary>
            public abstract bool TryGetBurnTime(Part part, PartModule engine, out double burnTime, out bool isEstimate);
        }

        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static;

        // Seconds between polls of the selected source.
        private const float PollInterval = 0.25f;
        // Seconds a result is kept while the source has nothing to report, which
        // happens routinely while it is in the middle of a simulation run.
        private const float ResultMaxAge = 3f;

        /// <summary>
        /// True while the simulation controls are in "Calc → %" mode. Static, like the
        /// other mode toggles, so it persists across all engines.
        /// </summary>
        public static bool CalcMode = false;

        private static List<Source> _sources;
        private static int _sourceIndex = 0;

        private static readonly List<StageTime> _stageTimes = new List<StageTime>();
        private static Part _cachePart;
        private static PartModule _cacheEngine;
        private static int _cacheSourceIndex = -1;
        private static float _cachePolledAt = float.MinValue;
        private static float _cacheValidAt = float.MinValue;
        private static float _cacheBurnTime;
        private static bool _cacheIsEstimate;

        private static List<Source> Sources
        {
            get
            {
                if (_sources == null)
                {
                    _sources = new List<Source>();
                    var mechJeb = new MechJebSource();
                    if (mechJeb.Available) _sources.Add(mechJeb);
                    var ker = new KerbalEngineerSource();
                    if (ker.Available) _sources.Add(ker);
                }
                return _sources;
            }
        }

        /// <summary>True when at least one source mod is installed.</summary>
        public static bool AnyAvailable => Sources.Count > 0;

        public static string CurrentSourceName => AnyAvailable ? Sources[_sourceIndex].Name : string.Empty;

        public static void CycleSource()
        {
            if (AnyAvailable)
                _sourceIndex = (_sourceIndex + 1) % Sources.Count;
        }

        /// <summary>
        /// Polls the selected source for the engine module <paramref name="engine"/> of
        /// <paramref name="part"/>, at most once per <see cref="PollInterval"/>.
        /// Polling makes the source collect and publish new simulation results, so this
        /// must be called from Update, never from OnGUI: the source's own windows break
        /// if their data changes between their Layout and Repaint passes.
        /// </summary>
        public static void Refresh(Part part, PartModule engine)
        {
            if (!AnyAvailable || part == null) return;

            float now = Time.realtimeSinceStartup;
            if (part != _cachePart || engine != _cacheEngine || _sourceIndex != _cacheSourceIndex)
            {
                _cachePart = part;
                _cacheEngine = engine;
                _cacheSourceIndex = _sourceIndex;
                _cachePolledAt = float.MinValue;
                _cacheValidAt = float.MinValue;
            }

            if (now - _cachePolledAt >= PollInterval)
            {
                _cachePolledAt = now;
                if (Poll(part, engine, out float polled, out bool polledIsEstimate))
                {
                    _cacheBurnTime = polled;
                    _cacheIsEstimate = polledIsEstimate;
                    _cacheValidAt = now;
                }
            }
        }

        /// <summary>
        /// Burn time of the engine module <paramref name="engine"/> of
        /// <paramref name="part"/> from the last <see cref="Refresh"/>.
        /// <paramref name="isEstimate"/> is set when it is derived from stage times.
        /// Returns false when the source has no usable result.
        /// </summary>
        public static bool TryGetBurnTime(Part part, PartModule engine, out float burnTime, out bool isEstimate)
        {
            burnTime = 0f;
            isEstimate = false;
            if (part == null || part != _cachePart || engine != _cacheEngine || _sourceIndex != _cacheSourceIndex)
                return false;
            if (Time.realtimeSinceStartup - _cacheValidAt > ResultMaxAge) return false;
            burnTime = _cacheBurnTime;
            isEstimate = _cacheIsEstimate;
            return true;
        }

        private static bool Poll(Part part, PartModule engine, out float burnTime, out bool isEstimate)
        {
            burnTime = 0f;
            isEstimate = false;
            double total;
            try
            {
                if (!Sources[_sourceIndex].TryGetBurnTime(part, engine, out total, out isEstimate)) return false;
            }
            catch (Exception)
            {
                return false;
            }

            if (!(total > 0d) || double.IsInfinity(total)) return false;
            burnTime = (float)total;
            return true;
        }

        // Sums the stages in which the engine is both ignited and still attached.
        private static double SumEngineStages(Part part, List<StageTime> stageTimes)
        {
            double total = 0d;
            foreach (StageTime st in stageTimes)
            {
                if (st.stage <= part.inverseStage && st.stage > part.separationIndex)
                    total += st.time;
            }
            return total;
        }

        private static List<Part> VesselParts(Part part)
        {
            if (HighLogic.LoadedSceneIsEditor)
                return EditorLogic.fetch?.ship?.parts;
            return part.vessel != null ? part.vessel.parts : null;
        }

        /// <summary>
        /// MechJeb's stage stats, as shown in its Delta-V Stats window. Per-engine times
        /// come from <see cref="MechJebEngineBurnTimes"/>. If that patch cannot be applied,
        /// the per-segment times MechJeb publishes are used for a stage estimate. Uses only
        /// members present in both the release (2.15) and dev (2.16) builds.
        /// </summary>
        private class MechJebSource : Source
        {
            private readonly Type _coreType;
            private readonly MethodInfo _getMasterMechJeb;
            private readonly MethodInfo _getComputerModule;
            private FieldInfo _editorBody;
            private FieldInfo _vacStats;
            private FieldInfo _vesselManagerVac;
            private MethodInfo _requestUpdate;
            private FieldInfo _kspStage;
            private FieldInfo _deltaTime;

            public override string Name => "MechJeb";

            public bool Available => _coreType != null && _getMasterMechJeb != null && _getComputerModule != null;

            public MechJebSource()
            {
                _coreType = Type.GetType("MuMech.MechJebCore, MechJeb2", false);
                _getMasterMechJeb = Type.GetType("MuMech.VesselExtensions, MechJeb2", false)
                    ?.GetMethod("GetMasterMechJeb", StaticFlags, null, new[] { typeof(Vessel) }, null);
                _getComputerModule = _coreType?.GetMethod("GetComputerModule", InstanceFlags, null, new[] { typeof(string) }, null);
            }

            public override bool TryGetBurnTime(Part part, PartModule engine, out double burnTime, out bool isEstimate)
            {
                burnTime = 0d;
                isEstimate = false;
                object stats = FindStageStats(part);
                if (stats == null) return false;

                if (_vacStats == null)
                {
                    Type statsType = stats.GetType();
                    _editorBody = statsType.GetField("EditorBody", InstanceFlags);
                    _vacStats = statsType.GetField("VacStats", InstanceFlags);
                    _vesselManagerVac = statsType.GetField("_vesselManagerVac", BindingFlags.NonPublic | BindingFlags.Instance);
                    _requestUpdate = statsType.GetMethod("RequestUpdate", InstanceFlags, null, Type.EmptyTypes, null);
                }
                if (_vacStats == null || _requestUpdate == null) return false;

                // MechJeb does not simulate in the editor until a body is chosen, which
                // its own Delta-V Stats window does the first time it is drawn.
                if (HighLogic.LoadedSceneIsEditor && _editorBody != null && _editorBody.GetValue(stats) == null)
                    _editorBody.SetValue(stats, Planetarium.fetch.Home);

                // Read before requesting an update: in the editor every new run rebuilds
                // the sim vessel, which discards the per-engine results.
                bool patched = _vesselManagerVac != null && MechJebEngineBurnTimes.EnsurePatched();
                bool hasEngineTime = patched
                    && MechJebEngineBurnTimes.TryGetBurnTime(_vesselManagerVac.GetValue(stats), engine, out burnTime);

                // Collects finished results and starts the next run, as MechJeb's own
                // consumers of the stage stats do.
                _requestUpdate.Invoke(stats, null);

                if (patched) return hasEngineTime;

                if (!(_vacStats.GetValue(stats) is IList segments) || segments.Count == 0) return false;

                _stageTimes.Clear();
                foreach (object segment in segments)
                {
                    if (_kspStage == null)
                    {
                        Type segmentType = segment.GetType();
                        _kspStage = segmentType.GetField("KSPStage", InstanceFlags);
                        _deltaTime = segmentType.GetField("DeltaTime", InstanceFlags);
                    }
                    if (_kspStage == null || _deltaTime == null) return false;

                    _stageTimes.Add(new StageTime
                    {
                        stage = (int)_kspStage.GetValue(segment),
                        time = (double)_deltaTime.GetValue(segment)
                    });
                }
                burnTime = SumEngineStages(part, _stageTimes);
                isEstimate = true;
                return true;
            }

            private object FindStageStats(Part part)
            {
                List<Part> parts = VesselParts(part);
                if (parts == null || !parts.Contains(part)) return null;

                // Only the master core's stage stats are kept up to date by MechJeb. In the
                // editor it is looked up with a null vessel.
                object core = _getMasterMechJeb.Invoke(null, new object[] { HighLogic.LoadedSceneIsEditor ? null : part.vessel });
                if (core == null) return null;
                return _getComputerModule.Invoke(core, new object[] { "MechJebModuleStageStats" });
            }
        }

        /// <summary>
        /// Kerbal Engineer Redux's vessel simulation, as shown in its build engineer.
        /// Per-engine times come from <see cref="KerbalEngineerBurnTimes"/>. If that patch
        /// cannot be applied, the per-stage times KER publishes are used for a stage estimate.
        /// </summary>
        private class KerbalEngineerSource : Source
        {
            private readonly PropertyInfo _stages;
            private readonly MethodInfo _requestSimulation;
            private readonly MethodInfo _tryStartSimulation;
            private readonly FieldInfo _time;

            public override string Name => "KER";

            public bool Available => _stages != null && _requestSimulation != null && _tryStartSimulation != null && _time != null;

            public KerbalEngineerSource()
            {
                Type simManager = Type.GetType("KerbalEngineer.VesselSimulator.SimManager, KerbalEngineer", false);
                Type stage = Type.GetType("KerbalEngineer.VesselSimulator.Stage, KerbalEngineer", false);
                _stages = simManager?.GetProperty("Stages", StaticFlags);
                _requestSimulation = simManager?.GetMethod("RequestSimulation", StaticFlags, null, Type.EmptyTypes, null);
                _tryStartSimulation = simManager?.GetMethod("TryStartSimulation", StaticFlags, null, Type.EmptyTypes, null);
                _time = stage?.GetField("time", InstanceFlags);
            }

            public override bool TryGetBurnTime(Part part, PartModule engine, out double burnTime, out bool isEstimate)
            {
                burnTime = 0d;
                isEstimate = true;

                // The simulation always runs on the editor ship or the active vessel.
                List<Part> parts = VesselParts(part);
                if (parts == null || !parts.Contains(part)) return false;
                if (HighLogic.LoadedSceneIsFlight && part.vessel != FlightGlobals.ActiveVessel) return false;

                bool patched = KerbalEngineerBurnTimes.EnsurePatched();
                bool hasEngineTime = patched && KerbalEngineerBurnTimes.TryGetBurnTime(engine, out burnTime);

                // The stage results are cleared when a run starts, so read them before
                // asking for the next one.
                var stages = _stages.GetValue(null, null) as Array;

                _requestSimulation.Invoke(null, null);
                _tryStartSimulation.Invoke(null, null);

                if (patched)
                {
                    isEstimate = false;
                    return hasEngineTime;
                }

                if (stages == null || stages.Length == 0) return false;

                // The array is indexed by stage number.
                _stageTimes.Clear();
                for (int i = 0; i < stages.Length; i++)
                {
                    object stage = stages.GetValue(i);
                    if (stage == null) continue;
                    _stageTimes.Add(new StageTime { stage = i, time = (double)_time.GetValue(stage) });
                }
                burnTime = SumEngineStages(part, _stageTimes);
                return true;
            }
        }
    }
}
