using System;
using System.Net;
using HarmonyLib;
using Lidgren.Network;

namespace ApotheonArena.NetworkMod.Patches
{
    [HarmonyPatch(typeof(NetPeer), "SendUnconnectedMessage",
        new[] { typeof(NetOutgoingMessage), typeof(IPEndPoint) })]
    static class NetPeer_SendUnconnectedMessage_Patch
    {
        static void Prefix(NetOutgoingMessage msg, IPEndPoint recipient)
        {
            try
            {
                int length = msg != null ? msg.LengthBytes : 0;
                NetworkModLog.ToPeer(NetworkModLog.PeerFor(recipient),
                    "send-unconnected | to=" + NetworkModLog.Describe(recipient) + " bytes=" + length);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(NetPeer), "ReadMessage", new Type[0])]
    static class NetPeer_ReadMessage_Patch
    {
        static void Postfix(NetIncomingMessage __result)
        {
            if (__result == null) return;
            try
            {
                NetIncomingMessageType type = __result.MessageType;
                if (type == NetIncomingMessageType.Data) return;
                if (type == NetIncomingMessageType.Receipt) return;

                IPEndPoint sender = __result.SenderEndpoint;
                NetworkModLog.FromPeer(NetworkModLog.PeerFor(sender),
                    "read-message | type=" + type
                    + " from=" + NetworkModLog.Describe(sender)
                    + " bytes=" + __result.LengthBytes);
            }
            catch { }
        }
    }
}
