using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Veldrid;

namespace CubeApp
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
        private sealed class ModelPart
        {
            public Vector3[] Positions = Array.Empty<Vector3>();
            public Vector2[] Uvs = Array.Empty<Vector2>();
            public ushort[] Indices = Array.Empty<ushort>();
            public float[] Shades = Array.Empty<float>();
            public Vector3 Pivot;
            public int JointNode = -1; // animation joint node, or -1 for static parts
            public string Name = "";   // mesh node name (e.g. "leftarm") for procedural limbs
            public Quaternion StaticRotation = Quaternion.Identity; // node's baked pose rotation
        }

        private readonly List<ModelPart> _parts = new();
        private readonly List<float> _allShades = new();

        // Keyframed joint rotations: jointNode -> (times, quaternions). Sampled by animTime.
        private sealed class JointTrack
        {
            public float[] Times = Array.Empty<float>();
            public Quaternion[] Rotations = Array.Empty<Quaternion>();
            public float Duration;
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

                // Blockbench exports each body part as its OWN mesh, placed by a NODE translation
                // (the part's pivot/origin in blocks). Each skinned part has a child JOINT node
                // that the walk animation rotates. We collect per-mesh: pivot translation + joint
                // node index, so animating = rotating the part around its pivot.
                _parts.Clear();
                _jointTracks.Clear();
                int[] meshJoint = Array.Empty<int>();   // meshIndex -> joint node index (or -1)
                Vector3[] meshPivot = Array.Empty<Vector3>();
                Quaternion[] meshStaticRotation = Array.Empty<Quaternion>();
                string[] meshName = Array.Empty<string>();

                if (root.TryGetProperty("nodes", out var nodes))
                {
                    // First pass: for every node that references a mesh, find its pivot, its baked
                    // pose rotation, its node name (used for the procedural walk fallback) and the
                    // skinned joint (the node's FIRST child - Blockbench puts the joint there).
                    var nodeList = new List<(Vector3 t, Quaternion r, string name, int mesh, int child)>();
                    foreach (var node in nodes.EnumerateArray())
                    {
                        Vector3 t = Vector3.Zero;
                        if (node.TryGetProperty("translation", out var tArr) && tArr.GetArrayLength() >= 3)
                            t = new Vector3(tArr[0].GetSingle(), tArr[1].GetSingle(), tArr[2].GetSingle());
                        Quaternion r = Quaternion.Identity;
                        if (node.TryGetProperty("rotation", out var rArr) && rArr.GetArrayLength() >= 4)
                            r = new Quaternion(rArr[0].GetSingle(), rArr[1].GetSingle(), rArr[2].GetSingle(), rArr[3].GetSingle());
                        string name = "";
                        if (node.TryGetProperty("name", out var nameProp))
                            name = nameProp.GetString() ?? "";
                        int mesh = -1;
                        if (node.TryGetProperty("mesh", out var mProp)) mesh = mProp.GetInt32();
                        int child = -1;
                        if (node.TryGetProperty("children", out var cArr) && cArr.GetArrayLength() >= 1)
                            child = cArr[0].GetInt32();
                        nodeList.Add((t, r, name, mesh, child));
                    }

                    meshPivot = new Vector3[meshes.GetArrayLength()];
                    meshJoint = new int[meshes.GetArrayLength()];
                    meshStaticRotation = new Quaternion[meshes.GetArrayLength()];
                    meshName = new string[meshes.GetArrayLength()];
                    for (int i = 0; i < meshJoint.Length; i++) { meshJoint[i] = -1; meshStaticRotation[i] = Quaternion.Identity; meshName[i] = ""; }
                    for (int i = 0; i < nodeList.Count; i++)
                    {
                        if (nodeList[i].mesh < 0 || nodeList[i].mesh >= meshJoint.Length) continue;
                        meshPivot[nodeList[i].mesh] = nodeList[i].t;
                        meshJoint[nodeList[i].mesh] = nodeList[i].child;
                        meshStaticRotation[nodeList[i].mesh] = nodeList[i].r;
                        meshName[nodeList[i].mesh] = nodeList[i].name;
                    }
                }

                int meshIndex = 0;
                foreach (var mesh in meshes.EnumerateArray())
                {
                    Vector3 pivot = meshPivot.Length > meshIndex ? meshPivot[meshIndex] : Vector3.Zero;
                    int joint = meshJoint.Length > meshIndex ? meshJoint[meshIndex] : -1;
                    Quaternion staticRot = meshStaticRotation.Length > meshIndex ? meshStaticRotation[meshIndex] : Quaternion.Identity;
                    string partName = meshName.Length > meshIndex ? meshName[meshIndex] : "";
                    if (!mesh.TryGetProperty("primitives", out var primitives)) continue;
                    
                    foreach (var primitive in primitives.EnumerateArray())
                    {
                        ExtractPrimitive(primitive, bufferViews, accessors, buffers, glbBytes, binaryOffset, pivot, joint, staticRot, partName);
                    }
                    meshIndex++;
                }

                // Parse the animation: joint node -> rotation keyframes (times + quaternions).
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
                            if (pathProp.GetString() != "rotation") continue;
                            int jointNode = nodeProp.GetInt32();
                            int samplerIdx = channel.GetProperty("sampler").GetInt32();
                            if (samplerIdx < 0 || samplerIdx >= animSamplers.GetArrayLength()) continue;
                            var sampler = animSamplers[samplerIdx];
                            int inputAcc = sampler.GetProperty("input").GetInt32();
                            int outputAcc = sampler.GetProperty("output").GetInt32();
                            var track = new JointTrack();
                            track.Times = ReadFloatArray(accessors, bufferViews, glbBytes, binaryOffset, inputAcc);
                            track.Rotations = ReadQuaternionArray(accessors, bufferViews, glbBytes, binaryOffset, outputAcc);
                            if (track.Times.Length > 0) track.Duration = track.Times[track.Times.Length - 1];
                            _jointTracks[jointNode] = track;
                        }
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

        /// <summary>
        /// Samples the given joint's rotation at <paramref name="animTime"/> (seconds, wrapped to
        /// the track duration). Returns identity for a static joint.
        /// </summary>
        public Quaternion SampleJointRotation(int jointNode, float animTime)
        {
            if (!_jointTracks.TryGetValue(jointNode, out var track) || track.Rotations.Length == 0)
                return Quaternion.Identity;

            float t = track.Duration > 0.0001f ? animTime % track.Duration : 0f;
            int n = track.Times.Length;
            if (n == 1) return track.Rotations[0];

            // Find the surrounding keyframes (binary-ish: the coyote's 24 keys are small enough to scan).
            int hi = 1;
            while (hi < n && track.Times[hi] < t) hi++;
            if (hi >= n) return track.Rotations[n - 1];
            int lo = hi - 1;
            if (lo < 0) return track.Rotations[0];

            float span = track.Times[hi] - track.Times[lo];
            float s = span > 0.0001f ? (t - track.Times[lo]) / span : 0f;
            return Quaternion.Slerp(track.Rotations[lo], track.Rotations[hi], s);
        }

        private void ExtractPrimitive(JsonElement primitive, JsonElement bufferViews, 
            JsonElement accessors, JsonElement buffers, byte[] glbBytes, uint binaryOffset,
            Vector3 pivot, int jointNode, Quaternion staticRotation, string partName)
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
                
                // Read positions into LOCAL space (not offset by pivot - the pivot rotation is
                // applied at animation/write time so parts swing around their joint origin).
                var part = new ModelPart { Pivot = pivot, JointNode = jointNode, StaticRotation = staticRotation, Name = partName };
                part.Positions = new Vector3[posCount];
                part.Shades = new float[posCount];
                int posStart = (int)(binaryOffset + posByteOffset);
                for (int i = 0; i < posCount; i++)
                {
                    int offset = posStart + i * 12;
                    float x = BitConverter.ToSingle(glbBytes, offset);
                    float y = BitConverter.ToSingle(glbBytes, offset + 4);
                    float z = BitConverter.ToSingle(glbBytes, offset + 8);
                    part.Positions[i] = new Vector3(x, y, z);
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
        /// </summary>
        public void WriteInstance(float[] vertexScratch, ref int vf, ushort[] indexScratch, ref int ii, ref ushort baseVertex,
            float x, float y, float z, float yaw, float animTime = 0f, float animBlend = 1f, float nightDim = 1f,
            float headYawLocal = 0f, float hurtTimer = 0f, float headPitchLocal = 0f,
            bool isDead = false, float deathT = 0f, float deathRollDir = 0f, float scale = 1f)
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
                // Rigged mobs (coyote) sample the GLB animation track on their joint node.
                // Rig-less mobs (zombie) get a procedural walk cycle: arms/legs swing around the
                // part's pivot from the walk phase, so ANY static Blockbench export can still move.
                Quaternion jointRot = part.JointNode >= 0 ? SampleJointRotation(part.JointNode, animTime) : Quaternion.Identity;
                Quaternion staticRot = part.StaticRotation;
                if (part.JointNode < 0 && IsProceduralLimb(part.Name) && animBlend > 0f)
                {
                    jointRot = ProceduralLimbRotation(part.Name, animTime) * jointRot;
                }

                // Blend toward rest pose: at blend 0 the joint is identity (neutral stance).
                if (animBlend < 1f)
                {
                    jointRot = Quaternion.Slerp(Quaternion.Identity, jointRot, animBlend);
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

                    if (jointRot != Quaternion.Identity)
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

                    // Uniform world scale grows the whole model about its feet origin. ModelScale is
                    // the per-type bake (mob config); scale is the per-instance multiplier, so brute
                    // variants render 2x without a separate model.
                    local *= ModelScale * scale;

                    float fx = local.X * cosY + local.Z * sinY;
                    float fy = local.Y;
                    float fz = -local.X * sinY + local.Z * cosY;

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

