using System;

namespace Cubuild
{
    /// <summary>
    /// Geometry of a rail block, carried in the per-cell meta byte.
    ///
    /// Before this existed a rail was a featureless flat slab: it had no idea which way the
    /// track ran, so a corner and a straight looked identical and there was nothing for the
    /// mesher to draw or the cart to follow. The meta now encodes the shape outright, the same
    /// way vanilla rails do, so the value is self-describing anywhere it turns up (mesher,
    /// cart physics, network, disk).
    ///
    /// The numbering deliberately matches Minecraft's rail meta. Anyone who knows rails knows
    /// these values, and the "Minecraft" data pack in Assets/Data is line-for-line compatible.
    ///
    ///   meta | shape                  | links
    ///   -----+------------------------+----------------------------------------
    ///    0   | flat, North-South      | dir 3 &lt;-&gt; dir 1
    ///    1   | flat, East-West        | dir 0 &lt;-&gt; dir 2
    ///    2   | slope, ascends East    | dir 2 low  -&gt; dir 0 high   (+1 block)
    ///    3   | slope, ascends West    | dir 0 low  -&gt; dir 2 high   (+1 block)
    ///    4   | slope, ascends North   | dir 1 low  -&gt; dir 3 high   (+1 block)
    ///    5   | slope, ascends South   | dir 3 low  -&gt; dir 1 high   (+1 block)
    ///    6   | curve, South + East    | dir 1 &amp; dir 0
    ///    7   | curve, South + West    | dir 1 &amp; dir 2
    ///    8   | curve, North + West    | dir 3 &amp; dir 2
    ///    9   | curve, North + East    | dir 3 &amp; dir 0
    ///
    /// Directions use the engine's cardinal indices, the same ones CartEntity has always used:
    /// 0 = +X (East), 1 = +Z (South), 2 = -X (West), 3 = -Z (North). The opposite of direction
    /// i is therefore (i + 2) % 4, which is what makes the straight/slope cases below fall out
    /// so cleanly.
    /// </summary>
    public static class RailShape
    {
        // ---- shape values ----------------------------------------------------

        public const int FlatNorthSouth = 0;
        public const int FlatEastWest = 1;
        public const int SlopeEast = 2;
        public const int SlopeWest = 3;
        public const int SlopeNorth = 4;
        public const int SlopeSouth = 5;
        public const int CurveSouthEast = 6;
        public const int CurveSouthWest = 7;
        public const int CurveNorthWest = 8;
        public const int CurveNorthEast = 9;

        /// <summary>Highest meta value a rail shape uses; anything above is treated as straight.</summary>
        public const int MaxShape = 9;

        // ---- directions ------------------------------------------------------

        /// <summary>X offset per direction index (0=+X, 1=+Z, 2=-X, 3=-Z).</summary>
        public static readonly int[] DirX = { 1, 0, -1, 0 };

        /// <summary>Z offset per direction index (0=+X, 1=+Z, 2=-X, 3=-Z).</summary>
        public static readonly int[] DirZ = { 0, 1, 0, -1 };

        /// <summary>The direction opposite to <paramref name="dir"/>.</summary>
        public static int Opposite(int dir) => (dir + 2) & 3;

        /// <summary>
        /// The slope shape that rises toward <paramref name="dir"/> (its high end points that
        /// way). Indexed by direction: E=2, S=5, W=3, N=4 - note this is NOT the direction order.
        /// </summary>
        private static readonly int[] SlopeAscending = { SlopeEast, SlopeSouth, SlopeWest, SlopeNorth };

        /// <summary>Shape of a slope whose high end faces <paramref name="dir"/>.</summary>
        public static int SlopeAscendingToward(int dir) => SlopeAscending[dir & 3];

        /// <summary>True for the four sloped shapes (metas 2-5).</summary>
        public static bool IsSlope(int meta) => meta >= SlopeEast && meta <= SlopeSouth;

