using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Veldrid;

namespace Cubuild
{
    public sealed class MobModel : IDisposable
    {
        private GraphicsDevice _gd;
        private DeviceBuffer? _vertexBuffer;
        private DeviceBuffer? _indexBuffer;
        private Texture? _texture;
        private TextureView? _textureView;
        private Sampler? _sampler;
        private ResourceLayout? _textureLayout;

        // One rigid body part of the model (Blockbench exports each part as its own mesh). The
        // part has a pivot (its mesh node's translation) and an optional joint node whose rotation
        // is driven by the model's animation - animating a part = rotating it around its pivot:
        //   v' = T + R(t)(v - T)
        internal sealed class ModelPart
        {
            public Vector3[] Positions = Array.Empty<Vector3>();
            public Vector2[] Uvs = Array.Empty<Vector2>();
            public ushort[] Indices = Array.Empty<ushort>();
            public float[] Shades = Array.Empty<float>();
            public Vector3 Pivot;
            public int JointNode = -1; // animation joint node, or -1 for static parts
            public string Name = "";   // mesh node name (e.g. "leftarm") for procedural limbs
            public Quaternion StaticRotation = Quaternion.Identity; // node's baked pose rotation

            // ---- bone-parented parts (the locomotive's wheels/piston) --------------------------
            // When this part hangs off an ANIMATED ANCESTOR bone (Blockbench can nest the mesh
            // cubes INSIDE the bone, the opposite of the coyote's mesh-node-then-child-joint
            // shape), the joint's translation and scale animate too, not just its rotation - the
            // locomotive's piston slides and stretches. Those parts set JointFullTrs and carry the
            // pivot's own rotation so the sampled TRS can be lifted into the model's frame.
            public bool JointFullTrs;
            public Quaternion JointParentRotation = Quaternion.Identity;
            // Rest TRS (already in the parent-rotated model frame) the animation blends back to
            // when AnimBlend eases to 0, so a stopped locomotive returns to its neutral stance.
            public Quaternion JointRestRotation = Quaternion.Identity;
            public Vector3 JointRestTranslation = Vector3.Zero;
            public Vector3 JointRestScale = Vector3.One;
        }

        private readonly List<ModelPart> _parts = new();
        private readonly List<float> _allShades = new();

        /// <summary>
        /// CPU-side mesh, in bind pose. The world path only needs the GPU buffers, but offline uses
        /// do need the raw geometry - the software icon renderer draws the minecart's hotbar icon
        /// from exactly these triangles, so the icon and the entity can never diverge.
        /// </summary>
        internal List<ModelPart> Parts => _parts;

        /// <summary>
        /// CPU copy of the model texture (RGBA, top-left origin - glTF's UV convention, which is
        /// what the mesh UVs and the world shader both assume). Kept for the icon renderer, which
        /// has no GPU sampler to lean on.
        /// </summary>
        internal byte[]? CpuTexture;
        internal int CpuTextureWidth;
        internal int CpuTextureHeight;

        // Keyframed joint channels: jointNode -> keyframes, sampled by animTime. glTF stores
        // rotation / translation / scale as separate channels that may have DIFFERENT key counts
        // and times (the locomotive's piston has 15 rotation keys but only 5 translation and 8
        // scale keys), so each path keeps its own timeline. Rest* holds the node's untweened TRS,
        // used when that path is not animated at all.
        private sealed class JointTrack
        {
            public float[] Times = Array.Empty<float>();
            public Quaternion[] Rotations = Array.Empty<Quaternion>();
            public float Duration;

            public float[] TTimes = Array.Empty<float>();
            public Vector3[] Translations = Array.Empty<Vector3>();
            public float TDuration;

            public float[] STimes = Array.Empty<float>();
            public Vector3[] Scales = Array.Empty<Vector3>();
            public float SDuration;

            public Vector3 RestTranslation = Vector3.Zero;
            public Quaternion RestRotation = Quaternion.Identity;
            public Vector3 RestScale = Vector3.One;
        }

        private readonly Dictionary<int, JointTrack> _jointTracks = new();

        public bool Loaded { get; private set; } = false;
        public int VertexCount { get; private set; }
        public int IndexCount { get; private set; }

        public MobModel(GraphicsDevice graphicsDevice) { _gd = graphicsDevice; }

        public bool Load(string modelPath, string texturePath)
        {
            try
            {
                if (modelPath.EndsWith(".bbmodel", StringComparison.OrdinalIgnoreCase))
                {
                    LoadBlockbenchModel(modelPath);
                }
                else
                {
                    LoadGLBModel(modelPath);
                }

                ComputeFaceShades();
                CreateBuffers();
                if (!string.IsNullOrEmpty(texturePath)) LoadTexture(texturePath);
                return Loaded = VertexCount > 0 && IndexCount > 0;
            }
            catch { return false; }
        }

        // Classic directional face shading: bottom 0.5, top 1.0, N/S 0.8, E/W 0.6. For each
        // triangle, the face normal picks a shade; every vertex of that triangle gets the same
        // shade so GLB models read like the hand-authored duck/player cube models. Vertices shared
        // across faces take the max (brightest) shade so seams stay clean.
        private void ComputeFaceShades()
        {
            foreach (var part in _parts)
            {
                for (int i = 0; i < part.Positions.Length; i++) part.Shades[i] = 0f;
                if (part.Indices.Length == 0) continue;

                for (int i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    int i0 = part.Indices[i], i1 = part.Indices[i + 1], i2 = part.Indices[i + 2];
                    if (i0 >= part.Positions.Length || i1 >= part.Positions.Length || i2 >= part.Positions.Length) continue;

                    var a = part.Positions[i0];
                    var b = part.Positions[i1];
                    var c = part.Positions[i2];
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.LengthSquared() < 1e-12f) continue;
                    n = Vector3.Normalize(n);

                    float shade = FaceShade(n);
                    if (shade > part.Shades[i0]) part.Shades[i0] = shade;
                    if (shade > part.Shades[i1]) part.Shades[i1] = shade;
                    if (shade > part.Shades[i2]) part.Shades[i2] = shade;
                }
            }

            // Flatten for CreateBuffers (which still uses the flat index/vertex arrays).
            _allShades.Clear();
            foreach (var part in _parts) _allShades.AddRange(part.Shades);
        }

        private static float FaceShade(Vector3 n)
        {
            if (n.Y > 0.5f) return 1.0f;
            if (n.Y < -0.5f) return 0.5f;
            if (Math.Abs(n.X) > 0.5f) return 0.6f;
            return 0.8f;
        }

        public bool LoadGLB(string modelPath)
        {
            try
            {
                LoadGLBModel(modelPath);
                ComputeFaceShades();
                CreateBuffers();
                return Loaded = VertexCount > 0 && IndexCount > 0;
            }
            catch { return false; }
        }

