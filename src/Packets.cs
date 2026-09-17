using System.Collections.Generic;
using ProtoBuf;

namespace lionfox_ArmorQuickWear
{
    public enum QuickWearAction
    {
        Toggle = 0,
        PutOn = 1,
        TakeOff = 2,
        MountFromCursor = 3,
        Unmount = 4,
        MountWorn = 5,
        ClearAll = 6
    }

    // No field initializers on these: protobuf-net skips zero values when writing, so a field that
    // defaulted to -1 would come back as -1 whenever 0 was sent.

    // Client -> server. Cell only matters for MountFromCursor and Unmount.
    [ProtoContract]
    public class ActionPacket
    {
        [ProtoMember(1)] public QuickWearAction Action;
        [ProtoMember(2)] public int Cell;
    }

    // Server -> client: the player's whole loadout as TreeAttribute bytes, item stacks included.
    [ProtoContract]
    public class LoadoutPacket
    {
        [ProtoMember(1)] public byte[]? Data;
    }

    [ProtoContract]
    public class Problem
    {
        // -1 when the problem isn't about one mounted piece.
        [ProtoMember(1)] public int Cell;
        [ProtoMember(2)] public string? Code;
    }

    // Server -> client after an action. Only codes travel; the client words them in its own language.
    [ProtoContract]
    public class ResultPacket
    {
        [ProtoMember(1)] public QuickWearAction Action;
        [ProtoMember(2)] public int Moved;
        [ProtoMember(3)] public List<Problem>? Problems;
    }
}
