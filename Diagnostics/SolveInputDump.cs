using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using SeaPower;
using UnityEngine;

namespace AutoTOT
{
    /// <summary>
    /// Writes one solver input to disk as plain text, so the same solve can be replayed outside the
    /// game.
    ///
    /// <see cref="FlightTime.SolveInput"/> is a pure value: the step loop reads no Unity API and no
    /// mutable game state, so a solve is fully determined by this struct plus the ammunition fields
    /// the loop and the game's thrust helper read. Launch-time loft optimization is resolved before
    /// the struct is written; configured/resolved heights and the resulting Kappa nodes are all
    /// captured, so offline replay does not repeat a mutable game-state lookup.
    ///
    /// One file per round, under BepInEx/AutoTOT-solve/. Written only when TraceFlightModel is on,
    /// the same gate as the sim-track trace, and never on the normal path.
    /// </summary>
    internal static class SolveInputDump
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal const string CaptureSchema = "rich-flight-v3";
        // The assembly MVID changes with the compiled artifact and is available even when several
        // development builds intentionally share the same user-facing mod version.
        private static string ModBuildId =>
            typeof(SolveInputDump).Assembly.ManifestModule.ModuleVersionId.ToString("N");

        // Monotonic, so two rounds of one ammunition launched on the same tick still get
        // separate entries.
        private static int _seq;

        internal static string Directory => Path.Combine(Paths.BepInExRootPath, "AutoTOT-solve");

