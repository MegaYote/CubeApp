using System;
using System.Numerics;
using Veldrid;

namespace Cubuild.Renderer
{
    /// <summary>
    /// First-person held item / block viewmodel.
    ///
    /// Whatever the player has selected in the hotbar is drawn in the fist:
    ///  - a plain block renders as a TEXTURED CUBE (terrain atlas), tilted so the top and the
    ///    left faces read in 3D;
    ///  - a genuine item (tools, raw ore, ...) renders as a flat CARD (items.png) angled the
    ///    same way - extruded into a thin slab (one sprite pixel by default, like Minecraft's
    ///    extruded items) so it reads as a physical object instead of a decal.
    ///
    /// The grip point is derived from the ARM MESH itself - the far end of the arm cube - and
    /// pushed through the exact same pose transform the arm vertices use, so the item stays in
    /// the fist no matter how the arm is tuned (F8 sliders), and it inherits the mining chop and
    /// the walk bob for free.
    ///
    /// Everything is drawn in CAMERA space with depth testing OFF (like the arm), so terrain can
    /// never swallow the held item and the world fog (which measures world-space distances) can
    /// never wash it out.
    /// </summary>
    public sealed partial class VeldridRenderer : IRenderer, IDisposable
    {
        /// <summary>Fist position in the arm's local space (pivot at the origin).</summary>
        private void ComputeGripPoint()
        {
            if (_gripComputed || _handMesh.Length < 9) return;

            // The arm is a box hanging off the shoulder pivot: the fist is the centre of its far
            // (local -Y) face. The mesh is unwelded (6 faces x 4 verts), so the far face's verts
            // are found by taking every vertex at the minimum Y.
            float minY = float.MaxValue;
            for (int i = 0; i < _handMesh.Length; i += 9)
            {
                if (_handMesh[i + 1] < minY) minY = _handMesh[i + 1];
            }
            float sumX = 0f, sumZ = 0f;
            int count = 0;
            const float eps = 1e-5f;
            for (int i = 0; i < _handMesh.Length; i += 9)
            {
                if (_handMesh[i + 1] > minY + eps) continue;
                sumX += _handMesh[i];
                sumZ += _handMesh[i + 2];
                count++;
            }
            _gripX = count > 0 ? sumX / count : 0f;
            _gripY = minY;
            _gripZ = count > 0 ? sumZ / count : 0f;
            _gripComputed = true;
        }

        /// <summary>
        /// The arm's pose transform, shared by the arm vertices and the held item so the item
        /// rides the exact same motion (flip -> pitch -> yaw -> roll -> shoulder anchor).
        /// </summary>
        private static Vector3 ArmToCamera(float x, float y, float z, float scale,
            float cp, float sp, float cy, float syy, float cr, float sr,
            float ox, float oy, float oz)
        {
            x *= scale; y *= scale; z *= scale;
            float xf = -x;
            float yf = -y;
            float zf = z;
            float y1 = yf * cp - zf * sp;
            float z1 = yf * sp + zf * cp;
            float x2 = xf * cy + z1 * syy;
            float z2 = -xf * syy + z1 * cy;
            float x3 = x2 * cr - y1 * sr;
            float y3 = x2 * sr + y1 * cr;
            return new Vector3(x3 + ox, y3 + oy, z2 + oz);
        }

