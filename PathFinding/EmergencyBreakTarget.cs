namespace Cubuild
{
    /// <summary>A single explicitly-approved block on an emergency navigation route.</summary>
    public readonly struct EmergencyBreakTarget
    {
        public readonly int X, Y, Z;
        public readonly int BlockId;
        public readonly float Hardness;
        public readonly int ApproachX, ApproachY, ApproachZ;

        public EmergencyBreakTarget(int x, int y, int z, int blockId, float hardness,
            int approachX, int approachY, int approachZ)
        {
            X = x; Y = y; Z = z;
            BlockId = blockId;
            Hardness = hardness;
            ApproachX = approachX; ApproachY = approachY; ApproachZ = approachZ;
        }
    }
}
