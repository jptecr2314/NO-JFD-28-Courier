using System.Collections;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Japensou.NuclearOption.Superheavy
{
    internal sealed class SuperheavyCargoGroundAI : MonoBehaviour
    {
        private static ManualLogSource log;
        private static readonly AccessTools.FieldRef<GroundVehicle,bool> Mobile=AccessTools.FieldRefAccess<GroundVehicle,bool>("mobile");
        private static readonly AccessTools.FieldRef<GroundVehicle,bool> Navigate=AccessTools.FieldRefAccess<GroundVehicle,bool>("navigateToObjectives");
        private static readonly System.Reflection.FieldInfo Waypoints=AccessTools.Field(typeof(GroundVehicle),"savedWaypoints");
        private static readonly System.Action<GroundVehicle> Assess=AccessTools.MethodDelegate<System.Action<GroundVehicle>>(AccessTools.Method(typeof(GroundVehicle),"CheckObstacles"));
        private GroundVehicle vehicle;
        internal static void Configure(ManualLogSource logger){log=logger;}
        internal static void Track(Unit unit)
        {
            if(unit is GroundVehicle ground && ground.GetComponent<SuperheavyCargoGroundAI>()==null)
                ground.gameObject.AddComponent<SuperheavyCargoGroundAI>().vehicle=ground;
        }
        private IEnumerator Start()
        {
            float deadline=Time.time+SuperheavyAirdropEnvelope.MaximumSeconds+10f;
            while(vehicle!=null&&!vehicle.disabled&&Time.time<deadline)
            {
                if(vehicle.LocalSim && Ready())break;
                yield return new WaitForSeconds(.25f);
            }
            if(vehicle==null||vehicle.disabled||!vehicle.LocalSim||!Ready()||!Mobile(vehicle))yield break;
            // Player orders and explicit mission waypoints take precedence.
            if(vehicle.UnitCommand==null||vehicle.UnitCommand.GetCommandCached().FromPlayer ||
                (Waypoints.GetValue(vehicle) is ICollection waypoints&&waypoints.Count>0))yield break;
            bool held=vehicle.GetHoldPosition(),nav=Navigate(vehicle);
            vehicle.SetWheelsLocked(false);vehicle.StowTurrets(false);vehicle.enabled=true;
            vehicle.SetHoldPosition(false);Navigate(vehicle)=true;
            yield return new WaitForSeconds(.25f);
            if(vehicle==null||vehicle.disabled||vehicle.UnitCommand.GetCommandCached().FromPlayer)yield break;
            Assess(vehicle);
            log?.LogInfo($"Courier ground AI recovered {vehicle.definition.unitName}: hold={held}->false, objectives={nav}->true, destination={vehicle.GetDestination()}.");
            // Native logic seeks mission objectives, then known ground enemies.
            // If neither is available, join an ally or clear the landing area.
            if(!vehicle.UnitCommand.GetCommandCached().FromPlayer && (vehicle.GetDestination()-vehicle.GlobalPosition()).sqrMagnitude<900f)
            {
                var friend=Object.FindObjectsOfType<GroundVehicle>().Where(other=>other!=vehicle&&!other.disabled&&
                    other.NetworkHQ==vehicle.NetworkHQ&&Mobile(other))
                    .OrderBy(other=>(other.GlobalPosition()-vehicle.GlobalPosition()).sqrMagnitude)
                    .FirstOrDefault(other=>{float d=(other.GlobalPosition()-vehicle.GlobalPosition()).sqrMagnitude;return d>10000f&&d<25000000f;});
                GlobalPosition target=friend!=null?friend.GlobalPosition()-friend.transform.forward*45f:
                    vehicle.GlobalPosition()+vehicle.transform.forward*75f;
                if(SuperheavyAirdropZone.ValidateTouchdown(target,vehicle.transform.forward,
                    Mathf.Max(1f,vehicle.definition.length*.5f),Mathf.Max(.7f,vehicle.definition.width*.5f),out _))
                {
                    vehicle.UnitCommand.SetDestination(target,false);
                    log?.LogInfo($"Courier ground AI {vehicle.definition.unitName}: {(friend!=null?"joining friendly ground unit":"clearing landing area")} via native pathfinder.");
                }
            }
        }
        private bool Ready()
        {
            if(vehicle.rb==null||vehicle.rb.velocity.sqrMagnitude>16f)return false;
            var descent=vehicle.GetComponent<SuperheavyAirdrop>();
            if(descent!=null&&!descent.Finished)return false;
            var retro=vehicle.GetComponents<MonoBehaviour>().FirstOrDefault(c=>c!=null&&c.GetType().FullName=="Japensou.NuclearOption.Type12Retro.RetroLandingController");
            if(retro!=null && (bool)AccessTools.Property(retro.GetType(),"Armed").GetValue(retro))return false;
            if(!Physics.Raycast(vehicle.transform.position+Vector3.up*.1f,Vector3.down,out var hit,10f,PhysicsLayers.StaticsMask,QueryTriggerInteraction.Ignore))return false;
            float clearance=vehicle.transform.position.y-vehicle.definition.spawnOffset.y-hit.point.y;
            return hit.point.y>Datum.LocalSeaY+.2f && clearance>=-.25f&&clearance<=.25f;
        }
    }
}
