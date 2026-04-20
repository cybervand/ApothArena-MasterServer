using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Apotheon;
using HarmonyLib;

namespace ApotheonArena.NetworkMod.Patches
{
    [HarmonyPatch(typeof(ServerBrowser), "OnInitialize")]
    static class ServerBrowser_OnInitialize_Patch
    {
        static void Prefix()
        {
            NetworkModLog.FromSelf(NetworkModLogTag.Client, "browser-init");
        }
    }

    [HarmonyPatch(typeof(ServerBrowser), "Refresh")]
    static class ServerBrowser_Refresh_Patch
    {
        static void Prefix()
        {
            NetworkModLog.FromSelf(NetworkModLogTag.Client, "browser-refresh");
        }
    }

    [HarmonyPatch(typeof(ServerBrowser), "Update")]
    static class ServerBrowser_Update_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var codes = new List<CodeInstruction>(instructions);

            int readBoolIdx = -1;
            for (int i = 0; i + 3 < codes.Count; i++)
            {
                var ins = codes[i];
                bool isReadBool =
                    (ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call)
                    && ins.operand is MethodInfo mi
                    && mi.Name == "ReadBoolean";
                if (!isReadBool) continue;
                var branch = codes[i + 3];
                if (branch.opcode != OpCodes.Brfalse && branch.opcode != OpCodes.Brfalse_S) continue;
                readBoolIdx = i;
                break;
            }

            if (readBoolIdx < 0)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game,
                    "browser-update-transpiler | pattern not found; leaving method untouched");
                return codes;
            }

            var brfalseIns = codes[readBoolIdx + 3];
            if (!(brfalseIns.operand is Label originalTarget))
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game,
                    "browser-update-transpiler | branch target not a Label; aborting");
                return codes;
            }

            Label popupLabel = il.DefineLabel();
            brfalseIns.operand = popupLabel;

            int targetIdx = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].labels.Contains(originalTarget))
                {
                    targetIdx = i;
                    break;
                }
            }
            if (targetIdx < 0)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game,
                    "browser-update-transpiler | could not find branch target instruction");
                brfalseIns.operand = originalTarget;
                return codes;
            }

            FieldInfo screenManagerField = AccessTools.Field(typeof(ScreenElement), "ScreenManager");
            ConstructorInfo messageBoxCtor = AccessTools.Constructor(typeof(MessageBoxScreen), new[] { typeof(string) });
            MethodInfo addScreenMethod = AccessTools.FirstMethod(typeof(ScreenManager),
                m => m.Name == "AddScreen" && m.GetParameters().Length == 1);

            if (screenManagerField == null || messageBoxCtor == null || addScreenMethod == null)
            {
                NetworkModLog.SelfError(NetworkModLogTag.Game,
                    "browser-update-transpiler | missing reflection targets; aborting");
                brfalseIns.operand = originalTarget;
                return codes;
            }

            var firstPopupInstr = new CodeInstruction(OpCodes.Ldarg_0);
            firstPopupInstr.labels.Add(popupLabel);

            var popupCode = new List<CodeInstruction>
            {
                firstPopupInstr,
                new CodeInstruction(OpCodes.Ldfld, screenManagerField),
                new CodeInstruction(OpCodes.Ldstr, "No games available."),
                new CodeInstruction(OpCodes.Newobj, messageBoxCtor),
                new CodeInstruction(OpCodes.Callvirt, addScreenMethod),
                new CodeInstruction(OpCodes.Br, originalTarget),
            };

            codes.InsertRange(targetIdx, popupCode);
            return codes;
        }
    }
}
