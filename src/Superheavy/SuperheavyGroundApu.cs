using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyGroundApu : MonoBehaviour
    {
        private const float RatedRpm = 2500f;
        private const float FuelRate = .025f;
        private Aircraft aircraft;
        private PowerSupply power;
        private Generator generator;
        private ManualLogSource log;
        private float fuelSeconds;
        private bool active;

        internal bool Running => generator != null && generator.Rpm > RatedRpm * .1f;

        internal static void Attach(Aircraft aircraft, ManualLogSource log)
        {
            PowerSupply power = aircraft.GetPowerSupply();
            if (power == null || aircraft.TryGetComponent<SuperheavyGroundApu>(out _))
                return;
            var apu = aircraft.gameObject.AddComponent<SuperheavyGroundApu>();
            apu.aircraft = aircraft;
            apu.power = power;
            apu.log = log;
        }

        private void FixedUpdate()
        {
            if (power == null || aircraft == null || !aircraft.LocalSim)
                return;
            if (generator == null)
            {
                ref IEngine[] sources = ref PowerSources.Get(power);
                if (sources == null)
                    return;
                generator = new Generator(aircraft.transform);
                Array.Resize(ref sources, sources.Length + 1);
                sources[sources.Length - 1] = generator;
            }

            float dt = Time.fixedDeltaTime;
            bool groundPower = !aircraft.disabled && !aircraft.HasEjected() && !aircraft.Ignition &&
                aircraft.GetFuelQuantity() > 15f && SuperheavyCrewAccess.StationaryOnGround(aircraft);
            if (groundPower != active)
            {
                active = groundPower;
                log.LogInfo(active ? "Superheavy ground APU started." : "Superheavy ground APU shut down.");
            }
            if (!groundPower)
            {
                generator.Rpm = 0f;
                return;
            }
            fuelSeconds += dt;
            if (fuelSeconds >= 1f)
            {
                aircraft.UseFuel(FuelRate * fuelSeconds);
                fuelSeconds = 0f;
            }
            generator.Rpm = Mathf.MoveTowards(generator.Rpm, RatedRpm, RatedRpm * dt / 3f);
            if (power.GetCharge() < .995f)
                power.enabled = true;
        }

        private static class PowerSources
        {
            internal static readonly AccessTools.FieldRef<PowerSupply, IEngine[]> Get =
                AccessTools.FieldRefAccess<PowerSupply, IEngine[]>("engineInterfaces");
        }

        // Only the electrical bus sees the generator. Flight propulsion and
        // native engine detection continue to use the existing thrust banks.
        private sealed class Generator : IEngine
        {
            internal float Rpm;
            public Transform transform { get; }

            internal Generator(Transform location) { transform = location; }

            public event Action OnEngineDisable { add { } remove { } }
            public event Action OnEngineDamage { add { } remove { } }
            public float GetRPM() => Rpm;
            public float GetRPMRatio() => Rpm / RatedRpm;
            public float GetThrust() => 0f;
            public float GetMaxThrust() => 0f;
            public void SetInteriorSounds(bool useInteriorSound) { }
        }
    }
}