        private void LoadBlockbenchModel(string modelPath)
        {
            var json = File.ReadAllText(modelPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _parts.Clear();
            _jointTracks.Clear();

            if (root.TryGetProperty("elements", out var elements))
            {
                foreach (var element in elements.EnumerateArray())
                {
                    ProcessBlockbenchElement(element);
                }
            }
        }

        private void ProcessBlockbenchElement(JsonElement element)
        {
            // Blockbench elements have from/to directly on the element
            if (!element.TryGetProperty("from", out var fromArr) || !element.TryGetProperty("to", out var toArr))
                return;

            Vector3 origin = Vector3.Zero;
            Vector3 rotation = Vector3.Zero;
            if (element.TryGetProperty("origin", out var originArr)) origin = ReadVector3(originArr);
            if (element.TryGetProperty("rotation", out var rotArr)) rotation = ReadVector3(rotArr);

            // Load custom vertices if present
            Dictionary<string, Vector3> vertexMap = new Dictionary<string, Vector3>();
            if (element.TryGetProperty("vertices", out var verticesObj))
            {
                foreach (var vertexProp in verticesObj.EnumerateObject())
                {
                    if (vertexProp.Value.ValueKind == JsonValueKind.Array && vertexProp.Value.GetArrayLength() >= 3)
                    {
                        Vector3 pos = new Vector3(
                            vertexProp.Value[0].GetSingle(),
                            vertexProp.Value[1].GetSingle(),
                            vertexProp.Value[2].GetSingle()
                        );
                        vertexMap[vertexProp.Name] = pos;
                    }
                }
            }

            // Read face UV data from faces property
            Dictionary<string, float[]> faceUVMap = new Dictionary<string, float[]>();
            if (element.TryGetProperty("faces", out var facesObj))
            {
                foreach (var faceProp in facesObj.EnumerateObject())
                {
                    if (faceProp.Value.TryGetProperty("uv", out var uvArr) && uvArr.GetArrayLength() >= 4)
                    {
                        faceUVMap[faceProp.Name] = new float[] {
                            uvArr[0].GetSingle(),
                            uvArr[1].GetSingle(),
                            uvArr[2].GetSingle(),
                            uvArr[3].GetSingle()
                        };
                    }
                }
            }

            float scale = 1.0f / 16.0f;
            var part = new ModelPart
            {
                Pivot = origin * scale,
                JointNode = -1, // .bbmodel files carry no animation rig here
            };
            var posList = new List<Vector3>();
            var uvList = new List<Vector2>();
            var idxList = new List<ushort>();

            // Process faces directly from the faces property
            if (element.TryGetProperty("faces", out var facesElement))
            {
                foreach (var faceProp in facesElement.EnumerateObject())
                {
                    if (!faceProp.Value.TryGetProperty("vertices", out var vertexIds))
                        continue;
                    
                    if (vertexIds.GetArrayLength() < 3)
                        continue;

                    // Get UVs for this face
                    float[] uvData = new float[] { 0, 0, 1, 1 };
                    if (faceUVMap.ContainsKey(faceProp.Name))
                    {
                        uvData = faceUVMap[faceProp.Name];
                    }

                    float uvLeft = uvData[0];
                    float uvTop = uvData[1];
                    float uvRight = uvData[2];
                    float uvBottom = uvData[3];

                    // Collect all vertex positions for this face
                    List<Vector3> faceVerts = new List<Vector3>();
                    for (int i = 0; i < vertexIds.GetArrayLength(); i++)
                    {
                        Vector3 pos = GetVertexPosition(vertexIds[i].GetString(), vertexMap, origin, rotation, scale);
                        faceVerts.Add(pos);
                    }

                    // Calculate proper UVs for each vertex based on its position in the face
                    Vector2[] faceUVs = CalculateFaceUVs(faceVerts, uvLeft, uvTop, uvRight, uvBottom);

                    // Triangulate the face vertices (fan triangulation for convex polygons)
                    for (int tri = 1; tri < faceVerts.Count - 1; tri++)
                    {
                        int baseIndex = posList.Count;
                        posList.Add(faceVerts[0]);
                        uvList.Add(faceUVs[0]);
                        posList.Add(faceVerts[tri]);
                        uvList.Add(faceUVs[tri]);
                        posList.Add(faceVerts[tri + 1]);
                        uvList.Add(faceUVs[tri + 1]);

                        idxList.Add((ushort)baseIndex);
                        idxList.Add((ushort)(baseIndex + 1));
                        idxList.Add((ushort)(baseIndex + 2));
                    }
                }
            }

            if (posList.Count > 0)
            {
                part.Positions = posList.ToArray();
                part.Uvs = uvList.ToArray();
                part.Indices = idxList.ToArray();
                part.Shades = new float[part.Positions.Length];
                _parts.Add(part);
            }
        }

        private Vector2[] CalculateFaceUVs(List<Vector3> verts, float uvLeft, float uvTop, float uvRight, float uvBottom)
        {
            Vector2[] uvs = new Vector2[verts.Count];
            float texW = 64f, texH = 64f;
            
            // Find the bounds of the face in world space
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            
            foreach (var v in verts)
            {
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }
            
            float rangeX = maxX - minX;
            float rangeY = maxY - minY;
            float rangeZ = maxZ - minZ;
            
            // Map each vertex to UV space based on its position
            for (int i = 0; i < verts.Count; i++)
            {
                float u, v;
                var vert = verts[i];
                
                // Determine which axis the face is aligned with and map accordingly
                if (Math.Abs(rangeX) < 0.001f) // Face is in YZ plane (left/right face)
                {
                    u = (vert.Y - minY) / (rangeY > 0.001f ? rangeY : 1f);
                    v = (vert.Z - minZ) / (rangeZ > 0.001f ? rangeZ : 1f);
                }
                else if (Math.Abs(rangeZ) < 0.001f) // Face is in XY plane (top/bottom face)
                {
                    u = (vert.X - minX) / (rangeX > 0.001f ? rangeX : 1f);
                    v = (vert.Y - minY) / (rangeY > 0.001f ? rangeY : 1f);
                }
                else // Face is in XZ plane (front/back face)
                {
                    u = (vert.X - minX) / (rangeX > 0.001f ? rangeX : 1f);
                    v = (vert.Z - minZ) / (rangeZ > 0.001f ? rangeZ : 1f);
                }
                
                // Map to texture coordinates
                u = uvLeft + u * (uvRight - uvLeft);
                v = uvBottom + v * (uvTop - uvBottom);
                
                uvs[i] = new Vector2(u / texW, v / texH);
            }
            
            return uvs;
        }

        private Vector3 GetVertexPosition(string vertexId, Dictionary<string, Vector3> vertexMap, Vector3 origin, Vector3 rotation, float scale)
        {
            Vector3 localPos = Vector3.Zero;
            
            if (!string.IsNullOrEmpty(vertexId) && vertexMap.ContainsKey(vertexId))
            {
                localPos = vertexMap[vertexId];
            }
            else
            {
                return localPos;
            }

            // Rotate around the element's origin pivot (localPos is in element-local space)
            Vector3 rotated = RotateVertex(localPos, origin, rotation);
            return rotated * scale;
        }

        private Vector3 ReadVector3(JsonElement arr)
        {
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 3)
            {
                return new Vector3(arr[0].GetSingle(), arr[1].GetSingle(), arr[2].GetSingle());
            }
            return Vector3.Zero;
        }

