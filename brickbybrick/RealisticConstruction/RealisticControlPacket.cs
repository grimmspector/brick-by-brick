using ProtoBuf;

namespace brickbybrick.RealisticConstruction
{
    // Carries client placement state and server profiling requests.
    [ProtoContract]
    public sealed class RealisticControlPacket
    {
        [ProtoMember(1)]
        public int Code;

        [ProtoMember(2)]
        public bool PlacementState;

        [ProtoMember(3)]
        public int Orientation;

        [ProtoMember(4)]
        public int Variant;
    }

    // Mirrors one chunk-side reconstruction record to clients that may need
    // to rebuild the entityless block mesh before the chunk is reloaded.
    [ProtoContract]
    public sealed class StaticMasonryStatePacket
    {
        [ProtoMember(1)]
        public int X;

        [ProtoMember(2)]
        public int Y;

        [ProtoMember(3)]
        public int Z;

        [ProtoMember(4)]
        public byte[] State = System.Array.Empty<byte>();

        [ProtoMember(5)]
        public bool Remove;
    }
}
