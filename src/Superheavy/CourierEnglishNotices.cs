using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Japensou.NuclearOption.CurrentDamage
{
    // Owner request for 0.1.8 (23/11/2026): every in-game notice in English. The frozen family runtime still carries
    // eighteen Portuguese airdrop and gun-cradle notices (read from its IL; every other message is already English).
    // The family DLL stays byte-identical: this transpiler swaps only those string literals, in the seven family
    // methods that hold them, for the English text below. Unknown strings and every other instruction are untouched.
    internal static class CourierEnglishNotices
    {
        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Aguarde a carga sair da rampa."] = "Wait for the cargo to clear the ramp.",
            ["Para este airdrop, selecione o Type-12 sem o Retro Kit antigo."] = "For this airdrop, select the Type-12 without the old Retro Kit.",
            ["Lançamento aceito. Mantenha o rumo até a carga sair."] = "Airdrop accepted. Hold your heading until the cargo is out.",
            ["Esta carga ainda não tem um kit de airdrop."] = "This cargo has no airdrop kit yet.",
            ["AIRDROP — dados de trajetória inválidos."] = "AIRDROP — invalid trajectory data.",
            ["AIRDROP — a saída da carga está abaixo do terreno."] = "AIRDROP — the cargo exit point is below the terrain.",
            ["AIRDROP — sem terreno seguro ao alcance. Faça outra passada sobre terra."] = "AIRDROP — no safe terrain in reach. Make another pass over land.",
            ["AIRDROP — instale Cargo Retro Kit 0.3.1 para usar o planejador de terreno."] = "AIRDROP — install Cargo Retro Kit 0.3.1 to use the terrain planner.",
            ["AIRDROP — sem terreno alcançável para a frenagem do Retro Kit."] = "AIRDROP — no reachable terrain for Retro Kit braking.",
            ["AIRDROP — trajetória mudou; retros em recuperação de emergência."] = "AIRDROP — trajectory changed; retros in emergency recovery.",
            ["AIRDROP — trajetória mudou na rampa; recuperação nativa."] = "AIRDROP — trajectory changed on the ramp; native recovery.",
            ["AIRDROP — pouso seguro não confirmado: "] = "AIRDROP — safe landing not confirmed: ",
            ["Carga entregue: "] = "Cargo delivered: ",
            ["AIRDROP — atualize Cargo Retro Kit para 0.3.1."] = "AIRDROP — update Cargo Retro Kit to 0.3.1.",
            ["Berço indisponível: saída deste armamento não comprovada"] = "Gun cradle unavailable: this weapon's exit route is not verified",
            ["Recolhendo canhões para liberar a saída de bombas"] = "Stowing the guns to clear the bomb exit",
            ["Recolhendo berços dos canhões antes de baixar o trem"] = "Stowing the gun cradles before lowering the gear",
        };

        private static readonly (string type, string method)[] Targets =
        {
            ("SuperheavyAirdrop", "TryArm"), ("SuperheavyAirdrop", "TryPlan"), ("SuperheavyAirdrop", "TryPlanRetro"),
            ("SuperheavyAirdrop", "AcceptSpawn"), ("SuperheavyAirdrop", "Finish"), ("SuperheavyAirdrop+RetroVersionGuard", "Prefix"),
            ("SuperheavyRetractableBattery", "RequestGear"), ("SuperheavyRetractableBattery", "RequestBomb"),
        };

        private static int swapped;

        internal static void Install(Harmony h)
        {
            int patched = 0;
            var transpiler = new HarmonyMethod(typeof(CourierEnglishNotices), nameof(Transpiler));
            foreach (var (typeName, methodName) in Targets)
            {
                var parts = typeName.Split('+');
                Type type = CurrentAirframeCompatibility.Family(parts[0]);
                for (int i = 1; type != null && i < parts.Length; i++)
                    type = AccessTools.Inner(type, parts[i]);
                var methods = type == null ? Enumerable.Empty<MethodInfo>() :
                    type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(m => m.Name == methodName && !m.IsAbstract && !m.ContainsGenericParameters);
                foreach (var method in methods)
                {
                    try { h.Patch(method, transpiler: transpiler); patched++; }
                    catch (Exception e) { CurrentAirframeDamagePlugin.Log.LogWarning($"English notices: {typeName}.{methodName}: {e.Message}"); }
                }
            }
            CurrentAirframeDamagePlugin.Log.LogInfo($"English notices: {swapped} family notices translated in {patched} methods.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string text && English.TryGetValue(text, out var english))
                {
                    instruction.operand = english;
                    swapped++;
                }
                yield return instruction;
            }
        }
    }
}