        private Vector3 RotateVertex(Vector3 vertex, Vector3 origin, Vector3 rotationDeg)
        {
            Vector3 v = vertex - origin;
            Vector3 rot = MathF.PI / 180f * rotationDeg;

            float cosX = MathF.Cos(rot.X), sinX = MathF.Sin(rot.X);
            float y = v.Y * cosX - v.Z * sinX;
            float z = v.Y * sinX + v.Z * cosX;
            v.Y = y; v.Z = z;

            float cosY = MathF.Cos(rot.Y), sinY = MathF.Sin(rot.Y);
            float x = v.X * cosY + v.Z * sinY;
            float z2 = -v.X * sinY + v.Z * cosY; // use the original v.X, not the reassigned one
            v.X = x; v.Z = z2;

            float cosZ = MathF.Cos(rot.Z), sinZ = MathF.Sin(rot.Z);
            float x2 = v.X * cosZ - v.Y * sinZ;
            float y2 = v.X * sinZ + v.Y * cosZ;
            v.X = x2; v.Y = y2;

            return v + origin;
        }

        private void LoadGLBModel(string modelPath)
        {
            try
            {
                byte[] glbBytes = File.ReadAllBytes(modelPath);
                
                // GLB header: magic(4) + version(4) + length(4) = 12 bytes
                if (glbBytes.Length < 12) return;
                
                uint magic = BitConverter.ToUInt32(glbBytes, 0);
                if (magic != 0x46546C67) return; // "glTF"
                
                uint jsonLength = BitConverter.ToUInt32(glbBytes, 12);
                uint jsonChunkType = BitConverter.ToUInt32(glbBytes, 16);
                if (jsonChunkType != 0x4E4F534A) return; // "JSON"
                
                string jsonText = Encoding.UTF8.GetString(glbBytes, 20, (int)jsonLength);
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("meshes", out var meshes)) return;
                if (!root.TryGetProperty("bufferViews", out var bufferViews)) return;
                if (!root.TryGetProperty("accessors", out var accessors)) return;
                if (!root.TryGetProperty("buffers", out var buffers)) return;
                
                // Get binary chunk offset (skip JSON chunk header: length(4) + type(4) = 8 bytes)
                uint binaryOffset = 20 + jsonLength + 8;

                // Blockbench exports each body part as its OWN mesh, and two node shapes turn up:
                //
                //  * FLAT (coyote, zombie, cart): the MESH node sits in the scene and the animated
                //    joint is its FIRST CHILD. The part sits at the node translation and the joint
                //    only ever rotates, about the node's origin.
                //
                //  * BONE-PARENTED (locomotive): the mesh cubes hang INSIDE an animated ancestor
                //    bone ('wheel' - cubes, 'bone' - piston rods). Those bones animate TRANSLATION
                //    and SCALE as well as rotation, and their children carry their own baked pose
                //    rotations, so the flat model cannot express them - we need the real node chain.
                //
                // Both are served by accumulating the real node transforms, then baking each mesh
                // node's vertices down into the frame of its nearest animated ancestor-or-self bone
                // and storing THAT bone as the joint. Flat exports degenerate to the old behaviour
                // (bake the node's own rotation/scale, pivot at the node origin, rotate about it).
                _parts.Clear();
                _jointTracks.Clear();

                // ---- nodes -------------------------------------------------------------------
                int nodeCount = 0;
                if (root.TryGetProperty("nodes", out var nodes)) nodeCount = nodes.GetArrayLength();

                var nodeT = new Vector3[nodeCount];
                var nodeR = new Quaternion[nodeCount];
                var nodeS = new Vector3[nodeCount];
                var nodeName = new string[nodeCount];
                var nodeMesh = new int[nodeCount];
                var nodeChild = new int[nodeCount];       // FIRST child (flat exports put the joint here)
                var nodeParent = new int[nodeCount];
                var nodeChildren = new int[nodeCount][];
                for (int i = 0; i < nodeCount; i++)
                {
                    nodeR[i] = Quaternion.Identity;
                    nodeS[i] = Vector3.One;
                    nodeName[i] = "";
                    nodeMesh[i] = -1;
                    nodeChild[i] = -1;
                    nodeParent[i] = -1;
                    nodeChildren[i] = Array.Empty<int>();
                }

                int nIdx = 0;
                foreach (var node in nodes.EnumerateArray())
                {
                    if (node.TryGetProperty("translation", out var tArr) && tArr.GetArrayLength() >= 3)
                        nodeT[nIdx] = new Vector3(tArr[0].GetSingle(), tArr[1].GetSingle(), tArr[2].GetSingle());
                    if (node.TryGetProperty("rotation", out var rArr) && rArr.GetArrayLength() >= 4)
                        nodeR[nIdx] = new Quaternion(rArr[0].GetSingle(), rArr[1].GetSingle(), rArr[2].GetSingle(), rArr[3].GetSingle());
                    if (node.TryGetProperty("scale", out var sArr) && sArr.GetArrayLength() >= 3)
                        nodeS[nIdx] = new Vector3(sArr[0].GetSingle(), sArr[1].GetSingle(), sArr[2].GetSingle());
                    if (node.TryGetProperty("name", out var nameProp))
                        nodeName[nIdx] = nameProp.GetString() ?? "";
                    if (node.TryGetProperty("mesh", out var mProp)) nodeMesh[nIdx] = mProp.GetInt32();
                    if (node.TryGetProperty("children", out var cArr))
                    {
                        nodeChildren[nIdx] = new int[cArr.GetArrayLength()];
                        for (int c = 0; c < cArr.GetArrayLength(); c++) nodeChildren[nIdx][c] = cArr[c].GetInt32();
                        nodeChild[nIdx] = nodeChildren[nIdx].Length > 0 ? nodeChildren[nIdx][0] : -1;
                    }
                    nIdx++;
                }
                for (int i = 0; i < nodeCount; i++)
                {
                    foreach (int c in nodeChildren[i])
                    {
                        if (c >= 0 && c < nodeCount) nodeParent[c] = i;
                    }
                }

                // ---- animation channels --------------------------------------------------------
                // glTF keeps rotation / translation / scale as SEPARATE channels that may have
                // different key counts and times (the locomotive's piston has 15 rotation keys but
                // only 5 translation and 8 scale keys), so each path gets its own timeline.
                if (root.TryGetProperty("animations", out var animations))
                {
                    foreach (var anim in animations.EnumerateArray())
                    {
                        if (!anim.TryGetProperty("channels", out var channels)) continue;
                        if (!anim.TryGetProperty("samplers", out var animSamplers)) continue;
                        foreach (var channel in channels.EnumerateArray())
                        {
                            if (!channel.TryGetProperty("target", out var target)) continue;
                            if (!target.TryGetProperty("node", out var nodeProp)) continue;
                            if (!target.TryGetProperty("path", out var pathProp)) continue;
                            int jointNode = nodeProp.GetInt32();
                            if (jointNode < 0 || jointNode >= nodeCount) continue;
                            int samplerIdx = channel.GetProperty("sampler").GetInt32();
                            if (samplerIdx < 0 || samplerIdx >= animSamplers.GetArrayLength()) continue;
                            var sampler = animSamplers[samplerIdx];
                            int inputAcc = sampler.GetProperty("input").GetInt32();
                            int outputAcc = sampler.GetProperty("output").GetInt32();

                            if (!_jointTracks.TryGetValue(jointNode, out var track))
                            {
                                track = new JointTrack
                                {
                                    RestTranslation = nodeT[jointNode],
                                    RestRotation = nodeR[jointNode],
                                    RestScale = nodeS[jointNode],
                                };
                                _jointTracks[jointNode] = track;
                            }

                            switch (pathProp.GetString())
                            {
                                case "rotation":
                                    track.Times = ReadFloatArray(accessors, bufferViews, glbBytes, binaryOffset, inputAcc);
                                    track.Rotations = ReadQuaternionArray(accessors, bufferViews, glbBytes, binaryOffset, outputAcc);
                                    if (track.Times.Length > 0) track.Duration = track.Times[track.Times.Length - 1];
                                    break;
                                case "translation":
                                    track.TTimes = ReadFloatArray(accessors, bufferViews, glbBytes, binaryOffset, inputAcc);
                                    track.Translations = ReadVector3Array(accessors, bufferViews, glbBytes, binaryOffset, outputAcc);
                                    if (track.TTimes.Length > 0) track.TDuration = track.TTimes[track.TTimes.Length - 1];
                                    break;
                                case "scale":
                                    track.STimes = ReadFloatArray(accessors, bufferViews, glbBytes, binaryOffset, inputAcc);
                                    track.Scales = ReadVector3Array(accessors, bufferViews, glbBytes, binaryOffset, outputAcc);
                                    if (track.STimes.Length > 0) track.SDuration = track.STimes[track.STimes.Length - 1];
                                    break;
                            }
                        }
                    }
                }

                // ---- accumulated node transforms ------------------------------------------------
                // accM[n] maps node n's LOCAL space straight to model space. System.Numerics uses
                // row vectors (v' = v * M), so "scale, then rotate, then translate" composes as
                // S * R * T, and a child prepends its parent:  accM[n] = M(n) * accM[parent].
                var accM = new Matrix4x4[nodeCount];
                var haveAcc = new bool[nodeCount];
                var pending = new Stack<int>();
                for (int i = 0; i < nodeCount; i++)
                {
                    if (nodeParent[i] < 0) pending.Push(i);
                }
                while (pending.Count > 0)
                {
                    int n = pending.Pop();
                    if (haveAcc[n]) continue;
                    var local = Matrix4x4.CreateScale(nodeS[n])
                              * Matrix4x4.CreateFromQuaternion(nodeR[n])
                              * Matrix4x4.CreateTranslation(nodeT[n]);
                    if (nodeParent[n] < 0)
                    {
                        accM[n] = local;
                    }
                    else
                    {
                        if (!haveAcc[nodeParent[n]]) { pending.Push(nodeParent[n]); pending.Push(n); continue; }
                        accM[n] = local * accM[nodeParent[n]];
                    }
                    haveAcc[n] = true;
                    foreach (int c in nodeChildren[n])
                    {
                        if (c >= 0 && c < nodeCount) pending.Push(c);
                    }
                }

                // ---- mesh parts -----------------------------------------------------------------
                int meshCount = meshes.GetArrayLength();
                for (int n = 0; n < nodeCount; n++)
                {
                    int meshIndex = nodeMesh[n];
                    if (meshIndex < 0 || meshIndex >= meshCount) continue;
                    if (!meshes[meshIndex].TryGetProperty("primitives", out var primitives)) continue;

                    int bone = NearestAnimatedNode(n, nodeParent, _jointTracks);

                    Vector3 pivot = Vector3.Zero;
                    Quaternion parentRot = Quaternion.Identity;
                    Quaternion staticRot = Quaternion.Identity;
                    Quaternion restRot = Quaternion.Identity;
                    Vector3 restTrans = Vector3.Zero;
                    Vector3 restScale = Vector3.One;
                    int jointNode = -1;
                    bool fullTrs = false;
                    Matrix4x4 bake = Matrix4x4.Identity;

                    if (bone >= 0)
                    {
                        // Bone-parented part. Hand the renderer the BONE's frame: pivot at the bone
                        // origin's parent, and the bone's sampled TRS re-applied at draw time.
                        int boneParent = nodeParent[bone];
                        if (boneParent >= 0)
                        {
                            DecomposeTransform(accM[boneParent], out pivot, out parentRot, out _);
                        }

                        // Bake the vertices into the BONE's local frame (strip the bone's own TRS) so
                        // that re-applying the sampled TRS rebuilds the whole chain. D = accM[n] *
                        // inverse(accM[bone]); folding it in here keeps draw time to one rotate.
                        if (Matrix4x4.Invert(accM[bone], out var boneInv))
                        {
                            bake = accM[n] * boneInv;
                        }

                        _jointTracks.TryGetValue(bone, out var boneTrack);
                        fullTrs = true;
                        if (boneTrack != null)
                        {
                            restRot = Quaternion.Multiply(parentRot, boneTrack.RestRotation);
                            restTrans = Vector3.Transform(boneTrack.RestTranslation, parentRot);
                            restScale = boneTrack.RestScale;
                        }
                        jointNode = bone;
                    }
                    else if (nodeChild[n] >= 0 && _jointTracks.ContainsKey(nodeChild[n]))
                    {
                        // Flat export: mesh node in the scene, animated joint is its first child.
                        // Bake the node's own pose so the joint can rotate about the node origin.
                        DecomposeTransform(accM[n], out pivot, out staticRot, out var scale);
                        bake = Matrix4x4.CreateScale(scale);
                        jointNode = nodeChild[n];
                    }
                    else
                    {
                        // Fully static part.
                        DecomposeTransform(accM[n], out pivot, out staticRot, out var scale);
                        bake = Matrix4x4.CreateScale(scale);
                    }

                    foreach (var primitive in primitives.EnumerateArray())
                    {
                        ExtractPrimitive(primitive, bufferViews, accessors, buffers, glbBytes, binaryOffset,
                            pivot, jointNode, staticRot, nodeName[n], bake,
                            parentRot, fullTrs, restRot, restTrans, restScale);
                    }
                }
            }
            catch { }
        }

        private static float[] ReadFloatArray(JsonElement accessors, JsonElement bufferViews, byte[] glbBytes, uint binaryOffset, int accessorIdx)
        {
            var acc = accessors[accessorIdx];
            int count = acc.GetProperty("count").GetInt32();
            var view = bufferViews[acc.GetProperty("bufferView").GetInt32()];
            int byteOffset = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int start = (int)(binaryOffset + byteOffset);
            var result = new float[count];
            for (int i = 0; i < count; i++)
                result[i] = BitConverter.ToSingle(glbBytes, start + i * 4);
            return result;
        }

        private static Quaternion[] ReadQuaternionArray(JsonElement accessors, JsonElement bufferViews, byte[] glbBytes, uint binaryOffset, int accessorIdx)
        {
            var acc = accessors[accessorIdx];
            int count = acc.GetProperty("count").GetInt32();
            var view = bufferViews[acc.GetProperty("bufferView").GetInt32()];
            int byteOffset = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int start = (int)(binaryOffset + byteOffset);
            var result = new Quaternion[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * 16;
                // glTF quaternion is (x, y, z, w).
                result[i] = new Quaternion(
                    BitConverter.ToSingle(glbBytes, o),
                    BitConverter.ToSingle(glbBytes, o + 4),
                    BitConverter.ToSingle(glbBytes, o + 8),
                    BitConverter.ToSingle(glbBytes, o + 12));
            }
            return result;
        }

        private static Vector3[] ReadVector3Array(JsonElement accessors, JsonElement bufferViews, byte[] glbBytes, uint binaryOffset, int accessorIdx)
        {
            var acc = accessors[accessorIdx];
            int count = acc.GetProperty("count").GetInt32();
            var view = bufferViews[acc.GetProperty("bufferView").GetInt32()];
            int byteOffset = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int start = (int)(binaryOffset + byteOffset);
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int o = start + i * 12;
                result[i] = new Vector3(
                    BitConverter.ToSingle(glbBytes, o),
                    BitConverter.ToSingle(glbBytes, o + 4),
                    BitConverter.ToSingle(glbBytes, o + 8));
            }
            return result;
        }

        /// <summary>
        /// Splits a node's accumulated transform into translation / rotation / per-axis scale.
        /// The rotation is recovered by normalising the basis vectors (glTF nodes may carry scale,
        /// and Quaternion.CreateFromRotationMatrix requires an orthonormal matrix).
        /// </summary>
        private static void DecomposeTransform(Matrix4x4 m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            translation = m.Translation;
            var sx = new Vector3(m.M11, m.M12, m.M13);
            var sy = new Vector3(m.M21, m.M22, m.M23);
            var sz = new Vector3(m.M31, m.M32, m.M33);
            scale = new Vector3(sx.Length(), sy.Length(), sz.Length());

            if (scale.X > 1e-6f) { m.M11 /= scale.X; m.M12 /= scale.X; m.M13 /= scale.X; }
            if (scale.Y > 1e-6f) { m.M21 /= scale.Y; m.M22 /= scale.Y; m.M23 /= scale.Y; }
            if (scale.Z > 1e-6f) { m.M31 /= scale.Z; m.M32 /= scale.Z; m.M33 /= scale.Z; }
            m.Translation = Vector3.Zero;

            try { rotation = Quaternion.CreateFromRotationMatrix(m); }
            catch { rotation = Quaternion.Identity; }
            rotation = Quaternion.Normalize(rotation);
        }

        /// <summary>
        /// Walks up from <paramref name="node"/> to the nearest ancestor-or-self that has an
        /// animation track, or -1 when nothing in the chain is animated. Flat exports hit on the
        /// node's own child (checked separately by the caller) rather than an ancestor, which is
        /// what keeps the coyote on its original code path.
        /// </summary>
        private static int NearestAnimatedNode(int node, int[] nodeParent, Dictionary<int, JointTrack> tracks)
        {
            int n = node;
            int guard = 0;
            while (n >= 0 && n < nodeParent.Length && guard++ < 1024)
            {
                if (tracks.ContainsKey(n)) return n;
                n = nodeParent[n];
            }
            return -1;
        }

        /// <summary>
        /// Samples the given joint's rotation at <paramref name="animTime"/> (seconds, wrapped to
        /// the track duration). Returns identity for a static joint.
        /// </summary>
        public Quaternion SampleJointRotation(int jointNode, float animTime)
        {
            SampleJoint(jointNode, animTime, out _, out var rotation, out _);
            return rotation;
        }

        /// <summary>
        /// Samples a joint's full TRS at <paramref name="animTime"/>. Channels the clip does not
        /// animate fall back to the node's rest transform (the locomotive's wheels only animate
        /// rotation, its piston animates all three).
        /// </summary>
        public void SampleJoint(int jointNode, float animTime, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
        {
            if (!_jointTracks.TryGetValue(jointNode, out var track))
            {
                translation = Vector3.Zero;
                rotation = Quaternion.Identity;
                scale = Vector3.One;
                return;
            }

            translation = SampleVector3(track.TTimes, track.Translations, track.TDuration, animTime, track.RestTranslation);
            rotation = SampleQuaternion(track.Times, track.Rotations, track.Duration, animTime, track.RestRotation);
            scale = SampleVector3(track.STimes, track.Scales, track.SDuration, animTime, track.RestScale);
        }

        /// <summary>Wraps t into [0, duration) - the wheel track runs 0..3.5s and must loop.</summary>
        private static float WrapTime(float animTime, float duration)
        {
            if (duration <= 0.0001f) return 0f;
            float t = animTime % duration;
            if (t < 0f) t += duration;
            return t;
        }

        private static Vector3 SampleVector3(float[] times, Vector3[] values, float duration, float animTime, Vector3 fallback)
        {
            if (values.Length == 0) return fallback;
            if (values.Length == 1) return values[0];

            float t = WrapTime(animTime, duration);
            int n = Math.Min(times.Length, values.Length);
            int hi = 1;
            while (hi < n && times[hi] < t) hi++;
            if (hi >= n) return values[n - 1];
            int lo = hi - 1;
            if (lo < 0) return values[0];

            float span = times[hi] - times[lo];
            float s = span > 0.0001f ? (t - times[lo]) / span : 0f;
            return Vector3.Lerp(values[lo], values[hi], s);
        }

        private static Quaternion SampleQuaternion(float[] times, Quaternion[] values, float duration, float animTime, Quaternion fallback)
        {
            if (values.Length == 0) return fallback;
            if (values.Length == 1) return values[0];

            float t = WrapTime(animTime, duration);
            int n = Math.Min(times.Length, values.Length);
            int hi = 1;
            while (hi < n && times[hi] < t) hi++;
            if (hi >= n) return values[n - 1];
            int lo = hi - 1;
            if (lo < 0) return values[0];

            float span = times[hi] - times[lo];
            float s = span > 0.0001f ? (t - times[lo]) / span : 0f;
            return Quaternion.Slerp(values[lo], values[hi], s);
        }

        private void ExtractPrimitive(JsonElement primitive, JsonElement bufferViews, 
            JsonElement accessors, JsonElement buffers, byte[] glbBytes, uint binaryOffset,
            Vector3 pivot, int jointNode, Quaternion staticRotation, string partName, Matrix4x4 bake,
            Quaternion parentRotation, bool fullTrs, Quaternion restRotation, Vector3 restTranslation, Vector3 restScale)
        {
            try
            {
                // Get accessor indices
                if (!primitive.TryGetProperty("attributes", out var attrs)) return;
                if (!primitive.TryGetProperty("indices", out var indicesProp)) return;
                
                // Get position accessor
                if (!attrs.TryGetProperty("POSITION", out var posAccessor)) return;
                int posAccessorIdx = posAccessor.GetInt32();
                
                // Get UV accessor
                int uvAccessorIdx = -1;
                if (attrs.TryGetProperty("TEXCOORD_0", out var uvAcc)) uvAccessorIdx = uvAcc.GetInt32();
                
                // Get indices accessor
                int indicesAccessorIdx = indicesProp.GetInt32();
                
                // Read positions
                var posAccEl = accessors[posAccessorIdx];
                int posCount = posAccEl.GetProperty("count").GetInt32();
                string posType = posAccEl.GetProperty("type").GetString();
                if (posType != "VEC3") return;
                
                var posViewIdx = posAccEl.GetProperty("bufferView").GetInt32();
                var posView = bufferViews[posViewIdx];
                int posByteOffset = posView.TryGetProperty("byteOffset", out var po) ? po.GetInt32() : 0;
                
                // Read UVs if available
                List<Vector2> uvs = new List<Vector2>();
                if (uvAccessorIdx >= 0)
                {
                    var uvAccEl = accessors[uvAccessorIdx];
                    int uvCount = uvAccEl.GetProperty("count").GetInt32();
                    string uvType = uvAccEl.GetProperty("type").GetString();
                    if (uvType == "VEC2")
                    {
                        var uvViewIdx = uvAccEl.GetProperty("bufferView").GetInt32();
                        var uvView = bufferViews[uvViewIdx];
                        int uvByteOffset = uvView.TryGetProperty("byteOffset", out var uo) ? uo.GetInt32() : 0;
                        int uvStart = (int)(binaryOffset + uvByteOffset);
                        for (int i = 0; i < uvCount; i++)
                        {
                            int offset = uvStart + i * 8;
                            float u = BitConverter.ToSingle(glbBytes, offset);
                            float v = BitConverter.ToSingle(glbBytes, offset + 4);
                            uvs.Add(new Vector2(u, v));
                        }
                    }
                }
                
                // Read positions into the PART's own space. For a flat export the bake is just the
                // node's scale (the joint rotates about the pivot, applied at write time). For a
                // bone-parented part it also folds the whole child chain down into the bone's frame,
                // so draw time only has to re-apply the sampled bone TRS.
                var part = new ModelPart
                {
                    Pivot = pivot,
                    JointNode = jointNode,
                    StaticRotation = staticRotation,
                    Name = partName,
                    JointFullTrs = fullTrs,
                    JointParentRotation = parentRotation,
                    JointRestRotation = restRotation,
                    JointRestTranslation = restTranslation,
                    JointRestScale = restScale,
                };
                part.Positions = new Vector3[posCount];
                part.Shades = new float[posCount];
                bool needBake = bake != Matrix4x4.Identity;
                int posStart = (int)(binaryOffset + posByteOffset);
                for (int i = 0; i < posCount; i++)
                {
                    int offset = posStart + i * 12;
                    float x = BitConverter.ToSingle(glbBytes, offset);
                    float y = BitConverter.ToSingle(glbBytes, offset + 4);
                    float z = BitConverter.ToSingle(glbBytes, offset + 8);
                    part.Positions[i] = needBake ? Vector3.Transform(new Vector3(x, y, z), bake) : new Vector3(x, y, z);
                }
                
                // UVs (pad with zeros if missing)
                part.Uvs = new Vector2[posCount];
                for (int i = 0; i < posCount; i++) part.Uvs[i] = i < uvs.Count ? uvs[i] : Vector2.Zero;
                
                // Read indices
                var idxAccEl = accessors[indicesAccessorIdx];
                int idxCount = idxAccEl.GetProperty("count").GetInt32();
                int idxCompType = idxAccEl.GetProperty("componentType").GetInt32();
                var idxViewIdx = idxAccEl.GetProperty("bufferView").GetInt32();
                var idxView = bufferViews[idxViewIdx];
                int idxByteOffset = idxView.TryGetProperty("byteOffset", out var io) ? io.GetInt32() : 0;
                
                int idxStart = (int)(binaryOffset + idxByteOffset);
                part.Indices = new ushort[idxCount];
                if (idxCompType == 5123) // UNSIGNED_SHORT
                {
                    for (int i = 0; i < idxCount; i++)
                        part.Indices[i] = BitConverter.ToUInt16(glbBytes, idxStart + i * 2);
                }
                else if (idxCompType == 5125) // UNSIGNED_INT
                {
                    for (int i = 0; i < idxCount; i++)
                        part.Indices[i] = (ushort)BitConverter.ToUInt32(glbBytes, idxStart + i * 4);
                }

                _parts.Add(part);
            }
            catch { }
        }

        private void LoadTexture(string texturePath)
        {
            try
            {
                using var stream = System.IO.File.OpenRead(texturePath);
                var imageBytes = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                // Keep the pixels on the CPU too: the icon renderer samples this copy directly.
                CpuTexture = imageBytes.Data;
                CpuTextureWidth = imageBytes.Width;
                CpuTextureHeight = imageBytes.Height;
                var texDesc = TextureDescription.Texture2D((uint)imageBytes.Width, (uint)imageBytes.Height, 1, 1,
                    PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled);
                _texture = _gd.ResourceFactory.CreateTexture(texDesc);
                _gd.UpdateTexture(_texture, imageBytes.Data, 0, 0, 0, (uint)imageBytes.Width, (uint)imageBytes.Height, 1, 0, 0);
                _textureView = _gd.ResourceFactory.CreateTextureView(_texture);
                _sampler = _gd.ResourceFactory.CreateSampler(new SamplerDescription(
                    SamplerAddressMode.Clamp, SamplerAddressMode.Clamp, SamplerAddressMode.Clamp,
                    SamplerFilter.MinPoint_MagPoint_MipPoint, null, 1, 0, 0, 0, SamplerBorderColor.TransparentBlack));
            }
            catch { }
        }

        private void CreateBuffers()
        {
            if (_parts.Count == 0) return;
            VertexCount = 0;
            IndexCount = 0;
            foreach (var p in _parts)
            {
                VertexCount += p.Positions.Length;
                IndexCount += p.Indices.Length;
            }

            // Bind-pose flat arrays (used only by the legacy single-instance Draw path).
            var flatPos = new List<Vector3>();
            var flatUv = new List<Vector2>();
            var flatIdx = new List<ushort>();
            foreach (var p in _parts)
            {
                int baseIndex = flatPos.Count;
                for (int i = 0; i < p.Positions.Length; i++)
                {
                    // Bind pose: vertices are local to the part's pivot, so restore the offset.
                    flatPos.Add(p.Positions[i] + p.Pivot);
                    flatUv.Add(p.Uvs[i]);
                }
                for (int i = 0; i < p.Indices.Length; i++)
                    flatIdx.Add((ushort)(baseIndex + p.Indices[i]));
            }

            var vertexData = new float[flatPos.Count * 5];
            for (int i = 0; i < flatPos.Count; i++)
            {
                int offset = i * 5;
                vertexData[offset + 0] = flatPos[i].X;
                vertexData[offset + 1] = flatPos[i].Y;
                vertexData[offset + 2] = flatPos[i].Z;
                vertexData[offset + 3] = flatUv[i].X;
                vertexData[offset + 4] = flatUv[i].Y;
            }

            _vertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(vertexData.Length * sizeof(float)), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_vertexBuffer, 0, vertexData);

            if (flatIdx.Count == 0)
            {
                for (int i = 0; i < flatPos.Count; i++) flatIdx.Add((ushort)i);
            }

            _indexBuffer?.Dispose();
            _indexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(flatIdx.Count * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_indexBuffer, 0, flatIdx.ToArray());
        }

        private DeviceBuffer? _instanceBuffer;
        private uint _instanceBufferSize;
        private float[] _transformedVertices = Array.Empty<float>();

        /// <summary>
        /// Writes this model's vertices transformed to (x,y,z) with the given yaw into a shared
        /// scratch buffer (pos + uv + shaded color, 9 floats per vertex), and copies its indices
        /// offset by <paramref name="baseVertex"/>. Each part is first animated: its joint's
        /// rotation (sampled from the walk cycle at <paramref name="animTime"/>) rotates the part
        /// around its pivot, so the mob swings its legs/tail properly instead of a flat bob.
        /// <paramref name="animBlend"/> (0..1) blends the sampled pose back toward the neutral
        /// rest pose, so an idle mob stands normally instead of freezing mid-stride.
        /// <paramref name="bodyPitch"/> (radians, positive = nose up) leans the WHOLE body onto a
        /// slope - the minecart's, which sits at the angle of the rail under it - and is applied
        /// after yaw in world space; see the note at the rotation itself for why.
        /// </summary>
        public void WriteInstance(float[] vertexScratch, ref int vf, ushort[] indexScratch, ref int ii, ref ushort baseVertex,
            float x, float y, float z, float yaw, float animTime = 0f, float animBlend = 1f, float nightDim = 1f,
            float headYawLocal = 0f, float hurtTimer = 0f, float headPitchLocal = 0f,
            bool isDead = false, float deathT = 0f, float deathRollDir = 0f, float scale = 1f, float bodyPitch = 0f)
        {
            float renderYaw = yaw + MathF.PI + YawCorrection;
            float cosY = MathF.Cos(renderYaw);
            float sinY = MathF.Sin(renderYaw);
            animBlend = Math.Clamp(animBlend, 0f, 1f);

            // Death roll: rotate the entire corpse sideways like WriteDuck/WritePlayer.
            float deathZ = isDead ? deathRollDir * (float)(Math.PI * 0.5) * (float)Math.Pow(deathT, 0.9) : 0f;
            float cosR = (float)Math.Cos(deathZ), sinR = (float)Math.Sin(deathZ);

            // Head pitch: AI gives positive=up, renderer convention is negative=up.
            float renderPitch = -headPitchLocal;
            float cosP = MathF.Cos(renderPitch), sinP = MathF.Sin(renderPitch);

            // Universal hurt flash — same formula as WriteDuck / WritePlayer so every mob type
            // (procedural duck, GLB coyote, zombie, future Blockbench models) gets the identical
            // red-tint treatment.
            float blink = hurtTimer > 0f ? ((float)Math.Sin(hurtTimer * 95.0f) > 0f ? 1f : 0.72f) : 0f;
            float flashBlend = hurtTimer > 0f ? Math.Clamp((hurtTimer / 0.20f) * blink, 0f, 1f) : 0f;
            float gbMul = 1f - 0.82f * flashBlend;

            // The head part yaws independently of the body (clamped by the mob's MaxHeadYaw), so
            // the mob can look around / track while walking like the hand-authored duck/player.
            float cosH = MathF.Cos(headYawLocal), sinH = MathF.Sin(headYawLocal);

            foreach (var part in _parts)
            {
                // Rigged mobs sample the GLB animation track on their joint node. Rig-less mobs
                // (zombie) get a procedural walk cycle: arms/legs swing around the part's pivot
                // from the walk phase, so ANY static Blockbench export can still move.
                Quaternion jointRot = Quaternion.Identity;
                Vector3 jointTrans = Vector3.Zero;
                Vector3 jointScale = Vector3.One;

                if (part.JointNode >= 0)
                {
                    SampleJoint(part.JointNode, animTime, out var sampleT, out var sampleR, out var sampleS);
                    if (part.JointFullTrs)
                    {
                        // Bone-parented part: the bone's TRS lives in ITS parent's frame, so lift it
                        // into model space via that parent's rotation. Quaternion.Multiply(a, b)
                        // applies b first and then a, which is the order the pivot rotation has to
                        // follow the bone's own (see the convention note in the loader).
                        jointRot = Quaternion.Multiply(part.JointParentRotation, sampleR);
                        jointTrans = Vector3.Transform(sampleT, part.JointParentRotation);
                        jointScale = sampleS;
                    }
                    else
                    {
                        jointRot = sampleR;
                    }
                }

                Quaternion staticRot = part.StaticRotation;
                if (part.JointNode < 0 && IsProceduralLimb(part.Name) && animBlend > 0f)
                {
                    jointRot = ProceduralLimbRotation(part.Name, animTime) * jointRot;
                }

                // Blend toward the rest pose: at blend 0 a plain joint goes back to identity, and a
                // bone-parented one returns to the bone's own rest TRS - so an idle mob stands
                // normally instead of freezing mid-stride, and a stopped locomotive settles back
                // onto its wheels.
                if (animBlend < 1f)
                {
                    jointRot = Quaternion.Slerp(part.JointRestRotation, jointRot, animBlend);
                    if (part.JointFullTrs)
                    {
                        jointTrans = Vector3.Lerp(part.JointRestTranslation, jointTrans, animBlend);
                        jointScale = Vector3.Lerp(part.JointRestScale, jointScale, animBlend);
                    }
                }

                bool isHead = part.Name.Contains("head", StringComparison.OrdinalIgnoreCase);

                for (int i = 0; i < part.Positions.Length; i++)
                {
                    var local = part.Positions[i];

                    // Skin: the mesh's vertices are ALREADY relative to the part's pivot (local
                    // y 0 = hip/top of a leg), so rotating the joint means rotating the vertex
                    // about the pivot origin and then placing it: v' = T + R(v). First apply the
                    // node's baked pose rotation (e.g. a zombie's arms-out stance), then the joint
                    // / procedural swing. Identity for static parts (v' = T + v, just place it).
                    if (staticRot != Quaternion.Identity)
                    {
                        local = Vector3.Transform(local, staticRot);
                    }

                    if (part.JointFullTrs)
                    {
                        // Bone-parented: world = pivot + jointT + rot(jointR, jointScale * x).
                        if (jointScale != Vector3.One)
                        {
                            local = new Vector3(local.X * jointScale.X, local.Y * jointScale.Y, local.Z * jointScale.Z);
                        }
                        if (jointRot != Quaternion.Identity)
                        {
                            local = Vector3.Transform(local, jointRot);
                        }
                        local += part.Pivot + jointTrans;
                    }
                    else if (jointRot != Quaternion.Identity)
                    {
                        local = part.Pivot + Vector3.Transform(local, jointRot);
                    }
                    else
                    {
                        local += part.Pivot;
                    }

                    // Head yaw about the part's pivot (neck) - yaw around +Y like the duck.
                    if (isHead && headYawLocal != 0f)
                    {
                        float hx = local.X - part.Pivot.X;
                        float hz = local.Z - part.Pivot.Z;
                        local.X = part.Pivot.X + hx * cosH + hz * sinH;
                        local.Z = part.Pivot.Z - hx * sinH + hz * cosH;
                    }
                    // Head pitch: rotation around the part's X axis for looking up/down.
                    if (isHead && renderPitch != 0f)
                    {
                        float hy = local.Y - part.Pivot.Y;
                        float hz = local.Z - part.Pivot.Z;
                        local.Y = part.Pivot.Y + hy * cosP + hz * sinP;
                        local.Z = part.Pivot.Z - hy * sinP + hz * cosP;
                    }

                    // Vertical origin nudge so the model's own origin lands on the entity's feet
                    // (see ModelYOffset). Done before the scale so it grows with the model, and
                    // before body pitch so the model still tips about its contact point.
                    local.Y += ModelYOffset;

                    // Uniform world scale grows the whole model about its feet origin. ModelScale is
                    // the per-type bake (mob config); scale is the per-instance multiplier, so brute
                    // variants render 2x without a separate model.
                    local *= ModelScale * scale;

                    float fx = local.X * cosY + local.Z * sinY;
                    float fy = local.Y;
                    float fz = -local.X * sinY + local.Z * cosY;

                    // Body pitch: lean the whole model onto a slope (the minecart rides a 45-degree
                    // ramp, so without this it slides through the hillside sitting flat).
                    //
                    // Done in WORLD space, after yaw, rather than as a pre-rotation in local space.
                    // The engine's yaw convention puts the body's forward at (sin yaw, cos yaw) - it
                    // is YawCorrection's whole job to put whichever local axis a given model's nose
                    // happens to sit on onto that world axis - so rotating about the horizontal
                    // axis perpendicular to (sin yaw, cos yaw) tips the model nose-up no matter
                    // which way its nose is modelled, and no per-model axis guess is needed.
                    // Pivot is the model origin: for a cart that is its bottom centre on the rail,
                    // so the wheels stay planted on the ramp surface. A positive angle raises the
                    // forward end, which is the sign CartEntity picks for facing uphill.
                    if (bodyPitch != 0f)
                    {
                        float fwdX = MathF.Sin(yaw), fwdZ = MathF.Cos(yaw);
                        // Work in the (forward, up, across) basis. A pitch only mixes forward with
                        // up; 'across' - the axis the body would roll about - passes through
                        // untouched, which is what keeps this a pure nose-up/down tilt.
                        float cp = MathF.Cos(bodyPitch), sp = MathF.Sin(bodyPitch);
                        float alongFwd = fx * fwdX + fz * fwdZ;
                        float across = -fx * fwdZ + fz * fwdX;
                        float a2 = alongFwd * cp - fy * sp;
                        float h2 = alongFwd * sp + fy * cp;
                        fx = a2 * fwdX - across * fwdZ;
                        fz = a2 * fwdZ + across * fwdX;
                        fy = h2;
                    }

                    // Death roll: rotate around Z axis (sideways tumble).
                    if (isDead)
                    {
                        float dfy = fy * cosR - fz * sinR;
                        float dfz = fy * sinR + fz * cosR;
                        fy = dfy; fz = dfz;
                    }

                    int offset = vf;
                    vertexScratch[offset + 0] = x + fx;
                    vertexScratch[offset + 1] = y + fy;
                    vertexScratch[offset + 2] = z + fz;
                    vertexScratch[offset + 3] = part.Uvs[i].X;
                    vertexScratch[offset + 4] = part.Uvs[i].Y;
                    float shade = part.Shades[i] <= 0f ? 1f : part.Shades[i];
                    shade *= nightDim;
                    vertexScratch[offset + 5] = shade;              // R: full
                    vertexScratch[offset + 6] = shade * gbMul;      // G: hurt-flash dim
                    vertexScratch[offset + 7] = shade * gbMul;      // B: hurt-flash dim
                    vertexScratch[offset + 8] = 1f;
                    vf += 9;
                }

                for (int i = 0; i < part.Indices.Length; i++)
                {
                    indexScratch[ii++] = (ushort)(baseVertex + part.Indices[i]);
                }
                baseVertex += (ushort)part.Positions.Length;
            }
        }

        // ---- Procedural walk-cycle fallback (for static GLB mobs with no animation rig) ----

        /// <summary>True when a part's name marks it as a swingable limb ("arm"/"leg").</summary>
        private static bool IsProceduralLimb(string name)
        {
            return name.Contains("arm", StringComparison.OrdinalIgnoreCase)
                || name.Contains("leg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Swings a limb around the X axis (forward/back) from the walk phase. Right limbs lead,
        /// left limbs trail, arms swing opposite to legs on the same side so the gait reads like a
        /// real walk. Amplitude is applied to the part's local X rotation.
        /// </summary>
        private Quaternion ProceduralLimbRotation(string name, float animTime)
        {
            bool isArm = name.Contains("arm", StringComparison.OrdinalIgnoreCase);
            bool isRight = name.Contains("right", StringComparison.OrdinalIgnoreCase);
            float phase = animTime * ProceduralSwingRate;
            float swing = MathF.Sin(phase + (isArm ? 0f : MathF.PI));
            swing *= isRight ? ProceduralSwingAmount : -ProceduralSwingAmount;
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, swing);
        }

        /// <summary>Radians/second of walk-phase → limb swing (tuned so a 1.33s coyote trot reads).</summary>
        public float ProceduralSwingRate = MathF.PI * 2f / 1.33f;
        /// <summary>Max swing amplitude in radians for rig-less limbs (≈ +-23°).</summary>
        public float ProceduralSwingAmount = 0.4f;

        /// <summary>
        /// Additional yaw (radians) applied on top of the standard yaw+PI rotation, so a model whose
        /// front is NOT +Z (e.g. a Blockbench model whose nose points +X) can be corrected.
        ///
        /// The coyote's nose points +X. With renderYaw = yaw + PI the +X vertex maps to
        /// (-cos, sin) = the forward axis (sin, cos) rotated 90deg LEFT - hence the "strafing left"
        /// look. Adding +PI/2 (renderYaw = yaw + 3PI/2) makes +X map to (sin, cos) = forward.
        /// </summary>
        public float YawCorrection = MathF.PI / 2f;

        /// <summary>Uniform world scale applied to the model at draw time (1.0 = raw GLB size).</summary>
        public float ModelScale = 1.0f;

        /// <summary>
        /// Vertical offset (blocks) applied to every vertex at draw time so the model's own origin
        /// can be lined up with the entity's feet. Blockbench exports keep the origin wherever the
        /// author left it - the locomotive's wheels hang at y=0.125, so it needs -0.125 to sit on
        /// the rail instead of floating a eighth-block up. 0 = raw GLB origin.
        /// </summary>
        public float ModelYOffset = 0f;

        public void Draw(CommandList cl, ResourceSet? textureSet, float x, float y, float z, float yaw)
        {
            if (_vertexBuffer == null || VertexCount == 0) return;
            cl.SetVertexBuffer(0, _vertexBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            if (textureSet != null) cl.SetGraphicsResourceSet(1, textureSet);
            cl.DrawIndexed((uint)IndexCount, 1, 0, 0, 0);
        }

        public TextureView? TextureView => _textureView;

        public ResourceSet? TextureSet =>
            _textureView != null && _sampler != null
                ? _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    _textureLayout ??= _gd.ResourceFactory.CreateResourceLayout(new ResourceLayoutDescription(
                        new ResourceLayoutElementDescription("uTex", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
                        new ResourceLayoutElementDescription("uTexSampler", ResourceKind.Sampler, ShaderStages.Fragment))),
                    _textureView, _sampler))
                : null;

        public void Dispose()
        {
            _vertexBuffer?.Dispose(); _indexBuffer?.Dispose(); _instanceBuffer?.Dispose();
            _sampler?.Dispose(); _textureView?.Dispose(); _texture?.Dispose(); _textureLayout?.Dispose();
        }
    }
}