        /// <summary>Writes one input and returns its key, or null if it could not be written.</summary>
        internal static string Write(string label, in FlightTime.SolveInput i, AmmunitionParameters ap,
                                     float inGameEstimate = float.NaN, float inGameStep = 0f)
        {
            try
            {
                // Keyed by label, wall clock and mission time. Label alone meant a second sortie
                // silently overwrote the first. Mission time alone is not enough either: firing one
                // round early in each mission, which is the cleanest way to gather data, reproduces
                // nearly the same mission time every run, so the corpus would hold one entry per
                // ammunition instead of one per shot. The wall clock is what actually separates two
                // sorties; mission time stays in the name because it is what the log lines show.
                // Label, wall clock, mission time and a sequence number. The label alone repeats
                // whenever the same ammunition is fired twice, and mission time alone repeats across
                // missions, since firing early in each one is the cleanest way to gather data. Both
                // ambiguities pair one round's outcome with another round's input, which is worse
                // than losing the round.
                string key = Sanitize(label) + "__" +
                             DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + "__t" +
                             GameClock.SimNow().ToString("0.0", Inv) + "__" +
                             System.Threading.Interlocked.Increment(ref _seq).ToString(Inv);
                System.IO.Directory.CreateDirectory(Directory);

                var b = new StringBuilder();
                b.AppendLine("# AutoTOT solver input. Replay with tools/lab.");
                b.AppendLine(F("Label", label));
                b.AppendLine(F("Key", key));
                b.AppendLine(F("SolvedAtSim", GameClock.SimNow()));
                b.AppendLine(F("ModBuild", ModBuildId));
                b.AppendLine(F("CaptureSchema", CaptureSchema));
                b.AppendLine(F("AmmoFingerprint", AmmunitionFingerprint(ap)));
                // What this build's step loop returned for this exact input, and the step it ran
                // at. tools/lab `parity` replays the input and must reproduce the value.
                b.AppendLine(F("InGameEstimate", inGameEstimate));
                b.AppendLine(F("InGameStep", inGameStep));

                // Ammunition. Two fields the step loop reads directly, and six more that
                // MissileSimulator.CalculateThrustOverTime reads out of the same object.
                b.AppendLine(F("Ammo.MinVelocity", ap.MinVelocity));
                b.AppendLine(F("Ammo.LiftFactor", ap.LiftFactor));
                b.AppendLine(F("Ammo.Acceleration", ap._acceleration));
                b.AppendLine(F("Ammo.AccelerationTime", ap._accelerationTime));
                b.AppendLine(F("Ammo.SustainerBurnAcceleration", ap._sustainerBurnAcceleration));
                b.AppendLine(F("Ammo.SustainerBurnTime", ap._sustainerBurnTime));
                b.AppendLine(F("Ammo.MaxVelocityInKnots", ap._maxVelocityInKnots));
                b.AppendLine(F("Ammo.Kinematics", ap.Kinematics.ToString()));
                b.AppendLine(F("Ammo.OptimizeLoftAlt", ap._optimizeLoftAlt));
                b.AppendLine(F("Ammo.TerminalAltRaw", ap._terminalAltUnity));
                b.AppendLine(F("Ammo.TerminalAltMode", ap._terminalAltMode.ToString()));
                b.AppendLine(F("Ammo.LoftToSkim", ap._loftToSkim));
                b.AppendLine(F("Ammo.TargetType", ap._targetType.ToString()));
                // Raw trajectory/controller fields are retained even when SolveInput also carries
                // their resolved value. That leaves future generalized models something to train
                // against and makes modded ammunition changes attributable instead of anonymous.
                b.AppendLine(F("Ammo.Deceleration", ap._deceleration));
                b.AppendLine(F("Ammo.MaxLoftVelocityInKnots", ap._maxLoftVelocityInKnots));
                b.AppendLine(F("Ammo.TerminalVelocityInKnots", ap._terminalVelocityInKnots));
                b.AppendLine(F("Ammo.MaxLoftAngle", ap._maxLoftAngle));
                b.AppendLine(F("Ammo.FinalFlightPhaseMaxAngle", ap._finalFlightPhaseMaxAngle));
                b.AppendLine(F("Ammo.SeaSkimmingMaxDescentAngle", ap._seaSkimmingMaxDescentAngle));
                b.AppendLine(F("Ammo.FinalFlightPhaseAltUnity", ap._finalFlightPhaseAltUnity));
                b.AppendLine(F("Ammo.FinalFlightPhaseDistToTargetUnity", ap._finalFlightPhaseDistToTargetUnity));
                b.AppendLine(F("Ammo.SeaSkimmingAltUnity", ap._seaSkimmingAltUnity));
                b.AppendLine(F("Ammo.SeaSkimmingStartDistToTargetUnity", ap._seaSkimmingStartDistToTargetUnity));
                b.AppendLine(F("Ammo.TerminalApproachDist", ap._terminalApproachDist));
                b.AppendLine(F("Ammo.SearchForTargetsTime", ap._searchForTargetsTime));
                b.AppendLine(F("Ammo.InitialFlightPhaseDuration", ap._initialFlightPhaseDuration));
                b.AppendLine(F("Ammo.PerformsSlightBallisticInitialFlight", ap._performsSlightBallisticInitialFlight));
                b.AppendLine(F("Ammo.MaxTurnRateDegrees", ap._maxTurnRateDegrees));
                b.AppendLine(F("Ammo.ToBearingTurnRateDegrees", ap._toBearingTurnRateDegrees));
                b.AppendLine(F("Ammo.MaxTurnG", ap._maxTurnG));
                b.AppendLine(F("Ammo.SupportsBanking", ap._supportsBanking));
                b.AppendLine(F("Ammo.BallisticLoft", ap._ballisticLoft));
                b.AppendLine(F("Ammo.TerminalLoft", ap._terminalLoft));
                b.AppendLine(F("Ammo.MaxFlightTime", ap._maxFlightTime));
                b.AppendLine(F("Ammo.MaxGroupSize", ap._maxGroupSize));
                b.AppendLine(F("Ammo.GuidanceType", ap._guidanceType.ToString()));
                b.AppendLine(F("Ammo.MidCourseCorrection", ap._midCourseCorrection.ToString()));
                b.AppendLine(F("Ammo.RequiresTargetToProceed", ap._requiresTargetToProceed));
                b.AppendLine(F("Ammo.SeekerActiveRange", ap._seekerActiveRange));
                b.AppendLine(F("Ammo.SeekerPassiveRange", ap._seekerPassiveRange));
                // Decides whether CurrentTarget can ever be null (WeaponBase.cs:362), and so
                // whether the game ever hands CalculateDrag the round's own altitude instead of the
                // target's. A TargetMemory=True round falls through to its intended target and
                // never enters that window; a False one enters it when its seeker switches on.
                b.AppendLine(F("Ammo.TargetMemory", ap._targetMemory));
                b.AppendLine(F("Ammo.NavigationGain", ap._navigationGain));
                b.AppendLine(F("Ammo.TerrainFollowFlight", ap._terrainFollowFlight));
                b.AppendLine(F("Ammo.TerminalPopUpDist", ap._terminalPopUpDist));
                b.AppendLine(F("Ammo.TerminalPopUpHeight", ap._terminalPopUpHeight));
                b.AppendLine(F("Ammo.TerminalPopUpAngle", ap._terminalPopUpAngle));
                b.AppendLine(F("Ammo.TerminalVerticalTurnRate", ap._terminalVerticalTurnRate));
                b.AppendLine(F("Ammo.SelectivePopUp", ap._selectivePopUp));
                b.AppendLine(F("Ammo.ForcePopUp", ap._forcePopUp));
                // v3: the rest of what Missile.CreateWaypointConfigs reads, so the lab can rebuild
                // the waypoint route from the capture alone.
                b.AppendLine(F("Ammo.TerminalDiveDistance", ap._terminalDiveDistance));
                b.AppendLine(F("Ammo.MaxLoftAltUnity", ap._maxLoftAltUnity));
                b.AppendLine(F("Ammo.GroupLeaderAltUnity", ap._groupLeaderAltUnity));

                b.AppendLine(F("AmmoLabel", i.AmmoLabel));
                b.AppendLine(F("BoostClimbDeg", i.BoostClimbDeg));
                b.AppendLine(F("DecelRate", i.DecelRate));
                b.AppendLine(F("DescentDeg", i.DescentDeg));
                b.AppendLine(F("DescentOnsetDeg", i.DescentOnsetDeg));
                b.AppendLine(F("DragFactor", i.DragFactor));
                b.AppendLine(F("CruiseAlt", i.CruiseAlt));
                b.AppendLine(F("FinalFlightAlt", i.FinalFlightAlt));
                b.AppendLine(F("FinalFlightDist", i.FinalFlightDist));
                b.AppendLine(F("FinalDist", i.FinalDist));
                b.AppendLine(F("FlatDistTotal", i.FlatDistTotal));
                b.AppendLine(F("InitialPhaseDur", i.InitialPhaseDur));
                b.AppendLine(F("PerformsSlightBallisticInitialFlight", i.PerformsSlightBallisticInitialFlight));
                b.AppendLine(F("IsAir", i.IsAir));
                b.AppendLine(F("IsHighBallisticLofter", i.IsHighBallisticLofter));
                b.AppendLine(F("IsTerminalLoft", i.IsTerminalLoft));
                b.AppendLine(F("LaunchPitch", i.LaunchPitch));
                b.AppendLine(F("ConfiguredLoftAlt", i.ConfiguredLoftAlt));
                b.AppendLine(F("LoftAlt", i.LoftAlt));
                b.AppendLine(F("LoftEntryDist", i.LoftEntryDist));
                b.AppendLine(F("LoftSpeedHoldDist", i.LoftSpeedHoldDist));
                // Resolved once at setup, the way LoftSpeedHoldDist is: 0 means no drag-brake
                // window can exist for this ammunition, and a positive value is the range that
                // opens the window on flat distance and closes it on slant.
                b.AppendLine(F("SeekerBrakeRange", i.SeekerBrakeRangeU));
                b.AppendLine(F("LoftVelKn", i.LoftVelKn));
                b.AppendLine(F("BallisticLoft", i.BallisticLoft));
                b.AppendLine(F("LoftClimbTan", i.LoftClimbTan));
                b.AppendLine(F("LoftDescentTan", i.LoftDescentTan));
                b.AppendLine(F("LaunchAltU", i.LaunchAltU));
                b.AppendLine(F("Lofting", i.Lofting));
                b.AppendLine(F("MaxFlight", i.MaxFlight));
                b.AppendLine(F("MaxVelKn", i.MaxVelKn));
                b.AppendLine(F("NonKin", i.NonKin));
                b.AppendLine(F("TargetAlt0", i.TargetAlt0));
                b.AppendLine(F("TargetPos", i.TargetPos));
                b.AppendLine(F("TargetVel", i.TargetVel));
                // A capture that carries these was solved on the rounded target velocity (see
                // FlightTime.TargetMapVelocity). tools/lab score rounds older captures that do not.
                b.AppendLine(F("TargetVelReported", i.TargetVelReported));
                b.AppendLine(F("TargetMoveStep", i.TargetMoveStep));
                b.AppendLine(F("TerminalLinear", i.TerminalLinear));
                b.AppendLine(F("TerminalEndAlt", i.TermAlt));
                b.AppendLine(F("TermAlt", i.TermAlt));
                b.AppendLine(F("TermDist", i.TermDist));
                b.AppendLine(F("TermVelKn", i.TermVelKn));
                b.AppendLine(F("TerminalPopUpEnabled", i.TerminalPopUpEnabled));
                b.AppendLine(F("TerminalPopUpDist", i.TerminalPopUpDist));
                b.AppendLine(F("TerminalPopUpHeight", i.TerminalPopUpHeight));
                b.AppendLine(F("TerminalPopUpAngle", i.TerminalPopUpAngle));
                b.AppendLine(F("TerminalVerticalTurnRate", i.TerminalVerticalTurnRate));
                b.AppendLine(F("TurnRate", i.TurnRate));
                b.AppendLine(F("ToBearingTurnRate", i.ToBearingTurnRate));
                b.AppendLine(F("TurnRateBase", i.TurnRateBase));
                b.AppendLine(F("TurnDerateThresholdKn", i.TurnDerateThresholdKn));
                b.AppendLine(F("AltLatchPhase", i.AltLatchPhase));
                b.AppendLine(F("AltLatched", i.AltLatched));
                b.AppendLine(F("Att", i.Att));
                b.AppendLine(F("LaunchHeading", i.LaunchHeading));
                b.AppendLine(F("NextSample", i.NextSample));
                b.AppendLine(F("Pos", i.Pos));
                b.AppendLine(F("PrevAltErr", i.PrevAltErr));
                b.AppendLine(F("PrevFlat", i.PrevFlat));
                b.AppendLine(F("PrevPitch", i.PrevPitch));
                b.AppendLine(F("T", i.T));
                b.AppendLine(F("TlGliding", i.TlGliding));
                b.AppendLine(F("KappaEndFlat", i.KappaEndFlat));
                b.AppendLine(F("KappaProgress", i.KappaProgress));
                b.AppendLine(F("VelKnots", i.VelKnots));
                // The route as built in game, for comparison with the lab's rebuild. Each leg is
                // endDist/mode/alt/climb/descent/clamp+skip; empty means the older altitude law.
                b.AppendLine(F("WaypointLegs", i.Legs == null ? "" : string.Join(";",
                    System.Array.ConvertAll(i.Legs, l => l.ToString()))));
                b.AppendLine(F("GroupLeader", i.GroupLeader));
                b.AppendLine(F("AltNodes", i.AltNodes));

                string file = Path.Combine(Directory, key + ".solveinput");
                File.WriteAllText(file, b.ToString());
                Bootstrap.Log.LogInfo($"[AutoTOT] solve-dump {label}: wrote {file}");
                return key;
            }
            catch (Exception e)
            {
                ModLog.Warn("solve-input dump failed", e);
                return null;
            }
        }

