using System;

namespace DfoServer.Network.Parsers.Town
{
    /// <summary>
    /// PARTY_TELEPORT (0x01B3) destination selected by the town-map cursor.
    /// Wire layout: town:u8, area:u8, x:i16, y:i16, direction:u8.
    /// </summary>
    internal readonly struct PartyTeleportRequest
    {
        internal PartyTeleportRequest(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
        }

        internal byte TownId { get; }
        internal byte AreaId { get; }
        internal short X { get; }
        internal short Y { get; }
        internal byte Direction { get; }

        internal static bool TryParse(
            byte[] body,
            out PartyTeleportRequest request)
        {
            request = default;
            if (body == null || body.Length != 7)
                return false;

            request = new PartyTeleportRequest(
                body[0],
                body[1],
                BitConverter.ToInt16(body, 2),
                BitConverter.ToInt16(body, 4),
                body[6]);
            return true;
        }
    }
}
