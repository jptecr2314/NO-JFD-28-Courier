using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyAirdrop : MonoBehaviour
    {
        internal const float SeparationSeconds = 1f;
        private const float Gravity = 9.81f;
        private const float ExhaustVelocity = 4500f;
        private const float ForecastStep = .06f;
        private const float MinimumReserve = .12f;
        private static readonly ConditionalWeakTable<Aircraft, Operation> Operations =
            new ConditionalWeakTable<Aircraft, Operation>();
        private static ManualLogSource log;
        private static Harmony patcher;
        private static bool oldRetroPatched;

        private sealed class Operation
        {
            internal MountedCargo CommittedMount;
            internal Plan Committed;
            internal float CommittedUntil;
            internal GlobalPosition LastTouchdown;
            internal float LastDrop;
        }

        internal struct Profile
        {
            internal float VehicleMass, Dry, Fuel, PeakForce, SideAcceleration;
            internal float HalfLength, HalfWidth, Offset, Drag;

            internal static Profile ForMass(float mass)
            {
                bool light = mass < 9000f;
                bool heavy = mass > 34000f;
                float dry = light ? Mathf.Max(90f, mass * .035f) : heavy ? Mathf.Max(950f, mass * .022f) : mass * .03f;
                float fuel = mass * (heavy ? .11f : .105f);
                return new Profile
                {
                    VehicleMass = mass, Dry = dry, Fuel = fuel,
                    PeakForce = (mass + dry + fuel) * (heavy ? 32f : light ? 28f : 30f),
                    SideAcceleration = heavy ? 18f : 20f
                };
            }
        }

        private struct Plan
        {
            internal bool Retro;
            internal GlobalPosition Touchdown;
            internal Vector3 Heading;
            internal Profile Profile;
        }

        private struct ForecastResult
        {
            internal Vector3 Point;
            internal float Seconds, FuelFraction;
        }

        private GroundVehicle vehicle;
        private Aircraft carrier;
        private Rigidbody body;
        private CargoDeploymentSystem deployment;
        private Plan plan;
        private float age, fuel, landedFor, initialMass, nextZoneCheck;
        private bool powered, finished;
        internal bool Finished => finished;
        private GameObject hardware;
        private Transform[] bells;

        internal static void Install(Harmony harmony, ManualLogSource logger)
        {
            log = logger;
            SuperheavyCargoGroundAI.Configure(logger);
            patcher = harmony;
            harmony.CreateClassProcessor(typeof(DeploymentPatch)).Patch();
            harmony.CreateClassProcessor(typeof(RetroVersionGuard)).Patch();
        }

        // Older Retro Kit releases install a prefix which bypasses this
        // planner. Run first and refuse only marked loads on that combination,
        // rather than silently reverting to the old unguided handoff.
        [HarmonyPatch(typeof(SuperheavyAirdrop),nameof(TryArm))]
        private static class RetroVersionGuard
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(MountedCargo __1,ref string __5,ref bool __result)
            {
                if(!SuperheavyRetroBridge.IsRetro(__1)||SuperheavyRetroBridge.Ready)return true;
                __5="AIRDROP — atualize Cargo Retro Kit para 0.3.1.";__result=false;return false;
            }
        }

        internal static bool TryArm(Aircraft aircraft, MountedCargo cargo, Vector3 releaseWorld,
            Vector3 releaseVelocity, float delay, out string reason)
        {
            var operation = Operations.GetOrCreateValue(aircraft);
            if (operation.CommittedMount != null && Time.time < operation.CommittedUntil)
            {
                reason = "Aguarde a carga sair da rampa.";
                return false;
            }
            if (!TryPlan(aircraft, cargo, releaseWorld, releaseVelocity, delay, operation, out var plan, out reason))
                return false;
            if (!plan.Retro && !PrepareExistingRetro(cargo))
            {
                reason = "Para este airdrop, selecione o Type-12 sem o Retro Kit antigo.";
                return false;
            }

            operation.CommittedMount = cargo;
            operation.Committed = plan;
            operation.CommittedUntil = Time.time + delay + 12f;
            Report(aircraft, "Lançamento aceito. Mantenha o rumo até a carga sair.");
            log.LogInfo($"Superheavy airdrop armed: {cargo.cargo.unitName}, rail delay {delay:F1}s, " +
                $"target {plan.Touchdown}, " + (plan.Retro ? "guided Cargo Retro Kit." : $"gross kit {plan.Profile.Dry + plan.Profile.Fuel:F0}kg."));
            return true;
        }

        private static bool TryPlan(Aircraft aircraft, MountedCargo cargo,
            Vector3 releaseWorld, Vector3 releaseVelocity, float delay, Operation operation, out Plan plan, out string reason)
        {
            plan = default;
            if (SuperheavyRetroBridge.IsRetro(cargo))
                return TryPlanRetro(aircraft,cargo,releaseWorld,releaseVelocity,delay,operation,out plan,out reason);
            if (!TryProfile(cargo, out var profile))
            {
                reason = "Esta carga ainda não tem um kit de airdrop.";
                return false;
            }
            if (!SuperheavyAirdropEnvelope.ValidMotion(releaseVelocity.x, releaseVelocity.y, releaseVelocity.z, delay))
            {
                reason = "AIRDROP — dados de trajetória inválidos.";
                return false;
            }

            GlobalPosition? occupied = operation.LastDrop > 0f && Time.time - operation.LastDrop < 35f
                ? operation.LastTouchdown : (GlobalPosition?)null;
            Vector3 heading = Vector3.ProjectOnPlane(releaseVelocity, Vector3.up).normalized;
            if (heading.sqrMagnitude < .01f)
                heading = Vector3.ProjectOnPlane(aircraft.transform.forward, Vector3.up).normalized;
            if (heading.sqrMagnitude < .01f)
                heading = Vector3.forward;
            Vector3 projectedRelease = releaseWorld + aircraft.rb.GetPointVelocity(releaseWorld) * delay;
            if (!ReleaseHeightAllowed(projectedRelease.y - SurfaceBelow(projectedRelease, out _, out _) - profile.Offset))
            {
                reason = "AIRDROP — a saída da carga está abaixo do terreno.";
                return false;
            }
            Forecast(projectedRelease, releaseVelocity, projectedRelease, profile, true, out var natural, false);
            if (natural.Seconds <= 0f || !SuperheavyAirdropZone.TryResolve(natural.Point.ToGlobalPosition(), heading,
                    profile.HalfLength, profile.HalfWidth, out var touchdown, out var direction,
                    point => ReleaseHeightAllowed(projectedRelease.y - point.ToLocalPosition().y - profile.Offset) &&
                        Forecast(projectedRelease, releaseVelocity, point.ToLocalPosition(), profile, true, out _),
                    occupied))
            {
                reason = "AIRDROP — sem terreno seguro ao alcance. Faça outra passada sobre terra.";
                return false;
            }

            if (Vector3.Dot(direction, heading) < 0f)
                direction = -direction;
            plan = new Plan { Touchdown = touchdown, Heading = direction, Profile = profile };
            reason = null;
            return true;
        }

        private static bool ReleaseHeightAllowed(float clearance) => SuperheavyAirdropEnvelope.AboveSurface(clearance);

        private static bool TryPlanRetro(Aircraft aircraft,MountedCargo cargo,Vector3 release,Vector3 velocity,
            float delay,Operation operation,out Plan plan,out string reason)
        {
            plan=default;reason="AIRDROP — instale Cargo Retro Kit 0.3.1 para usar o planejador de terreno.";
            if(!SuperheavyRetroBridge.Ready)return false;
            if(!SuperheavyAirdropEnvelope.ValidMotion(velocity.x,velocity.y,velocity.z,delay))
            {reason="AIRDROP — dados de trajetória inválidos.";return false;}
            Vector3 start=release+aircraft.rb.GetPointVelocity(release)*delay;
            Vector3 heading=Vector3.ProjectOnPlane(velocity,Vector3.up).normalized;
            if(heading.sqrMagnitude<.01f)heading=Vector3.ProjectOnPlane(aircraft.transform.forward,Vector3.up).normalized;
            if(heading.sqrMagnitude<.01f)heading=Vector3.forward;
            var profile=new Profile{HalfLength=Mathf.Max(1.1f,cargo.cargo.length*.5f),
                HalfWidth=Mathf.Max(.7f,cargo.cargo.width*.5f),Offset=cargo.cargo.spawnOffset.y};
            reason="AIRDROP — sem terreno alcançável para a frenagem do Retro Kit.";
            if(!SuperheavyRetroBridge.Natural(cargo,start,velocity,out var natural))return false;
            GlobalPosition? occupied=operation.LastDrop>0f&&Time.time-operation.LastDrop<35f
                ?operation.LastTouchdown:(GlobalPosition?)null;
            if(!SuperheavyAirdropZone.TryResolve(natural.ToGlobalPosition(),heading,profile.HalfLength,profile.HalfWidth,
                out var target,out var direction,(point,roadHeading)=>SuperheavyRetroBridge.Reachable(cargo,start,velocity,
                    cargo.transform.rotation,point,Vector3.Dot(roadHeading,heading)<0f?-roadHeading:roadHeading),occupied))return false;
            if(Vector3.Dot(direction,heading)<0f)direction=-direction;
            // Verify the actual chosen road heading as well as its position.
            if(!SuperheavyRetroBridge.Reachable(cargo,start,velocity,cargo.transform.rotation,target,direction))return false;
            plan=new Plan{Retro=true,Touchdown=target,Heading=direction,Profile=profile};reason=null;return true;
        }

        private static bool TryProfile(MountedCargo cargo, out Profile profile)
        {
            profile = default;
            if (cargo == null || cargo.cargo == null || cargo.cargo.unitPrefab == null ||
                !cargo.cargo.unitPrefab.TryGetComponent<GroundVehicle>(out var template))
                return false;
            float mass = template.GetPrefabMass();
            if (mass < 450f || mass > 75000f)
                return false;
            profile = Profile.ForMass(mass);
            profile.HalfLength = Mathf.Clamp(cargo.cargo.length * .5f, 1.1f, 7.5f);
            profile.HalfWidth = Mathf.Clamp(cargo.cargo.width * .5f, .7f, 3f);
            profile.Offset = Mathf.Max(0f, cargo.cargo.spawnOffset.y);
            profile.Drag = template.TryGetComponent<Rigidbody>(out var rb) ? rb.drag : 0f;
            return true;
        }

        internal static bool IsArmed(Aircraft aircraft, MountedCargo cargo) =>
            Operations.TryGetValue(aircraft, out var operation) && operation.CommittedMount == cargo &&
            Time.time <= operation.CommittedUntil;

        internal static void AcceptSpawn(Aircraft aircraft, MountedCargo cargo, GroundVehicle released)
        {
            if (!IsArmed(aircraft, cargo) || released == null || released.rb == null ||
                released.TryGetComponent<SuperheavyAirdrop>(out _))
                return;
            var operation = Operations.GetOrCreateValue(aircraft);
            Plan plan = operation.Committed;
            operation.CommittedMount = null;
            var body = released.rb;
            if(plan.Retro)
            {
                Vector3 actual=body.position;
                bool Valid(GlobalPosition point,Vector3 heading)=>
                    SuperheavyAirdropZone.ValidateTouchdown(point,heading,plan.Profile.HalfLength,plan.Profile.HalfWidth,out _) &&
                    SuperheavyRetroBridge.Reachable(cargo,actual,body.velocity,body.rotation,point,heading);
                if(!Valid(plan.Touchdown,plan.Heading) && SuperheavyRetroBridge.Natural(cargo,actual,body.velocity,out var natural) &&
                    SuperheavyAirdropZone.TryResolve(natural.ToGlobalPosition(),plan.Heading,plan.Profile.HalfLength,plan.Profile.HalfWidth,
                        out var alternate,out var direction,(point,roadHeading)=>Valid(point,
                            Vector3.Dot(roadHeading,plan.Heading)<0f?-roadHeading:roadHeading),
                        operation.LastDrop>0f&&Time.time-operation.LastDrop<35f?operation.LastTouchdown:(GlobalPosition?)null))
                {plan.Touchdown=alternate;plan.Heading=Vector3.Dot(direction,plan.Heading)<0f?-direction:direction;}
                bool reachable=Valid(plan.Touchdown,plan.Heading);
                if(SuperheavyRetroBridge.Arm(released,cargo,plan.Touchdown,plan.Heading))
                {
                    operation.LastTouchdown=plan.Touchdown;operation.LastDrop=Time.time;
                    log.LogInfo($"Courier guided retro separation revalidated: {released.definition.unitName}, safe={reachable}, target={plan.Touchdown}.");
                    if(!reachable)Report(aircraft,"AIRDROP — trajetória mudou; retros em recuperação de emergência.");
                }
                return;
            }
            Vector3 start = released.startPosition.ToLocalPosition();
            Vector3 target = plan.Touchdown.ToLocalPosition();
            if (!SuperheavyAirdropZone.ValidateTouchdown(plan.Touchdown, plan.Heading, plan.Profile.HalfLength,
                    plan.Profile.HalfWidth, out _) || !Forecast(start, body.velocity, target, plan.Profile, true, out _))
            {
                if (TryFindAlternate(ref plan, start, body.velocity, 0f, plan.Profile.Fuel, false))
                {
                    log.LogInfo("Superheavy airdrop selected an alternate landing spot at separation.");
                }
                else
                {
                    log.LogWarning($"Superheavy airdrop of {cargo.cargo.unitName}: target out of reach after rail travel; native recovery kept.");
                    Report(aircraft, "AIRDROP — trajetória mudou na rampa; recuperação nativa.");
                    return;
                }
            }

            var descent = released.gameObject.AddComponent<SuperheavyAirdrop>();
            descent.vehicle = released;
            descent.carrier = aircraft;
            descent.body = body;
            descent.plan = plan;
            descent.initialMass = body.mass;
            descent.fuel = plan.Profile.Fuel;
            body.mass = descent.initialMass + plan.Profile.Dry + descent.fuel;
            try { descent.CreateHardware(); }
            catch (Exception error) { log.LogWarning($"Superheavy airdrop visuals: {error.Message}"); }
            operation.LastTouchdown = plan.Touchdown;
            operation.LastDrop = Time.time;
            log.LogInfo($"Superheavy airdrop separated: {released.definition.unitName}, velocity {body.velocity}, target {plan.Touchdown}.");
        }

        private static bool TryFindAlternate(ref Plan plan, Vector3 point, Vector3 velocity, float age, float fuel, bool powered)
        {
            var profile = plan.Profile;
            Forecast(point, velocity, plan.Touchdown.ToLocalPosition(), profile, true, out var natural,
                false, age, fuel, powered);
            if (natural.Seconds <= 0f ||
                !SuperheavyAirdropZone.TryResolve(natural.Point.ToGlobalPosition(), plan.Heading, profile.HalfLength,
                    profile.HalfWidth, out var alternate, out var direction,
                    candidate => Forecast(point, velocity, candidate.ToLocalPosition(), profile, true, out _, true, age, fuel, powered)))
                return false;

            plan.Touchdown = alternate;
            if (direction.sqrMagnitude > .1f)
                plan.Heading = Vector3.Dot(direction, plan.Heading) < 0f ? -direction : direction;
            return true;
        }

        // The original optional Type-12 variant still handles its other carrier aircraft.
        private static bool PrepareExistingRetro(MountedCargo cargo)
        {
            if (!cargo.name.StartsWith("JP_RDK_", StringComparison.OrdinalIgnoreCase) &&
                !cargo.cargo.name.StartsWith("JP_RDK_", StringComparison.OrdinalIgnoreCase))
                return true;
            if (oldRetroPatched)
                return true;
            var oldType = AccessTools.TypeByName("Japensou.NuclearOption.Type12Retro.RetroLandingController");
            var arm = oldType == null ? null : AccessTools.Method(oldType, "Arm", new[] { typeof(GroundVehicle) });
            if (arm == null)
                return false;
            try
            {
                patcher.Patch(arm, prefix: new HarmonyMethod(AccessTools.Method(typeof(SuperheavyAirdrop), nameof(AllowOriginalRetro))));
                oldRetroPatched = true;
                return true;
            }
            catch (Exception error)
            {
                log.LogWarning($"Superheavy old retro controller: {error.Message}");
                return false;
            }
        }

        private static bool AllowOriginalRetro(GroundVehicle __0) =>
            __0 == null || !__0.TryGetComponent<SuperheavyAirdrop>(out _);

        // Shared thrust and fuel law. A vertical stop always gets its share before lateral steering.
        internal static Vector3 Command(Profile profile, Vector3 position, Vector3 velocity, Vector3 target,
            float height, float time, float dt, ref bool lit, ref float fuel, bool steer = true)
        {
            if (time < SeparationSeconds || fuel <= 0f)
                return Vector3.zero;
            Vector3 horizontal = Vector3.ProjectOnPlane(velocity, Vector3.up);
            float maxAccel = profile.PeakForce / (profile.VehicleMass + profile.Dry + fuel);
            float falling = Mathf.Max(0f, -velocity.y);
            float brakeDistance = falling * falling / (2f * (maxAccel - Gravity));
            if (!lit && height > 170f + brakeDistance * 1.35f + horizontal.sqrMagnitude / 75f)
                return Vector3.zero;
            lit = true;

            float terminal = Mathf.Clamp(Mathf.Sqrt(2f * 2.4f * Mathf.Max(0f, height - 2.4f)), .95f, 32f);
            float vertical = Mathf.Clamp(Gravity + (-terminal - velocity.y) / .45f, 0f, maxAccel);
            Vector3 error = Vector3.ProjectOnPlane(target - position, Vector3.up);
            float desiredSpeed = steer ? Mathf.Min(38f, Mathf.Min(error.magnitude / 2.2f, height * .36f)) : 0f;
            Vector3 desired = error.normalized * desiredSpeed;
            float lateral = Mathf.Min(profile.SideAcceleration, Mathf.Sqrt(Mathf.Max(0f, maxAccel * maxAccel - vertical * vertical)));
            Vector3 thrust = Vector3.up * vertical + Vector3.ClampMagnitude((desired - horizontal) / 1.4f, lateral);
            float cost = (profile.VehicleMass + profile.Dry + fuel) * thrust.magnitude * dt / ExhaustVelocity;
            float spent = Mathf.Min(fuel, cost);
            fuel -= spent;
            return cost > .00001f ? thrust * (spent / cost) : Vector3.zero;
        }

        private static bool Forecast(Vector3 start, Vector3 velocity, Vector3 target, Profile profile, bool terrain,
            out ForecastResult result, bool steer = true, float initialAge = 0f, float initialFuel = -1f, bool lit = false)
        {
            result = default;
            Vector3 point = start;
            float fuel = initialFuel < 0f ? profile.Fuel : initialFuel;
            float offset = profile.Offset;
            float surface = target.y;
            int collisionLayers = terrain ? (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask : 0;
            Vector3 lastSegment = start;
            int steps = SuperheavyAirdropEnvelope.ForecastSteps(start.y - target.y - offset);
            for (int i = 0; i < steps; i++)
            {
                if (terrain && i % 5 == 0)
                    surface = SurfaceBelow(point, out _, out _);
                float clearance = point.y - surface - offset;
                if (clearance < .18f)
                {
                    float error = Vector3.ProjectOnPlane(point - target, Vector3.up).magnitude;
                    float lateral = Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude;
                    result = new ForecastResult { Point = point, Seconds = i * ForecastStep, FuelFraction = fuel / profile.Fuel };
                    return lit && (!steer || error <= Mathf.Max(8f, profile.HalfLength * 1.6f)) && Mathf.Abs(velocity.y) <= 2f &&
                        lateral <= 4f && result.FuelFraction >= MinimumReserve && (!terrain || surface > Datum.LocalSeaY + .3f);
                }
                Vector3 thrust = Command(profile, point, velocity, target, clearance, initialAge + i * ForecastStep,
                    ForecastStep, ref lit, ref fuel, steer);
                velocity = (velocity + (Vector3.down * Gravity + thrust) * ForecastStep) *
                    Mathf.Max(0f, 1f - profile.Drag * ForecastStep);
                point += velocity * ForecastStep;
                if (terrain && clearance > 3f && i % 3 == 0 &&
                    Physics.Linecast(lastSegment - Vector3.up * offset, point - Vector3.up * offset,
                        collisionLayers, QueryTriggerInteraction.Ignore))
                    return false;
                if (i % 3 == 0)
                    lastSegment = point;
                if (fuel <= 0f)
                    return false;
            }
            return false;
        }

        internal static bool VerifyFlatRelease(Profile profile, Vector3 start, Vector3 velocity, Vector3 target,
            out Vector3 end, out float seconds, out float reserve)
        {
            bool safe = Forecast(start, velocity, target, profile, false, out var result);
            end = result.Point;
            seconds = result.Seconds;
            reserve = result.FuelFraction;
            return safe;
        }

        private static float SurfaceBelow(Vector3 point, out Vector3 normal, out bool dry)
        {
            bool hit = Physics.Raycast(point + Vector3.up * .12f, Vector3.down, out var support, 5000f,
                PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
            normal = hit ? support.normal : Vector3.up;
            dry = hit && support.point.y > Datum.LocalSeaY + .3f;
            return dry ? support.point.y : Datum.LocalSeaY;
        }

        private void FixedUpdate()
        {
            if (finished || vehicle == null || body == null || (vehicle.networked && !vehicle.IsServer))
                return;
            if (vehicle.disabled)
            {
                Finish(false);
                return;
            }
            float dt = Time.fixedDeltaTime;
            age += dt;
            float surface = SurfaceBelow(body.position, out var normal, out bool dry);
            float height = body.position.y - surface - plan.Profile.Offset;
            Vector3 velocity = body.velocity;
            bool contact = dry && height >= -.32f && height <= .25f && normal.y >= .90f &&
                Mathf.Abs(Vector3.Dot(velocity, normal)) <= 2f &&
                Vector3.ProjectOnPlane(velocity, normal).sqrMagnitude <= 16f &&
                Vector3.Dot(body.rotation * Vector3.up, normal) >= .92f && body.angularVelocity.sqrMagnitude <= .25f;
            landedFor = contact ? landedFor + dt : 0f;
            if (landedFor >= .06f || age >= SuperheavyAirdropEnvelope.MaximumSeconds || (!dry && height < -.5f))
            {
                Finish(landedFor >= .06f);
                return;
            }
            if (body.isKinematic || contact)
                return;

            if (height > 65f && age >= nextZoneCheck)
            {
                nextZoneCheck = age + 1.2f;
                if (!SuperheavyAirdropZone.ValidateTouchdown(plan.Touchdown, plan.Heading, plan.Profile.HalfLength,
                        plan.Profile.HalfWidth, out _) && TryFindAlternate(ref plan, body.position, velocity, age, fuel, powered))
                    log.LogInfo($"Superheavy airdrop redirected to clear terrain at {plan.Touchdown}.");
            }
            Vector3 target = plan.Touchdown.ToLocalPosition();
            Vector3 thrust = Command(plan.Profile, body.position, velocity, target, Mathf.Max(0f, height),
                age, dt, ref powered, ref fuel);
            body.mass = initialMass + plan.Profile.Dry + fuel;
            body.AddForce(thrust, ForceMode.Acceleration);
            if (powered && fuel > 0f)
            {
                Vector3 up = height < 10f && dry ? normal : Vector3.up;
                Quaternion desired = Quaternion.LookRotation(Vector3.ProjectOnPlane(plan.Heading, up), up);
                Quaternion error = desired * Quaternion.Inverse(body.rotation);
                error.ToAngleAxis(out float angle, out var axis);
                if (angle > 180f)
                    angle -= 360f;
                Vector3 correction = axis * (angle * Mathf.Deg2Rad * 3f) - body.angularVelocity * 3.3f;
                body.AddTorque(Vector3.ClampMagnitude(correction, 3.2f), ForceMode.Acceleration);
            }
            UpdateHardware(thrust);
        }

        private void Finish(bool landed)
        {
            finished = true;
            body.mass = initialMass;
            if (hardware != null)
                Destroy(hardware);
            try
            {
                if (deployment != null)
                {
                    if (landed)
                        deployment.CargoDeploymentSystem_OnUnitLanded();
                    else
                        Destroy(deployment.gameObject);
                }
            }
            finally
            {
                if (!vehicle.disabled)
                {
                    vehicle.SetWheelsLocked(false);
                    vehicle.StowTurrets(false);
                    vehicle.enabled = true;
                }
            }
            float error = Vector3.ProjectOnPlane(body.position - plan.Touchdown.ToLocalPosition(), Vector3.up).magnitude;
            Report(carrier, landed ? $"Carga entregue: {vehicle.definition.unitName}." :
                $"AIRDROP — pouso seguro não confirmado: {vehicle.definition.unitName}.");
            log.LogInfo($"Superheavy airdrop {(landed ? "touchdown" : "ended without dry touchdown")}: {vehicle.definition.unitName}, " +
                $"offset={error:F1}m, remaining fuel={fuel:F0}kg.");
            enabled = false;
        }

        private void CreateHardware()
        {
            hardware = new GameObject("SHGT_AirdropRetroKit");
            hardware.transform.SetParent(transform, false);
            bells = new Transform[4];
            float radius = Mathf.Clamp(plan.Profile.HalfWidth * .18f, .14f, .42f);
            for (int i = 0; i < bells.Length; i++)
            {
                var pod = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                var collider = pod.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
                pod.name = "Retro thruster";
                pod.transform.SetParent(hardware.transform, false);
                pod.transform.localPosition = new Vector3((i % 2 == 0 ? -1f : 1f) * (plan.Profile.HalfWidth + radius * .28f),
                    Mathf.Max(.18f, plan.Profile.Offset * .20f), (i < 2 ? -1f : 1f) * plan.Profile.HalfLength * .57f);
                pod.transform.localScale = new Vector3(radius * 2f, radius * 1.3f, radius * 2f);
                var flame = new GameObject("Retro exhaust");
                flame.transform.SetParent(pod.transform, false);
                flame.transform.localPosition = Vector3.down * 1.15f;
                flame.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var particles = flame.AddComponent<ParticleSystem>();
                var main = particles.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetime = .36f;
                main.startSpeed = 9f;
                main.startSize = radius * 1.3f;
                main.startColor = new Color(1f, .55f, .13f, .9f);
                main.maxParticles = 100;
                var emission = particles.emission;
                emission.rateOverTime = 40f;
                var shape = particles.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 9f;
                flame.SetActive(false);
                bells[i] = flame.transform;
            }
        }

        private void UpdateHardware(Vector3 thrust)
        {
            if (bells == null)
                return;
            bool emitting = thrust.sqrMagnitude > 1f;
            foreach (var bell in bells)
            {
                if (bell == null)
                    continue;
                if (emitting)
                    bell.rotation = Quaternion.LookRotation(-thrust.normalized, transform.forward);
                if (bell.gameObject.activeSelf != emitting)
                    bell.gameObject.SetActive(emitting);
            }
        }

        private static void Report(Aircraft aircraft, string message)
        {
            if (aircraft != null && GameManager.IsLocalAircraft(aircraft))
                SceneSingleton<AircraftActionsReport>.i?.ReportText(message, 3f);
        }

        [HarmonyPatch(typeof(CargoDeploymentSystem), "Initialize")]
        private static class DeploymentPatch
        {
            private static readonly AccessTools.FieldRef<CargoDeploymentSystem, List<Parachute>> Chutes =
                AccessTools.FieldRefAccess<CargoDeploymentSystem, List<Parachute>>("listParachutes");

            private static void Prefix(CargoDeploymentSystem __instance, Unit __0)
            {
                if (__0 == null || !__0.TryGetComponent<SuperheavyAirdrop>(out var descent) || descent.finished)
                    return;
                var parachutes = Chutes(__instance);
                if (parachutes != null)
                    foreach (var parachute in parachutes)
                        if (parachute != null)
                            parachute.gameObject.SetActive(false);
                Chutes(__instance) = new List<Parachute>();
                descent.deployment = __instance;
            }
        }
    }
}
