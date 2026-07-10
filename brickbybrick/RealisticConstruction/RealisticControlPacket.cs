namespace brickbybrick.RealisticConstruction
{
    // Vintage Story's contractless protobuf registration serializes public
    // fields. No external protobuf assembly is required by the mod.
    public sealed class RealisticControlPacket
    {
        public int Code;
        public bool PlacementState;
        public int Orientation;
        public int Variant;
    }

    // Mirrors one chunk-side reconstruction record to clients that may need
    // to rebuild the entityless block mesh before the chunk is reloaded.
    public sealed class StaticMasonryStatePacket
    {
        public int X;
        public int Y;
        public int Z;
        public byte[] State = System.Array.Empty<byte>();
        public bool Remove;
    }
}
