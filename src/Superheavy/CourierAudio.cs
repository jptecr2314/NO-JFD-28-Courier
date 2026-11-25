using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner request 24/11/2026 ("verifica como tá a questão dos sons", then "Sim, corrige"). Audit flight-audio1-20261124:
    //  1. every Courier part had hitSound = null, so AeroPart.ApplyDamage played nothing on a hit (stock: bulletImpact1);
    //  2. the eight Turbofans had failureMessageAudio = null, so KillEngine gave no warning (stock: left/rightEngineFire);
    //  3. the cargo ramp (CargoRamp SHGT_CargoReleaseGate) and the bomb-bay gate (BayDoor SHGT_BombBayGate*) had no
    //     source and no clips, while six BayDoors inherited from the donor played weaponBayOpen/Close from the belly
    //     and from inside the two inner engines.
    // Clips are the game's own, taken from the loaded stock prefabs; nothing is shipped.
    internal static class CourierAudio
    {
        private static T Clip<T>(string name) where T : UnityEngine.Object =>
            Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(c => c != null && c.name == name);

        private static void CopySettings(AudioSource from, AudioSource to)
        {
            to.outputAudioMixerGroup = from.outputAudioMixerGroup; to.spatialBlend = from.spatialBlend; to.rolloffMode = from.rolloffMode;
            to.minDistance = from.minDistance; to.maxDistance = from.maxDistance; to.volume = from.volume; to.priority = from.priority;
            to.dopplerLevel = from.dopplerLevel; to.spread = from.spread; to.loop = false; to.playOnAwake = false;
            if (from.rolloffMode == AudioRolloffMode.Custom) to.SetCustomCurve(AudioSourceCurveType.CustomRolloff, from.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
        }

        private static void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        private static object Get(object target, string field) => AccessTools.Field(target.GetType(), field).GetValue(target);

        internal static string Apply(Transform aircraft, IList<UnitPart> parts)
        {
            int hits = 0, warnings = 0, doors = 0; string ramp = "none";
            // 1. impact sound
            var impact = Clip<AudioClip>("bulletImpact1");
            if (impact != null)
                foreach (var p in parts.Where(p => p != null))
                    if (Get(p, "hitSound") == null) { Set(p, "hitSound", impact); hits++; }
            // 2. engine-fire warning by side (aircraft-local x > 0 is the right wing)
            AudioClip left = Clip<AudioClip>("leftEngineFire"), right = Clip<AudioClip>("rightEngineFire");
            foreach (var engine in aircraft.GetComponentsInChildren<Turbofan>(true).Concat(parts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<Turbofan>(true))).Distinct())
            {
                if (Get(engine, "failureMessageAudio") != null) continue;
                var clip = aircraft.InverseTransformPoint(engine.transform.position).x > 0f ? right : left;
                if (clip != null) { Set(engine, "failureMessageAudio", clip); warnings++; }
            }
            // 3. bomb bay: one source at the gate shared by every BayDoor (stock pattern: baydoor_L and _LL share
            // baydoor_LL), so the opening is heard once, from the bay, never from the engines.
            var allDoors = aircraft.GetComponentsInChildren<BayDoor>(true).Concat(parts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<BayDoor>(true))).Distinct().Where(d => !(d is CargoRamp)).ToArray();
            var gate = allDoors.FirstOrDefault(d => d.name.StartsWith("SHGT_BombBayGate", StringComparison.Ordinal));
            var template = allDoors.Select(d => Get(d, "doorAudioSource") as AudioSource).FirstOrDefault(s => s != null);
            var open = allDoors.Select(d => Get(d, "openStartSound") as AudioClip).FirstOrDefault(c => c != null) ?? Clip<AudioClip>("weaponBayOpen050s");
            var close = allDoors.Select(d => Get(d, "closeStartSound") as AudioClip).FirstOrDefault(c => c != null) ?? Clip<AudioClip>("weaponBayClose050s");
            if (gate != null && open != null && close != null)
            {
                var bay = new GameObject("SHGT_BayAudio").AddComponent<AudioSource>();
                // the gate object sits at the aircraft origin (flight-audio2-20261124); place the source in the drop opening
                bay.transform.SetParent(gate.transform, false); bay.transform.position = aircraft.TransformPoint(new Vector3(.35f, 2.9f, -6.05f));
                if (template != null) CopySettings(template, bay); else { bay.spatialBlend = 1f; bay.minDistance = 5f; bay.maxDistance = 120f; bay.playOnAwake = false; }
                foreach (var d in allDoors)
                {
                    Set(d, "doorAudioSource", bay); Set(d, "openStartSound", open); Set(d, "closeStartSound", close); doors++;
                }
            }
            // 4. cargo ramp: the large stock ramp set (tilt-rotor), with its source settings
            var cargo = aircraft.GetComponentsInChildren<CargoRamp>(true).FirstOrDefault(r => r.name == "SHGT_CargoReleaseGate");
            if (cargo != null && Get(cargo, "doorAudioSource") == null)
            {
                var stock = Resources.FindObjectsOfTypeAll<CargoRamp>().Where(r => r != null && !r.gameObject.scene.IsValid() && Get(r, "openStartSound") != null)
                    .OrderByDescending(r => ((AudioClip)Get(r, "openStartSound")).name.Contains("Big")).FirstOrDefault();
                if (stock != null)
                {
                    var src = new GameObject("SHGT_RampAudio").AddComponent<AudioSource>();
                    src.transform.SetParent(cargo.transform, false);
                    // centre of the ramp skin when present (exterior ramp mesh SHGT_CargoMesh_011), else the gate origin
                    var skin = aircraft.GetComponentsInChildren<Renderer>(true).Concat(parts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<Renderer>(true))).FirstOrDefault(r => r != null && r.name == "SHGT_CargoMesh_011");
                    if (skin != null) src.transform.position = skin.bounds.center;
                    var stockSource = Get(stock, "doorAudioSource") as AudioSource;
                    if (stockSource != null) CopySettings(stockSource, src); else if (template != null) CopySettings(template, src);
                    Set(cargo, "doorAudioSource", src);
                    foreach (var f in new[] { "openStartSound", "closeStartSound", "openStopSound", "closeStopSound" }) Set(cargo, f, Get(stock, f));
                    var at = aircraft.InverseTransformPoint(src.transform.position);
                    ramp = $"{((AudioClip)Get(stock, "openStartSound")).name} from {stock.name} at ({at.x:F1},{at.y:F1},{at.z:F1})";
                }
            }
            string brake = Airbrakes(aircraft, parts);
            int spread = CourierEngineAudioSpread.Attach(aircraft, parts);
            return $"impact {hits} parts, engine-fire warning {warnings} engines, bay doors {doors} on one gate source, ramp {ramp}, airbrake {brake}, engine loops spread {spread}";
        }

        // Owner request 25/11/2026 ("corrige também o som dos freios aerodinâmicos"). Airbrake.Update drives airbrakeSound
        // (volume sqrt(open * speed factor) * volumeMultiplier, pitch from speed, Play/Stop) and skips it when null; the
        // eight Courier spoilers all had it null. Stock pattern (Fighter1, VTOLTrainer1, trainer, SmallFighter1): one
        // source driven by one Airbrake, the others silent. The eight spoilers open together (throttle 0), so one source at
        // their centroid, on the fuselage, driven by an inboard spoiler; two sources would play the same loop in phase.
        private static string Airbrakes(Transform aircraft, IList<UnitPart> parts)
        {
            var brakes = aircraft.GetComponentsInChildren<Airbrake>(true).Concat(parts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<Airbrake>(true))).Distinct().ToArray();
            if (brakes.Length == 0) return "none";
            if (brakes.Any(b => Get(b, "airbrakeSound") is AudioSource s && s != null)) return "already set";
            var stock = Resources.FindObjectsOfTypeAll<Airbrake>()
                .Where(b => b != null && !b.gameObject.scene.IsValid() && Get(b, "airbrakeSound") is AudioSource s && s != null && s.clip != null && s.clip.name == "airbrake")
                .OrderByDescending(b => ((AudioSource)Get(b, "airbrakeSound")).maxDistance).FirstOrDefault();
            if (stock == null) return "no stock template";
            var stockSource = (AudioSource)Get(stock, "airbrakeSound");
            var driver = brakes.FirstOrDefault(b => b.name.StartsWith("SHGT_Control_Airbrake_02", StringComparison.Ordinal)) ?? brakes[0];
            var src = new GameObject("SHGT_AirbrakeAudio").AddComponent<AudioSource>();
            src.transform.SetParent(aircraft, false);
            var centre = Vector3.zero; foreach (var b in brakes) centre += b.transform.position;
            src.transform.position = centre / brakes.Length;
            CopySettings(stockSource, src); src.clip = stockSource.clip; src.loop = true; src.volume = 0f;
            Set(driver, "airbrakeSound", src);
            foreach (var f in new[] { "volumeMultiplier", "maxVolumeSpeed", "pitchMin", "pitchMax" }) Set(driver, f, Get(stock, f));
            var unit = aircraft.GetComponent<Unit>();
            // Airbrake.Update reads aircraft.speed from its own serialized field; set it if the clone left it empty
            if (Get(driver, "aircraft") == null && unit is Aircraft plane) Set(driver, "aircraft", plane);
            // follow the stock prefab on the cockpit 2D/3D switch (Unit.SetDoppler)
            var stockUnit = stock.GetComponentInParent<Unit>(true);
            var stockList = stockUnit == null ? null : AccessTools.Field(typeof(Unit), "dopplerSounds").GetValue(stockUnit) as System.Collections.IList;
            bool doppler = stockList != null && stockList.Contains(stockSource);
            if (doppler && unit != null)
            {
                unit.RegisterDopplerSound(src);
                var registered = AccessTools.Field(typeof(Unit), "dopplerSounds").GetValue(unit) as System.Collections.IList;
                var reference = registered?.OfType<AudioSource>().FirstOrDefault(s => s != null && s != src);
                if (reference != null) { src.spatialBlend = reference.spatialBlend; src.dopplerLevel = reference.dopplerLevel; }
            }
            var at = aircraft.InverseTransformPoint(src.transform.position);
            return $"{brakes.Length} spoilers on one source from {stockUnit?.name ?? stock.name} driven by {driver.name}, mult {Get(stock, "volumeMultiplier")}, doppler list {doppler}, at ({at.x:F1},{at.y:F1},{at.z:F1})";
        }
    }

    // Owner request 24/11/2026 ("corrige também os motores empilhados"). Turbofan.Animate starts turbineAudio from 0
    // when RPM rises and drives every engine's pitch from the same RPM; JetNozzle.Awake starts thrustAudio at 0 or 1 s
    // (Random.Range(0,2) int). The eight engines therefore play the same loops in phase and the sound piles up (comb
    // filtering, about +18 dB coherent sum). Each loop gets its own start offset whenever it (re)starts, and every
    // engine source's volume is scaled by 1/sqrt(2): two engines per pod sum to the power of one stock engine,
    // so the eight read as loud as the stock four-engine bomber instead of twice as loud.
    internal sealed class CourierEngineAudioSpread : MonoBehaviour
    {
        private const float PodScale = .7071f;
        private AudioSource[] sources;
        private float[] phase;
        private bool[] wasPlaying;

        internal static int Attach(Transform aircraft, IList<UnitPart> parts)
        {
            var roots = new[] { aircraft.gameObject }.Concat(parts.Where(p => p != null).Select(p => p.gameObject)).Distinct().ToArray();
            var turbines = roots.SelectMany(r => r.GetComponentsInChildren<Turbofan>(true)).Distinct().OrderBy(e => e.name, StringComparer.Ordinal)
                .Select(e => AccessTools.Field(typeof(Turbofan), "turbineAudio").GetValue(e) as AudioSource);
            var nozzles = roots.SelectMany(r => r.GetComponentsInChildren<JetNozzle>(true)).Distinct().OrderBy(n => n.name, StringComparer.Ordinal)
                .Select(n => AccessTools.Field(typeof(JetNozzle), "thrustAudio").GetValue(n) as AudioSource);
            var list = turbines.Concat(nozzles).Where(s => s != null && s.clip != null).Distinct().ToArray();
            if (list.Length == 0) return 0;
            // Owner request 24/11/2026 ("corrige também o 2D/3D dos motores na cabine"). Unit.SetDoppler switches the
            // registered dopplerSounds to 2D/no doppler in the cockpit and back to 3D outside. The Courier's list came
            // from the donor and only held engine_R/engine_L and their nozzles (flight-audio1: those four 2D in the
            // cockpit, the other twelve 3D). Register every engine source and copy the current state to the new ones.
            var unit = aircraft.GetComponent<Unit>();
            var registered = unit == null ? null : AccessTools.Field(typeof(Unit), "dopplerSounds").GetValue(unit) as System.Collections.IList;
            if (registered != null)
            {
                var reference = registered.OfType<AudioSource>().FirstOrDefault(s => s != null && list.Contains(s));
                foreach (var s in list.Where(s => !registered.Contains(s)))
                {
                    unit.RegisterDopplerSound(s);
                    if (reference != null) { s.spatialBlend = reference.spatialBlend; s.dopplerLevel = reference.dopplerLevel; }
                }
            }
            var c = aircraft.gameObject.GetComponent<CourierEngineAudioSpread>() ?? aircraft.gameObject.AddComponent<CourierEngineAudioSpread>();
            c.sources = list; c.phase = new float[list.Length]; c.wasPlaying = new bool[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                c.phase[i] = (i * .6180340f + .137f) % 1f; // golden-ratio spacing: no two loops share a start point
            }
            // Level scale on the volume itself, not the distance curve: in the cockpit SetDoppler makes these 2D, and a
            // 2D source ignores the rolloff curve. Turbofan never writes turbineAudio.volume; JetNozzle.AudioEffects
            // rewrites thrustAudio.volume every frame from thrustMaxVolume, so that field is scaled instead.
            foreach (var e in roots.SelectMany(r => r.GetComponentsInChildren<Turbofan>(true)).Distinct())
                if (AccessTools.Field(typeof(Turbofan), "turbineAudio").GetValue(e) is AudioSource ts && ts != null) ts.volume *= PodScale;
            var maxVolume = AccessTools.Field(typeof(JetNozzle), "thrustMaxVolume");
            foreach (var n in roots.SelectMany(r => r.GetComponentsInChildren<JetNozzle>(true)).Distinct())
                maxVolume.SetValue(n, (float)maxVolume.GetValue(n) * PodScale);
            return list.Length;
        }

        private void LateUpdate()
        {
            if (sources == null) return;
            for (int i = 0; i < sources.Length; i++)
            {
                var s = sources[i]; if (s == null) continue;
                bool playing = s.isPlaying;
                if (playing && !wasPlaying[i] && s.clip != null) s.time = Mathf.Min(phase[i] * s.clip.length, s.clip.length - .05f);
                wasPlaying[i] = playing;
            }
        }
    }
}