        /// <summary>True for the four curved shapes (metas 6-9).</summary>
        public static bool IsCurve(int meta) => meta >= CurveSouthEast && meta <= CurveNorthEast;

        /// <summary>True for either flat straight shape.</summary>
        public static bool IsFlatStraight(int meta) => meta == FlatNorthSouth || meta == FlatEastWest;

        /// <summary>
        /// The direction a slope climbs toward, or -1 when the shape is not a slope.
        /// East=0, South=1, West=2, North=3.
        /// </summary>
        public static int AscendDirection(int meta) => meta switch
        {
            SlopeEast => 0,
            SlopeSouth => 1,
            SlopeWest => 2,
            SlopeNorth => 3,
            _ => -1,
        };

        /// <summary>
        /// The two directions a shape links, low side first. For a slope the first entry is the
        /// low (ground-level) end and the second the raised end, so callers that just need
        /// "which way does this run" can ignore the distinction.
        /// </summary>
        public static (int a, int b) Connections(int meta) => meta switch
        {
            FlatNorthSouth => (3, 1),
            FlatEastWest => (0, 2),
            SlopeEast => (2, 0),
            SlopeWest => (0, 2),
            SlopeNorth => (1, 3),
            SlopeSouth => (3, 1),
            CurveSouthEast => (1, 0),
            CurveSouthWest => (1, 2),
            CurveNorthWest => (3, 2),
            CurveNorthEast => (3, 0),
            _ => (3, 1),
        };

        /// <summary>
        /// For a slope, the meta that describes the SAME track climbed from the other end.
        /// Going up East-facing and going up West-facing are different cells; this is only used
        /// when a shape has to be rebuilt from a travel direction alone.
        /// </summary>
        public static int SlopeFromLowToHigh(int lowDir, int highDir)
            => lowDir == Opposite(highDir) ? SlopeAscendingToward(highDir) : FlatNorthSouth;

        // ---- neighbour scanning ---------------------------------------------

        /// <summary>
        /// A neighbour's connection to this cell. Rise is how many blocks the neighbour sits
        /// above us: 0 for level track, +1 when it climbs, -1 when it drops. <see cref="None"/>
        /// means there is no rail in that direction at any of the three heights.
        /// </summary>
        public readonly struct Link
        {
            public readonly bool Exists;
            public readonly int Rise;

            public Link(bool exists, int rise) { Exists = exists; Rise = rise; }

            public static readonly Link None = new Link(false, 0);
        }

        private static int _railIdCache = -1;

        /// <summary>Cached numeric id of the "rail" block (the registry can be reloaded, hence the setter).</summary>
        public static int RailId
        {
            get
            {
                if (_railIdCache < 0) _railIdCache = BlockRegistry.GetId("rail");
                return _railIdCache;
            }
            set => _railIdCache = value;
        }

        /// <summary>True when the cell holds a rail block.</summary>
        public static bool IsRailAt(ChunkManager cm, int x, int y, int z)
            => cm.TryGetLoadedBlock(x, y, z, out int id) && id == RailId;

        /// <summary>
        /// Looks for rail track one block in <paramref name="dir"/> from (x,y,z), at this level
        /// or one up/down. Level track wins over a step, so a flat line is never mistaken for a
        /// slope just because something happens to sit above or below it.
        /// </summary>
        public static Link LinkFrom(ChunkManager cm, int x, int y, int z, int dir)
        {
            int nx = x + DirX[dir & 3];
            int nz = z + DirZ[dir & 3];
            if (IsRailAt(cm, nx, y, nz)) return new Link(true, 0);
            if (IsRailAt(cm, nx, y + 1, nz)) return new Link(true, 1);
            if (IsRailAt(cm, nx, y - 1, nz)) return new Link(true, -1);
            return Link.None;
        }

