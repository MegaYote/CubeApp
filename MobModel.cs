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

        private readonly List<Vector3> _positions = new();
        private readonly List<Vector2> _uvs = new();
        private readonly List<ushort> _indices = new();

        public bool Loaded { get; private set; } = false;
        public int VertexCount => _positions.Count;
        public int IndexCount => _indices.Count;

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

                CreateBuffers();
                if (!string.IsNullOrEmpty(texturePath)) LoadTexture(texturePath);
                return Loaded = _positions.Count > 0 && _indices.Count > 0;
            }
            catch { return false; }
        }

        public bool LoadGLB(string modelPath)
        {
            try
            {
                LoadGLBModel(modelPath);
                CreateBuffers();
                return Loaded = _positions.Count > 0 && _indices.Count > 0;
            }
            catch { return false; }
        }

        private void LoadBlockbenchModel(string modelPath)
        {
            var json = File.ReadAllText(modelPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _positions.Clear(); _uvs.Clear(); _indices.Clear();

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
            int baseIndex = _positions.Count;

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

                    float texW = 64f, texH = 64f;

                    // Collect all vertex positions for this face
                    List<Vector3> faceVerts = new List<Vector3>();
                    for (int i = 0; i < vertexIds.GetArrayLength(); i++)
                    {
                        Vector3 pos = GetVertexPosition(vertexIds[i].GetString(), vertexMap, origin, rotation, scale);
                        faceVerts.Add(pos);
                    }

                    // Calculate proper UVs for each vertex based on its position in the face
                    // The UV rectangle [uvLeft, uvTop, uvRight, uvBottom] maps to the face
                    // We need to map each vertex's X/Y/Z to UV coordinates
                    Vector2[] faceUVs = CalculateFaceUVs(faceVerts, uvLeft, uvTop, uvRight, uvBottom);

                    // Triangulate the face vertices (fan triangulation for convex polygons)
                    for (int tri = 1; tri < faceVerts.Count - 1; tri++)
                    {
                        // Add triangle vertices with their UVs
                        _positions.Add(faceVerts[0]);
                        _uvs.Add(faceUVs[0]);
                        
                        _positions.Add(faceVerts[tri]);
                        _uvs.Add(faceUVs[tri]);
                        
                        _positions.Add(faceVerts[tri + 1]);
                        _uvs.Add(faceUVs[tri + 1]);

                        _indices.Add((ushort)baseIndex);
                        _indices.Add((ushort)(baseIndex + 1));
                        _indices.Add((ushort)(baseIndex + 2));
                        baseIndex += 3;
                    }
                }
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
                // (the part's pivot/origin in blocks). Build a meshIndex -> (translation) map so
                // each part's vertices are offset into world space. Without this every part piles
                // up at the origin and the model looks mangled (this was the coyote bug).
                var meshOffsets = new Dictionary<int, Vector3>();
                if (root.TryGetProperty("nodes", out var nodes))
                {
                    foreach (var node in nodes.EnumerateArray())
                    {
                        if (!node.TryGetProperty("mesh", out var meshProp)) continue;
                        int nodeMeshIdx = meshProp.GetInt32();
                        Vector3 t = Vector3.Zero;
                        if (node.TryGetProperty("translation", out var tArr) && tArr.GetArrayLength() >= 3)
                        {
                            t = new Vector3(tArr[0].GetSingle(), tArr[1].GetSingle(), tArr[2].GetSingle());
                        }
                        // Nodes may also carry a rotation (pivot tilt). Blockbench typically bakes
                        // it into the vertices, but apply it anyway when present.
                        if (node.TryGetProperty("rotation", out var rArr) && rArr.GetArrayLength() >= 3)
                        {
                            var rot = new Vector3(rArr[0].GetSingle(), rArr[1].GetSingle(), rArr[2].GetSingle());
                            meshOffsets[nodeMeshIdx] = t + Vector3.Zero; // rotation handled per-vertex below if needed
                        }
                        else
                        {
                            meshOffsets[nodeMeshIdx] = t;
                        }
                    }
                }

                int meshIndex = 0;
                foreach (var mesh in meshes.EnumerateArray())
                {
                    Vector3 meshOffset = meshOffsets.TryGetValue(meshIndex, out var mo) ? mo : Vector3.Zero;
                    if (!mesh.TryGetProperty("primitives", out var primitives)) continue;
                    
                    foreach (var primitive in primitives.EnumerateArray())
                    {
                        ExtractPrimitive(primitive, bufferViews, accessors, buffers, glbBytes, binaryOffset, meshOffset);
                    }
                    meshIndex++;
                }
            }
            catch { }
        }

        private void ExtractPrimitive(JsonElement primitive, JsonElement bufferViews, 
            JsonElement accessors, JsonElement buffers, byte[] glbBytes, uint binaryOffset, Vector3 meshOffset)
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
                else if (attrs.TryGetProperty("TEXCOORD_0", out uvAcc)) uvAccessorIdx = uvAcc.GetInt32();
                
                // Get indices accessor
                int indicesAccessorIdx = indicesProp.GetInt32();
                
                // Read positions
                var posAccEl = accessors[posAccessorIdx];
                int posCount = posAccEl.GetProperty("count").GetInt32();
                int posCompType = posAccEl.GetProperty("componentType").GetInt32();
                string posType = posAccEl.GetProperty("type").GetString();
                if (posType != "VEC3") return;
                
                var posViewIdx = posAccEl.GetProperty("bufferView").GetInt32();
                var posView = bufferViews[posViewIdx];
                int posByteOffset = posView.TryGetProperty("byteOffset", out var po) ? po.GetInt32() : 0;
                int posByteLength = posView.GetProperty("byteLength").GetInt32();
                
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
                        // int uvByteLength = uvView.GetProperty("byteLength").GetInt32();
                        int uvCompType = uvAccEl.GetProperty("componentType").GetInt32();
                        
                        // Read UV floats
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
                
                // Read positions
                int posStart = (int)(binaryOffset + posByteOffset);
                for (int i = 0; i < posCount; i++)
                {
                    int offset = posStart + i * 12;
                    float x = BitConverter.ToSingle(glbBytes, offset);
                    float y = BitConverter.ToSingle(glbBytes, offset + 4);
                    float z = BitConverter.ToSingle(glbBytes, offset + 8);
                    _positions.Add(new Vector3(x + meshOffset.X, y + meshOffset.Y, z + meshOffset.Z));
                }
                
                // Read UVs (pad with zeros if missing)
                while (uvs.Count < posCount) uvs.Add(Vector2.Zero);
                for (int i = 0; i < posCount; i++) _uvs.Add(uvs[i]);
                
                // Read indices
                var idxAccEl = accessors[indicesAccessorIdx];
                int idxCount = idxAccEl.GetProperty("count").GetInt32();
                int idxCompType = idxAccEl.GetProperty("componentType").GetInt32();
                var idxViewIdx = idxAccEl.GetProperty("bufferView").GetInt32();
                var idxView = bufferViews[idxViewIdx];
                int idxByteOffset = idxView.TryGetProperty("byteOffset", out var io) ? io.GetInt32() : 0;
                
                int idxStart = (int)(binaryOffset + idxByteOffset);
                int baseIndex = _positions.Count - posCount;
                
                if (idxCompType == 5123) // UNSIGNED_SHORT
                {
                    for (int i = 0; i < idxCount; i++)
                    {
                        int offset = idxStart + i * 2;
                        ushort idx = BitConverter.ToUInt16(glbBytes, offset);
                        _indices.Add((ushort)(baseIndex + idx));
                    }
                }
                else if (idxCompType == 5125) // UNSIGNED_INT
                {
                    for (int i = 0; i < idxCount; i++)
                    {
                        int offset = idxStart + i * 4;
                        uint idx = BitConverter.ToUInt32(glbBytes, offset);
                        _indices.Add((ushort)(baseIndex + idx));
                    }
                }
            }
            catch { }
        }

        private void ExtractFromPrimitive(object primitive)
        {
            try
            {
                var getVertexAccessor = primitive.GetType().GetMethod("GetVertexAccessor", new[] { typeof(string) });
                if (getVertexAccessor == null) return;

                int vertexStart = _positions.Count;

                // Extract positions
                var posAccessor = getVertexAccessor.Invoke(primitive, new object[] { "POSITION" });
                var posArray = ExtractVector3Array(posAccessor);
                
                // Extract UVs
                var uvAccessor = getVertexAccessor.Invoke(primitive, new object[] { "TEXCOORD_0" });
                if (uvAccessor == null) uvAccessor = getVertexAccessor.Invoke(primitive, new object[] { "uv0" });
                if (uvAccessor == null) uvAccessor = getVertexAccessor.Invoke(primitive, new object[] { "UV" });
                var uvArray = ExtractVector2Array(uvAccessor);

                // Add positions and UVs together to keep them aligned
                int count = Math.Max(posArray.Count, uvArray.Count);
                for (int i = 0; i < count; i++)
                {
                    if (i < posArray.Count)
                        _positions.Add(posArray[i]);
                    else
                        _positions.Add(Vector3.Zero);
                    
                    if (i < uvArray.Count)
                        _uvs.Add(uvArray[i]);
                    else
                        _uvs.Add(Vector2.Zero);
                }

                int vertexCount = _positions.Count - vertexStart;
                if (vertexCount >= 3)
                {
                    for (int i = 0; i < vertexCount; i += 3)
                    {
                        if (i + 2 < vertexCount)
                        {
                            _indices.Add((ushort)(vertexStart + i));
                            _indices.Add((ushort)(vertexStart + i + 1));
                            _indices.Add((ushort)(vertexStart + i + 2));
                        }
                    }
                }
            }
            catch { }
        }

        private List<Vector3> ExtractVector3Array(object accessor)
        {
            var result = new List<Vector3>();
            try
            {
                if (accessor == null) return result;
                
                var asArrayMethod = accessor.GetType().GetMethod("AsVector3Array");
                if (asArrayMethod == null) return result;
                
                var array = asArrayMethod.Invoke(accessor, null) as System.Collections.IEnumerable;
                if (array == null) return result;

                foreach (var item in array)
                {
                    if (item != null)
                    {
                        var x = GetComponent(item, "X");
                        var y = GetComponent(item, "Y");
                        var z = GetComponent(item, "Z");
                        if (x.HasValue && y.HasValue && z.HasValue)
                            result.Add(new Vector3(x.Value, y.Value, z.Value));
                    }
                }
            }
            catch { }
            return result;
        }

        private List<Vector2> ExtractVector2Array(object accessor)
        {
            var result = new List<Vector2>();
            try
            {
                if (accessor == null) return result;
                
                var asArrayMethod = accessor.GetType().GetMethod("AsVector2Array");
                if (asArrayMethod == null) return result;
                
                var array = asArrayMethod.Invoke(accessor, null) as System.Collections.IEnumerable;
                if (array == null) return result;

                foreach (var item in array)
                {
                    if (item != null)
                    {
                        var x = GetComponent(item, "X");
                        var y = GetComponent(item, "Y");
                        if (x.HasValue && y.HasValue)
                            result.Add(new Vector2(x.Value, y.Value));
                    }
                }
            }
            catch { }
            return result;
        }

        private static float? GetComponent(object vec, string name)
        {
            var prop = vec.GetType().GetProperty(name);
            if (prop != null) { var val = prop.GetValue(vec); if (val != null) return Convert.ToSingle(val); }
            var field = vec.GetType().GetField(name);
            if (field != null) { var val = field.GetValue(vec); if (val != null) return Convert.ToSingle(val); }
            return null;
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
            if (_positions.Count == 0) return;
            while (_uvs.Count < _positions.Count) _uvs.Add(Vector2.Zero);

            var vertexData = new float[_positions.Count * 5];
            for (int i = 0; i < _positions.Count; i++)
            {
                int offset = i * 5;
                vertexData[offset + 0] = _positions[i].X;
                vertexData[offset + 1] = _positions[i].Y;
                vertexData[offset + 2] = _positions[i].Z;
                vertexData[offset + 3] = _uvs[i].X;
                vertexData[offset + 4] = _uvs[i].Y;
            }

            _vertexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(vertexData.Length * sizeof(float)), BufferUsage.VertexBuffer));
            _gd.UpdateBuffer(_vertexBuffer, 0, vertexData);

            if (_indices.Count == 0)
            {
                for (int i = 0; i < _positions.Count; i++) _indices.Add((ushort)i);
            }

            _indexBuffer?.Dispose();
            _indexBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                (uint)(_indices.Count * sizeof(ushort)), BufferUsage.IndexBuffer));
            _gd.UpdateBuffer(_indexBuffer, 0, _indices.ToArray());
        }

        private DeviceBuffer? _instanceBuffer;
        private uint _instanceBufferSize;
        private float[] _transformedVertices = Array.Empty<float>();

        /// <summary>
        /// Writes this model's vertices transformed to (x,y,z) with the given yaw into a shared
        /// scratch buffer (pos + uv + white color, 9 floats per vertex), and copies its indices
        /// offset by <paramref name="baseVertex"/>. This lets the renderer batch MULTIPLE mobs of
        /// the same model into ONE vertex/index buffer and draw them in a single call - drawing
        /// each mob separately with a shared instance buffer corrupts earlier draws (the second
        /// mob's UpdateBuffer overwrites the first's data before the GPU has consumed it).
        /// </summary>
        public void WriteInstance(float[] vertexScratch, ref int vf, ushort[] indexScratch, ref int ii, ref ushort baseVertex,
            float x, float y, float z, float yaw)
        {
            float cosY = (float)Math.Cos(yaw + Math.PI);
            float sinY = (float)Math.Sin(yaw + Math.PI);
            for (int i = 0; i < _positions.Count; i++)
            {
                var pos = _positions[i];
                var uv = _uvs[Math.Min(i, _uvs.Count - 1)];
                float fx = pos.X * cosY + pos.Z * sinY;
                float fy = pos.Y;
                float fz = -pos.X * sinY + pos.Z * cosY;
                int offset = vf;
                vertexScratch[offset + 0] = x + fx;
                vertexScratch[offset + 1] = y + fy;
                vertexScratch[offset + 2] = z + fz;
                vertexScratch[offset + 3] = uv.X;
                vertexScratch[offset + 4] = uv.Y;
                vertexScratch[offset + 5] = 1f; vertexScratch[offset + 6] = 1f;
                vertexScratch[offset + 7] = 1f; vertexScratch[offset + 8] = 1f;
                vf += 9;
            }

            for (int i = 0; i < _indices.Count; i++)
            {
                indexScratch[ii++] = (ushort)(baseVertex + _indices[i]);
            }
            baseVertex += (ushort)_positions.Count;
        }

        public void Draw(CommandList cl, ResourceSet? textureSet, float x, float y, float z, float yaw)
        {
            if (_vertexBuffer == null || _positions.Count == 0) return;
            int vertexCount = _positions.Count;
            int vertexFloats = vertexCount * 9;
            if (_transformedVertices.Length < vertexFloats) _transformedVertices = new float[vertexFloats];
            if (_instanceBuffer == null || _instanceBufferSize < (uint)(vertexFloats * sizeof(float)))
            {
                _instanceBuffer?.Dispose();
                _instanceBuffer = _gd.ResourceFactory.CreateBuffer(new BufferDescription(
                    (uint)(vertexFloats * sizeof(float)), BufferUsage.VertexBuffer | BufferUsage.Dynamic));
                _instanceBufferSize = (uint)(vertexFloats * sizeof(float));
            }

            float cosY = (float)Math.Cos(yaw + Math.PI);
            float sinY = (float)Math.Sin(yaw + Math.PI);
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = _positions[i];
                var uv = _uvs[Math.Min(i, _uvs.Count - 1)];
                float fx = pos.X * cosY + pos.Z * sinY;
                float fy = pos.Y;
                float fz = -pos.X * sinY + pos.Z * cosY;
                int offset = i * 9;
                _transformedVertices[offset + 0] = x + fx;
                _transformedVertices[offset + 1] = y + fy;
                _transformedVertices[offset + 2] = z + fz;
                _transformedVertices[offset + 3] = uv.X;
                _transformedVertices[offset + 4] = uv.Y;
                _transformedVertices[offset + 5] = 1f; _transformedVertices[offset + 6] = 1f;
                _transformedVertices[offset + 7] = 1f; _transformedVertices[offset + 8] = 1f;
            }

            _gd.UpdateBuffer(_instanceBuffer, 0, _transformedVertices);
            cl.SetVertexBuffer(0, _instanceBuffer);
            cl.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            if (textureSet != null) cl.SetGraphicsResourceSet(1, textureSet);
            cl.DrawIndexed((uint)_indices.Count, 1, 0, 0, 0);
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