        /// <summary>
        /// Draws the selected hotbar stack in the fist. <paramref name="grip"/> is the fist in
        /// camera space (already carrying the swing bob); <paramref name="qFist"/> is the
        /// vanilla attack rotation around that fist. The item is glued to the hand and
        /// inherits the whole chop from it - nothing kicks independently.
        /// </summary>
        private void DrawHeldItem(CommandList cl, Vector3 grip, Quaternion qFist)
        {
            var hotbar = _hud.Hotbar;
            if (hotbar == null || hotbar.Count == 0) return;
            int sel = _hud.SelectedSlot;
            if (sel < 0 || sel >= hotbar.Count) return;
            int id = hotbar[sel];
            if (id <= 0) return;

            var tile = ItemRegistry.GetTile(id, out bool fromItems);
            if (tile.Width <= 0 || tile.Height <= 0) return;

            // Genuine items (and blocks that ship their own items.png art) are flat sprites;
            // cross-quad blocks (saplings, torches) are sprites too - a cube would look wrong.
            bool itemsAtlas = fromItems;
            bool cross = !fromItems && BlockRegistry.IsCross(id);
            bool sprite = itemsAtlas || cross;

            var textureSet = itemsAtlas ? _itemsTextureSet : _textureSet;
            if (textureSet == null) return;
            var pipeline = sprite ? _heldItemPipeline : _heldBlockPipeline;
            if (pipeline == null || _heldInstanceBuffer == null) return;
            if (sprite && (_heldCardVertexBuffer == null || _heldCardIndexBuffer == null)) return;
            if (!sprite && (_itemDropVertexBuffer == null || _itemDropIndexBuffer == null)) return;

            float atlasW = Math.Max(1f, itemsAtlas ? _itemsAtlasPixelsW : _atlasWidth);
            float atlasH = Math.Max(1f, itemsAtlas ? _itemsAtlasPixelsH : _atlasHeight);

            // ---- Per-item MC display transform (items.json) --------------------------
            // "heldRotation"/"heldTranslation"/"heldScale" carry the exact numbers
            // Minecraft's item models ship for display.firstperson_righthand:
            // rotation (0,-90,25), translation (1.13/16, 3.2/16, 1.13/16), scale 0.68.
            // Plain blocks use the vanilla block display: rotation (0,45,0), no translation
            // (the center offset below places the cube where the vanilla corner-anchored
            // one sits).
            var dRot = ItemRegistry.HeldRotationOf(id);        // degrees (x=pitch, y=yaw, z=roll)
            var dTrans = ItemRegistry.HeldTranslationOf(id);   // world units
            float dScale = ItemRegistry.HeldScaleOf(id);       // multiplier (1 = none)
            if (!sprite && dRot == (0, 0, 0))
            {
                dRot = (0f, 45f, 0f); // vanilla held-block yaw, flat otherwise
            }

            // Display rotation, degrees -> radians (CreateFromYawPitchRoll wants yaw,pitch,roll).
            Quaternion qDisplay = Quaternion.Identity;
            if (dRot != (0, 0, 0))
            {
                const float Deg2Rad = MathF.PI / 180f;
                qDisplay = Quaternion.CreateFromYawPitchRoll(
                    dRot.y * Deg2Rad, dRot.x * Deg2Rad, dRot.z * Deg2Rad);
            }

            // Total rotation in vanilla order: the swing's attack rotation wraps the item's
            // own display rotation; the F8 yaw/pitch/roll nudge rides outermost.
            Quaternion q = Quaternion.Multiply(qFist, qDisplay);
            q = Quaternion.Multiply(
                Quaternion.CreateFromYawPitchRoll(_heldYaw, _heldPitch, _heldRoll), q);

            float scale = (sprite ? _heldItemScale : _heldBlockScale) * dScale;

            // ---- Position ----------------------------------------------------------------
            // Vanilla anchors the item by its local ORIGIN CORNER: it translates the attack-
            // rotated frame by the display translation, then grows the sprite (or cube) from
            // there. Our meshes are CENTERED, so we draw them at the vanilla object's CENTER:
            // corner position + half extents carried by the same final rotation. That lands
            // the geometry in the exact vanilla spot no matter what size it's drawn at.
            var dVec = new Vector3(dTrans.x, dTrans.y, dTrans.z);
            Vector3 pos = grip + Vector3.Transform(dVec, Matrix4x4.CreateFromQuaternion(qFist));
            float worldSize = (sprite ? 2f : 0.3f) * scale; // drawn size in world units
            pos += Vector3.Transform(new Vector3(0.5f, 0.5f, 0.5f) * worldSize,
                Matrix4x4.CreateFromQuaternion(q));
            // F8 nudge stays raw in screen space, on top of everything.
            pos += new Vector3(_heldOffsetX, _heldOffsetY, _heldOffsetZ);

            int f = 0;
            _heldInstanceScratch[f++] = pos.X;
            _heldInstanceScratch[f++] = pos.Y;
            _heldInstanceScratch[f++] = pos.Z;
            _heldInstanceScratch[f++] = tile.X / atlasW;
            _heldInstanceScratch[f++] = tile.Y / atlasH;
            _heldInstanceScratch[f++] = tile.Width / atlasW;
            _heldInstanceScratch[f++] = tile.Height / atlasH;
            _heldInstanceScratch[f++] = q.X;
            _heldInstanceScratch[f++] = q.Y;
            _heldInstanceScratch[f++] = q.Z;
            _heldInstanceScratch[f++] = q.W;
            _heldInstanceScratch[f++] = scale;
            // Flat items are extruded into a thin slab (1 sprite pixel by default) so they
            // read as physical objects instead of decals.
            _heldInstanceScratch[f++] = sprite ? _heldThickness : 0f;
            // The GPU fetches instance N at N * stride, so every slice of the stack needs its
            // own copy of the data - they are all the same item, just at a different depth.
            for (int s = 1; s < HeldSlices; s++)
                Array.Copy(_heldInstanceScratch, 0, _heldInstanceScratch, 13 * s, 13);
            _gd.UpdateBuffer(_heldInstanceBuffer, 0, _heldInstanceScratch);

            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, _handProjSet);
            cl.SetGraphicsResourceSet(1, textureSet);
            cl.SetVertexBuffer(0, sprite ? _heldCardVertexBuffer : _itemDropVertexBuffer);
            cl.SetVertexBuffer(1, _heldInstanceBuffer, 0);
            cl.SetIndexBuffer(sprite ? _heldCardIndexBuffer : _itemDropIndexBuffer, IndexFormat.UInt16);
            // A flat item is a STACK of its own sprite through the slab's depth: the copies
            // are spread by perspective, so the art's silhouette sweeps backward and the item
            // grows a body instead of reading as a decal. The front slice is drawn last and
            // wins (depth is off), so the item always shows its crisp sprite.
            cl.DrawIndexed(sprite ? 6u : (uint)FallingCubeIndices,
                sprite ? (uint)HeldSlices : 1u, 0, 0, 0);
        }
    }
}