        /// <summary>All four links around a cell, indexed by direction.</summary>
        public static Link[] Links(ChunkManager cm, int x, int y, int z)
        {
            var links = new Link[4];
            for (int i = 0; i < 4; i++) links[i] = LinkFrom(cm, x, y, z, i);
            return links;
        }

        // ---- shape resolution ------------------------------------------------

        /// <summary>
        /// Works out the shape a rail at (x,y,z) should take from the rails around it.
        ///
        /// A straight run takes priority over a curve, and a level pair beats a stepped one, so
        /// the common cases (a line, a corner, a ramp at the end of a line) all come out exactly
        /// as a player would draw them. Anything ambiguous - a T-junction, a cell walled in on
        /// three sides - falls back to a sensible straight rather than thrashing.
        /// </summary>
        public static int ComputeShape(ChunkManager cm, int x, int y, int z)
        {
            var links = Links(cm, x, y, z);

            // 1. A straight run: exactly the opposite pair, and nothing perpendicular. Checked
            //    first so a ramp between two level neighbours still reads as track, not a curve.
            for (int i = 0; i < 4; i++)
            {
                int j = Opposite(i);
                if (!links[i].Exists || !links[j].Exists) continue;

                // If something also joins from the side this is a junction; prefer the pair that
                // does NOT step vertically, so a flat line through a T stays flat.
                bool perpendicular = links[(i + 1) & 3].Exists || links[(i + 3) & 3].Exists;
                if (perpendicular && links[i].Rise == 0 && links[j].Rise == 0) continue;

                return StraightOrSlopeFor(i, links[i].Rise, j, links[j].Rise);
            }

            // 2. A corner: two neighbours at right angles. A neighbour one block BELOW still
            //    counts - that neighbour is a slope climbing up to this cell, and the top of a
            //    hill is exactly where that happens. Only a neighbour ABOVE disqualifies the
            //    curve: if either side rises away from us, this cell is itself a ramp.
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) & 3;
                if (links[i].Exists && links[j].Exists && links[i].Rise <= 0 && links[j].Rise <= 0)
                    return CurveFor(i, j);
            }

