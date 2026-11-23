using System.Linq;
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyCrewAccess : MonoBehaviour
    {
        private const string RigName = "SHGT_CrewAccessRig";
        private const string ExitName = "SHGT_CrewAccess_ExitGround";
        private const float OpenSeconds = 2.25f;
        private const float InteriorOpenSeconds = .75f;
        private static ConfigEntry<KeyboardShortcut> interiorDoorShortcut;
        private static ManualLogSource log;

        private struct Pose
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        private sealed class Track
        {
            internal Transform Pivot;
            internal Pose[] Samples;
        }

        private Aircraft aircraft;
        private Transform exit;
        private Track[] tracks;
        private Track[] interiorTracks;
        private float progress;
        private float interiorProgress;
        private bool preparing;
        private bool opening;
        private bool interiorRequested;
        private bool completingRequest;

        internal bool UsingCrewExit { get; private set; }

        internal static void Configure(ConfigFile config, ManualLogSource logger)
        {
            log = logger;
            interiorDoorShortcut = config.Bind("Crew", "Toggle interior doors",
                new KeyboardShortcut(KeyCode.F7, KeyCode.LeftControl),
                "Open or close the JFD-28 flight deck and cargo bulkhead doors when parked. Ground dismount opens them automatically.");
        }

        internal static bool StationaryOnGround(Aircraft aircraft)
        {
            if (aircraft.rb == null || aircraft.gearState == LandingGear.GearState.LockedRetracted ||
                SuperheavyGroundPhysicsPlugin.HorizontalSpeed(aircraft.rb) > .65f ||
                Mathf.Abs(aircraft.rb.velocity.y) > .6f)
                return false;
            return aircraft.TryGetComponent<GroundPhysicsProbe>(out var probe) && probe.NativeRadarClearance() <= .2f;
        }

        internal static bool TryGet(Aircraft aircraft, out SuperheavyCrewAccess access)
        {
            if (aircraft.TryGetComponent(out access))
                return access.tracks != null;

            Transform rig = aircraft.transform.Find(RigName);
            if (rig == null)
                return false;
            Transform exit = rig.Find(ExitName);
            if (exit == null)
                return false;

            var tracks = new List<Track>();
            var interiorTracks = new List<Track>();
            foreach (Transform frames in rig)
            {
                if (!frames.name.StartsWith("Frames_", StringComparison.Ordinal))
                    continue;
                string pivotName="SHGT_CrewAccess_Pivot_"+frames.name.Substring("Frames_".Length);
                Transform pivot = SuperheavyNativeDamage.Components<Transform>(aircraft).FirstOrDefault(t=>t.name==pivotName);
                if (pivot == null)
                    continue;
                var samples = new List<Pose>();
                foreach (Transform frame in frames)
                {
                    if (!frame.name.StartsWith("Frame_", StringComparison.Ordinal))
                        continue;
                    samples.Add(new Pose
                    {
                        Position = pivot.parent.InverseTransformPoint(frame.position),
                        Rotation = Quaternion.Inverse(pivot.parent.rotation) * frame.rotation
                    });
                }
                if (samples.Count < 2)
                    continue;
                var track = new Track { Pivot = pivot, Samples = samples.ToArray() };
                if (frames.name is "Frames_FlightDeckL" or "Frames_FlightDeckR" or "Frames_HoldR")
                    interiorTracks.Add(track);
                else
                    tracks.Add(track);
            }
            if (tracks.Count == 0)
                return false;

            if (interiorTracks.Count != 0 && interiorTracks.Count != 3)
            {
                log?.LogWarning($"Superheavy interior doors require three tracks; found {interiorTracks.Count}.");
                interiorTracks.Clear();
            }

            access = aircraft.gameObject.AddComponent<SuperheavyCrewAccess>();
            access.aircraft = aircraft;
            access.exit = exit;
            access.tracks = tracks.ToArray();
            access.interiorTracks = interiorTracks.ToArray();
            log?.LogInfo($"Superheavy crew access bound {tracks.Count} boarding and {interiorTracks.Count} interior door tracks.");
            return true;
        }

        internal bool RequestExit()
        {
            if (completingRequest)
                return true;
            if (!preparing)
            {
                preparing = true;
                opening = true;
                SceneSingleton<AircraftActionsReport>.i?.ReportText("Opening crew door and stairs", 2f);
            }
            return false;
        }

        internal void OpenForNativeDismount()
        {
            UsingCrewExit = true;
            // The regular player path pre-opens the stairs. Keep the native
            // callback safe for any stationary dismount that did not request it.
            progress = 1f;
            opening = true;
            ApplyPose(tracks, progress);
            SetInteriorProgress(1f);
        }

        internal Vector3 DismountRayStart(int crewIndex)
        {
            // Give the stock downward placement ray separate spots at the foot
            // of the stairs. Preserve normal ground collision and pilot logic.
            return exit.position + aircraft.transform.right * (-.45f * crewIndex) +
                   aircraft.transform.forward * (.8f * crewIndex) + Vector3.up * 1.8f;
        }

        private void Update()
        {
            if (tracks == null || aircraft == null)
                return;
            if (preparing && !StationaryOnGround(aircraft))
            {
                preparing = false;
                opening = false;
                SceneSingleton<AircraftActionsReport>.i?.ReportText("Crew exit cancelled: aircraft moving", 3f);
            }
            if (!UsingCrewExit && aircraft.HasEjected() && !completingRequest)
            {
                preparing = false;
                opening = false;
                interiorRequested = false;
            }

            UpdateInteriorDoors();
            float next = Mathf.MoveTowards(progress, opening ? 1f : 0f, Time.deltaTime / OpenSeconds);
            if (next != progress)
            {
                progress = next;
                ApplyPose(tracks, progress);
            }
            if (!preparing || progress < 1f)
                return;
            preparing = false;
            completingRequest = true;
            try { aircraft.StartEjectionSequence(); }
            finally { completingRequest = false; }
        }

        private void UpdateInteriorDoors()
        {
            if (interiorTracks.Length == 0)
                return;

            if (interiorRequested && !StationaryOnGround(aircraft))
                interiorRequested = false;
            if (!preparing && !aircraft.disabled && !aircraft.HasEjected() && Time.timeScale > 0f &&
                interiorDoorShortcut != null && interiorDoorShortcut.Value.IsDown() && GameManager.IsLocalAircraft(aircraft))
            {
                if (StationaryOnGround(aircraft))
                {
                    interiorRequested = !interiorRequested;
                    SceneSingleton<AircraftActionsReport>.i?.ReportText(
                        interiorRequested ? "Opening interior crew doors" : "Closing interior crew doors", 2f);
                }
                else
                    SceneSingleton<AircraftActionsReport>.i?.ReportText("Interior crew doors require the aircraft to be parked", 3f);
            }

            float target = opening || interiorRequested ? 1f : 0f;
            SetInteriorProgress(Mathf.MoveTowards(interiorProgress, target, Time.deltaTime / InteriorOpenSeconds));
        }

        private void SetInteriorProgress(float next)
        {
            if (interiorTracks.Length == 0 || next == interiorProgress)
                return;
            interiorProgress = next;
            ApplyPose(interiorTracks, interiorProgress);
            if (next == 1f || next == 0f)
                log?.LogInfo(next == 1f ? "Superheavy interior crew doors opened." : "Superheavy interior crew doors closed.");
        }

        private static void ApplyPose(Track[] tracks, float progress)
        {
            foreach (Track track in tracks)
            {
                if (track.Pivot == null)
                    continue;
                float sample = progress * (track.Samples.Length - 1);
                int index = Mathf.Min((int)sample, track.Samples.Length - 2);
                float blend = sample - index;
                Pose a = track.Samples[index];
                Pose b = track.Samples[index + 1];
                track.Pivot.SetLocalPositionAndRotation(Vector3.Lerp(a.Position, b.Position, blend),
                    Quaternion.Slerp(a.Rotation, b.Rotation, blend));
            }
        }
    }
}