        /// <summary>
        /// Records what actually happened to a round, beside the input it was solved from. An input
        /// with no outcome is not a test case; this is the half that makes the pair scoreable.
        /// </summary>
        internal static void WriteResult(string key, string ammoLabel, string ammoFingerprint,
                                         string targetName,
                                         float actualFlight, bool arrived, float finalRangeM,
                                         bool retargeted, float motorPerformance,
                                         float minCompression, float maxCompression,
                                         float minFlownStep, float maxFlownStep,
                                         float estAtLaunch, float launchTime,
                                         bool inGroup, bool groupLeader, int maxGroupSize,
                                         float peakSpeedKn, float peakAltU, float lastSpeedKn,
                                         List<string> track,
                                         List<string> flightTrack,
                                         List<string> guardTrack,
                                         float wpEst, float legacyEst,
                                         List<string> stageChanges, List<string> stageEvents,
                                         List<string> groupChanges,
                                         List<string> stepChanges,
                                         float registeredSim, float startupLead, float envelopeLead,
                                         float releaseLead, float impactAtSim, bool wasAnchor,
                                         float targetLengthM, Vector3 targetBoundsM,
                                         float targetCourse0, float targetSpeed0,
                                         float targetCourse1, float targetSpeed1,
                                         float targetMovedU,
                                         bool hasSpeedCommand,
                                         TargetMotionProbe.SpeedCommand speedCommand,
                                         List<string> speedCommandChanges,
                                         string launchCellBlock)
        {
            // A round that finished with no input to pair against is a hole in the corpus, and the
            // only way to notice is to say so. Silence here cost six of seven rounds once.
            if (string.IsNullOrEmpty(key))
            {
                Bootstrap.Log.LogWarning(
                    $"[AutoTOT] solve-result {ammoLabel}: NOT RECORDED, no solver input was dumped " +
                    $"for this round (actual {actualFlight:0.0}s).");
                return;
            }
            try
            {
                System.IO.Directory.CreateDirectory(Directory);

                var b = new StringBuilder();
                b.AppendLine("# AutoTOT flight outcome, paired with the .solveinput of the same name.");
                b.AppendLine(F("Key", key));
                b.AppendLine(F("Label", ammoLabel));
                b.AppendLine(F("CaptureSchema", CaptureSchema));
                b.AppendLine(F("AmmoFingerprint", ammoFingerprint));
                b.AppendLine(F("Target", targetName));
                b.AppendLine(F("ActualFlight", actualFlight));
                b.AppendLine(F("Arrived", arrived));
                b.AppendLine(F("FinalRangeM", finalRangeM));
                b.AppendLine(F("Retargeted", retargeted));
                // The game rolls this per round (Missile.cs:62) and multiplies thrust by it. The
                // estimator cannot know it in advance and correctly assumes 1.0, but a replay can
                // use the recorded value to remove the +/-2% that otherwise swamps kinematic rounds.
                b.AppendLine(F("MotorPerformance", motorPerformance));
                // Above 10x the game enlarges its own physics step, so the round really does fly a
                // different trajectory and the actual time is not comparable.
                b.AppendLine(F("MinCompression", minCompression));
                b.AppendLine(F("MaxCompression", maxCompression));
                // What the round was actually integrated with. A replay at this step is comparing
                // like with like, whatever compression produced it.
                b.AppendLine(F("MinFlownStep", minFlownStep));
                b.AppendLine(F("MaxFlownStep", maxFlownStep));
                b.AppendLine(F("EstAtLaunch", estAtLaunch));
                // Launch and impact, so a salvo can be reconstructed afterwards. Rounds of one
                // group arrive TOGETHER, so a shared impact time is what identifies them, and the
                // launch time is what says which of them waited and for how long.
                b.AppendLine(F("LaunchTime", launchTime));
                b.AppendLine(F("ImpactTime", launchTime + actualFlight));
                // What the round actually did, against what the model says it should have. Without
                // these an entry says only that the estimate was wrong, never where: a flight that
                // is slow because it never reached its top speed and one that is slow because it
                // flew a longer arc look identical from the flight time alone.
                b.AppendLine(F("PeakSpeedKn", peakSpeedKn));
                b.AppendLine(F("PeakAltU", peakAltU));
                b.AppendLine(F("TerminalSpeedKn", lastSpeedKn));
                // "t:altU:speedKn" every few seconds. This is what says whether a round that fell
                // short of its commanded altitude was still climbing when it nosed over, or had
                // levelled off long before.
                b.AppendLine(F("Track", track == null || track.Count == 0
                                        ? "" : string.Join(";", track)));
                // Adaptive-cadence ground truth. Track is intentionally retained for compatibility
                // with old corpus tools; FlightTrack contains the full target-relative state.
                b.AppendLine(F("FlightTrack", flightTrack == null || flightTrack.Count == 0
                                              ? "" : string.Join(";", flightTrack)));
                // Field order is LaunchDiagnostics.GuardTrackFields. Vectors are x,y,z in Unity
                // units; the last field says why the line was written.
                b.AppendLine(F("GuardTrackFields", LaunchDiagnostics.GuardTrackFields));
                b.AppendLine(F("GuardTrack", guardTrack == null || guardTrack.Count == 0
                                             ? "" : string.Join(";", guardTrack)));
                // The other estimator tiers, as they stood at launch. Recording them lets a tier
                // be compared against the others offline, on the whole corpus, without re-flying.
                b.AppendLine(F("WpEstAtLaunch", wpEst));
                b.AppendLine(F("LegacyEstAtLaunch", legacyEst));
                // Where the game changed flight stage. This is the ground truth the model's own
                // phase boundaries have to be scored against.
                b.AppendLine(F("StageChanges", stageChanges == null || stageChanges.Count == 0
                                               ? "" : string.Join(";", stageChanges)));
                b.AppendLine(F("StageEvents", stageEvents == null || stageEvents.Count == 0
                                              ? "" : string.Join(";", stageEvents)));
                b.AppendLine(F("GroupChanges", groupChanges == null || groupChanges.Count == 0
                                               ? "" : string.Join(";", groupChanges)));
                // "t:step" each time the game's physics step changed under this round, starting
                // with the step it launched on. MinFlownStep and MaxFlownStep say only that a
                // change happened; this says when, which separates a frame rate still settling
                // after a mission load from real load at apex.
                b.AppendLine(F("StepChanges", stepChanges == null || stepChanges.Count == 0
                                              ? "" : string.Join(";", stepChanges)));
                // Chain audit B1: what the coordinator predicted this round's order would cost
                // before its first round left, and the impact it was released against. The delay
                // residual is LaunchTime minus RegisteredSim, against StartupLead. Until now those
                // lived only in a log overwritten every launch, so no delay residual survived a
                // session. -1 on every field means the round matched no expectation, which is
                // normal for a round this mod did not order.
                b.AppendLine(F("RegisteredSim", registeredSim));
                b.AppendLine(F("StartupLead", startupLead));
                b.AppendLine(F("EnvelopeLead", envelopeLead));
                b.AppendLine(F("ReleaseLead", releaseLead));
                b.AppendLine(F("ImpactAtSim", impactAtSim));
                b.AppendLine(F("WasAnchor", wasAnchor));
                // The hull the round actually had to reach. The model stops at a fixed radius from
                // the target's centre; the game's arrival is the collider. Recorded so a hull-entry
                // arrival can be tested offline against FinalRangeM instead of re-flown for it.
                // The bounds are world-axis aligned, so they are a hull box only when read with
                // TargetCourseAtEnd above.
                b.AppendLine(F("TargetLengthM", targetLengthM));
                b.AppendLine(F("TargetBoundsM",
                               $"{targetBoundsM.x:0.##},{targetBoundsM.y:0.##},{targetBoundsM.z:0.##}"));
                // Target motion. A target that turned after launch was never predictable from a
                // launch-time snapshot, so the lab can now exclude it rather than counting it as
                // model error.
                b.AppendLine(F("TargetCourseAtLaunch", targetCourse0));
                b.AppendLine(F("TargetSpeedAtLaunch", targetSpeed0));
                b.AppendLine(F("TargetCourseAtEnd", targetCourse1));
                b.AppendLine(F("TargetSpeedAtEnd", targetSpeed1));
                b.AppendLine(F("TargetMovedU", targetMovedU));
                // D3 of max-solo-error-below-1s: the speed a surface target was converging on when
                // the capture was solved, after applyMainThrust's caps, and each change after it as
                // "t:wanted:converge:speed". A solve rounds the speed the ship has at that instant,
                // which holds for the flight only if the ship keeps it. Written for a Vessel only.
                if (hasSpeedCommand)
                {
                    b.AppendLine(F("TargetWantedAtLaunch", speedCommand.Wanted));
                    b.AppendLine(F("TargetConvergeAtLaunch", speedCommand.Converge));
                    b.AppendLine(F("TargetRudderAtLaunch", speedCommand.Rudder));
                    b.AppendLine(F("TargetDampingPerTick", speedCommand.DampingPerTick));
                    b.AppendLine(F("SeaState", speedCommand.SeaState));
                    b.AppendLine(F("TargetSpeedCommandChanges",
                                   speedCommandChanges == null || speedCommandChanges.Count == 0
                                   ? "" : string.Join(";", speedCommandChanges)));
                }
                // D-L of max-solo-error-below-1s: the launcher cell the round spawned in, against every
                // cell the mod can resolve before launch. Preformatted lines, GuidanceStateProbe.
                if (!string.IsNullOrEmpty(launchCellBlock)) b.Append(launchCellBlock);
                b.AppendLine(F("InGroup", inGroup));
                b.AppendLine(F("GroupLeader", groupLeader));
                b.AppendLine(F("MaxGroupSize", maxGroupSize));
                // Provenance. A corpus outlives the build that produced it, so an entry has to say
                // what it was captured against or a later disagreement is unattributable.
                b.AppendLine(F("GameVersion", Application.version));
                b.AppendLine(F("GameBranch", FlightTime.SimIsBeta ? "beta" : "public"));
                b.AppendLine(F("ModVersion", Bootstrap.Version));
                b.AppendLine(F("ModBuild", ModBuildId));
                b.AppendLine(F("CapturedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv)));
                // One input per round now, so this can no longer be ambiguous. Still written, so a
                // corpus mixing old and new captures reads uniformly.
                b.AppendLine(F("Claims", 1));

                string file = Path.Combine(Directory, key + ".result");
                File.WriteAllText(file, b.ToString());
                Bootstrap.Log.LogInfo(
                    $"[AutoTOT] solve-result {ammoLabel}: actual {actualFlight:0.0}s, " +
                    $"motor {motorPerformance:0.000}, compression {minCompression:0}-{maxCompression:0}x, " +
                    $"step {minFlownStep:0.0000}-{maxFlownStep:0.0000}s " +
                    $"-> {Path.GetFileName(file)}");
            }
            catch (Exception e)
            {
                ModLog.Warn("solve-result write failed", e);
            }
        }

        // "R" round-trips the float exactly, so a replay starts from the same bits the game solved
        // with rather than from a printed approximation.
        private static string F(string k, float v) => k + " = " + v.ToString("R", Inv);
        private static string F(string k, int v) => k + " = " + v.ToString(Inv);
        private static string F(string k, bool v) => k + " = " + (v ? "true" : "false");
        private static string F(string k, string v) => k + " = " + (v ?? string.Empty);

        private static string F(string k, Vector3 v)
            => k + " = " + v.x.ToString("R", Inv) + "," + v.y.ToString("R", Inv) + "," +
               v.z.ToString("R", Inv);

        private static string F(string k, Quaternion q)
            => k + " = " + q.x.ToString("R", Inv) + "," + q.y.ToString("R", Inv) + "," +
               q.z.ToString("R", Inv) + "," + q.w.ToString("R", Inv);

        /// <summary>A stable fingerprint of the raw ammunition fields that influence either flight
        /// model. It is deliberately data-derived rather than based on the ammunition name, so two
        /// versions of a modded weapon cannot silently occupy the same training cohort.</summary>
        internal static string AmmunitionFingerprint(AmmunitionParameters ap)
        {
            if (ap == null) return string.Empty;
            var b = new StringBuilder();
            b.AppendLine(F("MinVelocity", ap.MinVelocity));
            b.AppendLine(F("LiftFactor", ap.LiftFactor));
            b.AppendLine(F("Acceleration", ap._acceleration));
            b.AppendLine(F("AccelerationTime", ap._accelerationTime));
            b.AppendLine(F("SustainerBurnAcceleration", ap._sustainerBurnAcceleration));
            b.AppendLine(F("SustainerBurnTime", ap._sustainerBurnTime));
            b.AppendLine(F("MaxVelocity", ap._maxVelocityInKnots));
            b.AppendLine(F("Kinematics", ap.Kinematics.ToString()));
            b.AppendLine(F("OptimizeLoftAlt", ap._optimizeLoftAlt));
            b.AppendLine(F("TerminalAlt", ap._terminalAltUnity));
            b.AppendLine(F("TerminalAltMode", ap._terminalAltMode.ToString()));
            b.AppendLine(F("LoftToSkim", ap._loftToSkim));
            b.AppendLine(F("TargetType", ap._targetType.ToString()));
            b.AppendLine(F("Deceleration", ap._deceleration));
            b.AppendLine(F("MaxLoftVelocity", ap._maxLoftVelocityInKnots));
            b.AppendLine(F("TerminalVelocity", ap._terminalVelocityInKnots));
            b.AppendLine(F("MaxLoftAngle", ap._maxLoftAngle));
            b.AppendLine(F("FinalMaxAngle", ap._finalFlightPhaseMaxAngle));
            b.AppendLine(F("SeaSkimMaxDescent", ap._seaSkimmingMaxDescentAngle));
            b.AppendLine(F("FinalAlt", ap._finalFlightPhaseAltUnity));
            b.AppendLine(F("FinalDist", ap._finalFlightPhaseDistToTargetUnity));
            b.AppendLine(F("SeaSkimAlt", ap._seaSkimmingAltUnity));
            b.AppendLine(F("SeaSkimDist", ap._seaSkimmingStartDistToTargetUnity));
            b.AppendLine(F("TerminalDist", ap._terminalApproachDist));
            b.AppendLine(F("SearchTime", ap._searchForTargetsTime));
            b.AppendLine(F("InitialDuration", ap._initialFlightPhaseDuration));
            b.AppendLine(F("SlightBallisticInitial", ap._performsSlightBallisticInitialFlight));
            b.AppendLine(F("TurnRate", ap._maxTurnRateDegrees));
            b.AppendLine(F("BearingTurnRate", ap._toBearingTurnRateDegrees));
            b.AppendLine(F("MaxTurnG", ap._maxTurnG));
            b.AppendLine(F("SupportsBanking", ap._supportsBanking));
            b.AppendLine(F("BallisticLoft", ap._ballisticLoft));
            b.AppendLine(F("TerminalLoft", ap._terminalLoft));
            b.AppendLine(F("MaxFlight", ap._maxFlightTime));
            b.AppendLine(F("MaxGroupSize", ap._maxGroupSize));
            b.AppendLine(F("GuidanceType", ap._guidanceType.ToString()));
            b.AppendLine(F("MidCourseCorrection", ap._midCourseCorrection.ToString()));
            b.AppendLine(F("RequiresTargetToProceed", ap._requiresTargetToProceed));
            b.AppendLine(F("SeekerActiveRange", ap._seekerActiveRange));
            b.AppendLine(F("SeekerPassiveRange", ap._seekerPassiveRange));
            b.AppendLine(F("NavigationGain", ap._navigationGain));
            b.AppendLine(F("TerrainFollowFlight", ap._terrainFollowFlight));
            b.AppendLine(F("TerminalPopUpDist", ap._terminalPopUpDist));
            b.AppendLine(F("TerminalPopUpHeight", ap._terminalPopUpHeight));
            b.AppendLine(F("TerminalPopUpAngle", ap._terminalPopUpAngle));
            b.AppendLine(F("TerminalVerticalTurnRate", ap._terminalVerticalTurnRate));
            b.AppendLine(F("SelectivePopUp", ap._selectivePopUp));
            b.AppendLine(F("ForcePopUp", ap._forcePopUp));

            // FNV-1a is compact, deterministic on the game's older runtime, and sufficient as a
            // change detector. This is provenance, not a security boundary.
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string value = b.ToString();
                for (int n = 0; n < value.Length; n++)
                {
                    hash ^= value[n];
                    hash *= 1099511628211UL;
                }
                return "fnv1a64:" + hash.ToString("x16", Inv);
            }
        }

        private static string F(string k, Vector2[] nodes)
        {
            if (nodes == null || nodes.Length == 0) return k + " = ";
            var b = new StringBuilder(k).Append(" = ");
            for (int n = 0; n < nodes.Length; n++)
            {
                if (n > 0) b.Append(';');
                b.Append(nodes[n].x.ToString("R", Inv)).Append(',')
                 .Append(nodes[n].y.ToString("R", Inv));
            }
            return b.ToString();
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var b = new StringBuilder(s.Length);
            foreach (char c in s) b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return b.ToString();
        }
    }
}