            // 3. Exactly one neighbour: climb to it if it sits a block up, otherwise run
            //    straight toward it. A neighbour BELOW does not make this a ramp (see
            //    StraightOrSlopeFor) - the lower cell is the one carrying the climb. The
            //    single-neighbour guard matters: with two links that form no corner or straight
            //    (a T-junction on top of a hill, say) this would otherwise pick whichever
            //    direction came first and draw a bogus straight through the junction.
            int neighbours = (links[0].Exists ? 1 : 0) + (links[1].Exists ? 1 : 0)
                           + (links[2].Exists ? 1 : 0) + (links[3].Exists ? 1 : 0);
            if (neighbours == 1)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (!links[i].Exists) continue;
                    if (links[i].Rise > 0) return SlopeAscendingToward(i);
                    return i == 0 || i == 2 ? FlatEastWest : FlatNorthSouth;
                }
            }

            // 5. Nothing to connect to - a lone stub. Default to North-South.
            return FlatNorthSouth;
        }

        /// <summary>
        /// Picks the straight or sloped shape for an opposite pair of links.
        ///
        /// Only a neighbour a block ABOVE turns this cell into a ramp - the rail climbs to meet
        /// it. A neighbour a block BELOW is the one doing the climbing, and this cell stays
        /// level; treating that as a slope too would make a ramp two cells long and leave the
        /// block at the top of an ordinary step angled when it should sit flat.
        /// </summary>
        private static int StraightOrSlopeFor(int dirA, int riseA, int dirB, int riseB)
        {
            if (riseA > 0) return SlopeAscendingToward(dirA);
            if (riseB > 0) return SlopeAscendingToward(dirB);
            return (dirA == 0 || dirA == 2) ? FlatEastWest : FlatNorthSouth;
        }

        /// <summary>
        /// The curve shape joining two perpendicular directions. The eight curves form a cycle
        /// (SE, SW, NW, NE), so the two direction indices are enough to name one uniquely.
        /// </summary>
        private static int CurveFor(int a, int b)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            return (lo, hi) switch
            {
                (0, 1) => CurveSouthEast,   // East + South
                (1, 2) => CurveSouthWest,   // South + West
                (2, 3) => CurveNorthWest,   // West  + North
                (0, 3) => CurveNorthEast,   // East  + North
                // Same-axis pairs never reach here; fall back to a straight.
                _ => (a == 0 || a == 2) ? FlatEastWest : FlatNorthSouth,
            };
        }

        /// <summary>
        /// True when the rail at (x,y,z) links out toward <paramref name="dir"/>. Used by the
        /// cart to know which exits a cell actually offers.
        /// </summary>
        public static bool ConnectsTo(int meta, int dir)
        {
            var (a, b) = Connections(meta);
            return a == dir || b == dir;
        }

        /// <summary>
        /// Atlas tile for a rail shape. Only TWO tiles exist: one straight and one curve, both at
        /// row 9. The four orientations of each are produced by rotating the tile's UVs (see
        /// <see cref="GetRailRotation"/>), so the atlas stays small and the art is authored once.
        /// Slopes reuse the straight tile of their axis - the climb itself is carried by the
        /// cart's Y and the ramp geometry, not the art.
        /// </summary>
        public static TextureRect GetRailTexture(int meta)
        {
            int col = IsCurve(meta) ? CurveCol : StraightCol;
            int ts = BlockRegistry.TileSize;
            return new TextureRect(col * ts, RailRow * ts, ts, ts);
        }

        /// <summary>Atlas row the rail tiles live on.</summary>
        private const int RailRow = 9;
        private const int StraightCol = 3;
        private const int CurveCol = 4;

        /// <summary>
        /// Quarter turns (0-3) to apply to the rail tile so it faces the way the track runs.
        ///
        /// Baselines, proven against the art and the renderer rather than assumed: the straight
        /// tile's rails run along the tile's V axis, which is world Z, so it is already correct
        /// for a North-South line. The curve tile is a diagonal that leaves through the image's
        /// RIGHT and BOTTOM edges. The renderer does NOT flip V (the fragment shader samples
        /// atlasUV.y = ty + dv*th and Veldrid uploads image row 0 at the top of the texture),
        /// while the face axes give dv = 0 at the south edge - so image BOTTOM is the NORTH edge
        /// of the cell and the baseline curve is <see cref="CurveNorthEast"/>.
        ///
        /// From there each quarter turn steps the exits NORTH-EAST -> SOUTH-EAST ->
        /// SOUTH-WEST -> NORTH-WEST. (The step direction was initially backwards; NE and SW came
        /// out right while SE and NW were mirrored, which is what pinned it down.)
        /// </summary>
        public static int GetRailRotation(int meta)
        {
            switch (meta)
            {
                case FlatNorthSouth: return 0;   // straight along Z - the tile's own orientation
                case FlatEastWest:  return 1;   // turn it onto the X axis

                // Slopes need NO rotation, in either axis - and that is not an oversight. A
                // flat rail's normal is straight up and axis-aligned, so the renderer gives
                // du = X and dv = north-to-south. A ramp's normal is diagonal (it climbs), so
                // TryGetCubuildFaceAxes instead yields du = across the track and dv = the
                // ramp direction - which is already along the track. Rotating here would swap
                // those and lay the rails across the ramp instead of up it. Empirically the
                // E-W slopes did exactly that (sideways rails) while the N-S ones looked
                // right, because for both of them dv is the ramp.
                case SlopeEast:
                case SlopeWest:
                case SlopeNorth:
                case SlopeSouth:    return 0;

                case CurveNorthEast: return 0;  // the curve tile's authored orientation
                case CurveSouthEast: return 1;
                case CurveSouthWest: return 2;
                case CurveNorthWest: return 3;
                default: return 0;
            }
        }
    }
}
