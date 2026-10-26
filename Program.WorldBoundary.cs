using System;
using System.IO;

namespace Cubuild
{
    public sealed partial class Program
    {
        // Leave room for the largest render radius, vertical prefetch, and the player/block
        // collision footprint before chunk origins or neighboring block accesses approach int limits.
        private const int BoundaryRenderMarginChunks = 48; // 32-chunk maximum radius + 16 safety chunks
        private static readonly double WorldBoundaryLimit =
            ((long)int.MaxValue / ChunkManager.ChunkSize - BoundaryRenderMarginChunks) * ChunkManager.ChunkSize;

        private enum BoundarySide { PositiveX, NegativeX, PositiveZ, NegativeZ }

        private sealed class BoundaryHandoff
        {
            public BoundarySide Side;
            public string SourceSaveName = "";
            public string DestinationSaveName = "";
            public bool DestinationIsNew;
            public int NewSeed;
            public Point3D SourceSafePosition;
            public Point3D ArrivalPosition;
            public Point3D Velocity;
            public float Yaw, Pitch, BodyYaw, WalkPhase, WalkAmount;
            public bool Grounded, NoclipMode, CreativeFlightMode;
            public int Health;
        }

        /// <summary>Called after one complete simulation step, before the next frame can stream the unsafe location.</summary>
        private void TryBeginBoundaryHandoff()
        {
            if (World == null || screen != GameScreen.Playing || World.LocalPlayer.Health <= 0) return;
            var position = World.PlayerPosition;
            double overX = Math.Abs(position.X) - WorldBoundaryLimit;
            double overZ = Math.Abs(position.Z) - WorldBoundaryLimit;
            if (overX <= 0 && overZ <= 0) return;

            // There is deliberately no world handoff in multiplayer. Keep the player just inside
            // the supported range and stop outward motion instead of letting chunk math overflow.
            if (_netClient != null || _netHost != null)
            {
                double x = position.X, z = position.Z;
                var velocity = World.PlayerVelocity;
                if (overX > 0)
                {
                    x = Math.Sign(position.X) * (WorldBoundaryLimit - 1.0);
                    velocity = new Point3D(0, velocity.Y, velocity.Z);
                }
                if (overZ > 0)
                {
                    z = Math.Sign(position.Z) * (WorldBoundaryLimit - 1.0);
                    velocity = new Point3D(velocity.X, velocity.Y, 0);
                }
                World.PlayerPosition = new Point3D(x, position.Y, z);
                World.PlayerVelocity = velocity;
                if (!_boundaryMultiplayerWarningShown)
                {
                    _boundaryMultiplayerWarningShown = true;
                    SetStatusToast("World-boundary transitions are disabled in multiplayer.");
                }
                return;
            }

            var side = overX >= overZ
                ? position.X >= 0 ? BoundarySide.PositiveX : BoundarySide.NegativeX
                : position.Z >= 0 ? BoundarySide.PositiveZ : BoundarySide.NegativeZ;
            double overshoot = side switch
            {
                BoundarySide.PositiveX => Math.Max(0, position.X - WorldBoundaryLimit),
                BoundarySide.NegativeX => Math.Max(0, -WorldBoundaryLimit - position.X),
                BoundarySide.PositiveZ => Math.Max(0, position.Z - WorldBoundaryLimit),
                _ => Math.Max(0, -WorldBoundaryLimit - position.Z),
            };
            string sourceSaveName = SafeSaveKey(string.IsNullOrWhiteSpace(_activeWorldSaveName)
                ? SanitizeFileName(World.Name) : _activeWorldSaveName);
            string linkedName = SafeSaveKey(GetWorldLink(World, side));
            bool isNew = string.IsNullOrEmpty(linkedName);
            int newSeed = 0;
            if (isNew)
            {
                (linkedName, newSeed) = CreateUniqueFrontierSaveName(World.Name, side);
            }
            else if (string.Equals(linkedName, sourceSaveName, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(GetWorldSavePath(linkedName)))
            {
                ClampAtBoundary(side, position);
                SetStatusToast("Boundary crossing canceled: the linked world save is missing or invalid.");
                return;
            }

            var safeSourcePosition = position;
            switch (side)
            {
                case BoundarySide.PositiveX: safeSourcePosition = new Point3D(WorldBoundaryLimit, position.Y, position.Z); break;
                case BoundarySide.NegativeX: safeSourcePosition = new Point3D(-WorldBoundaryLimit, position.Y, position.Z); break;
                case BoundarySide.PositiveZ: safeSourcePosition = new Point3D(position.X, position.Y, WorldBoundaryLimit); break;
                case BoundarySide.NegativeZ: safeSourcePosition = new Point3D(position.X, position.Y, -WorldBoundaryLimit); break;
            }
            var arrival = position;
            double targetEdgeCoordinate = -Math.Sign(side switch
            {
                BoundarySide.PositiveX => 1,
                BoundarySide.NegativeX => -1,
                BoundarySide.PositiveZ => 1,
                _ => -1,
            }) * (WorldBoundaryLimit - overshoot);
            if (side is BoundarySide.PositiveX or BoundarySide.NegativeX)
                arrival = new Point3D(targetEdgeCoordinate, position.Y, position.Z);
            else
                arrival = new Point3D(position.X, position.Y, targetEdgeCoordinate);

            _pendingBoundaryHandoff = new BoundaryHandoff
            {
                Side = side,
                SourceSaveName = sourceSaveName,
                DestinationSaveName = linkedName,
                DestinationIsNew = isNew,
                NewSeed = newSeed,
                SourceSafePosition = safeSourcePosition,
                ArrivalPosition = arrival,
                Velocity = World.PlayerVelocity,
                Yaw = World.PlayerYaw,
                Pitch = World.PlayerPitch,
                BodyYaw = World.LocalPlayer.BodyYaw,
                WalkPhase = World.LocalPlayer.WalkPhase,
                WalkAmount = World.LocalPlayer.WalkAmount,
                Grounded = World.PlayerGrounded,
                NoclipMode = World.NoclipMode,
                CreativeFlightMode = World.CreativeFlightMode,
                Health = World.LocalPlayer.Health,
            };

            // Freeze the source at a representable pre-boundary position while the loading screen
            // is shown. The crossing overshoot is separately retained in ArrivalPosition.
            World.PlayerPosition = safeSourcePosition;
            screen = GameScreen.Loading;
            menu.Screen = GameScreen.Loading;
            menu.LoadingPhase = "Saving previous world";
            _loadingScreenShown = false;
            _pendingName = linkedName;
            _pendingWorldFromSave = false;
            DisableMouseLook();
        }

        private void PerformBoundaryHandoff(BoundaryHandoff handoff)
        {
            GameWorld? candidate = null;
            string? stagedDestination = null;
            string? stagedSource = null;
            try
            {
                if (World == null || _netClient != null || _netHost != null)
                    throw new InvalidOperationException("World-boundary transitions are only available in local single-player.");

                // Wait first, then snapshot the final source state at its safe edge. This prevents
                // an older background snapshot from overwriting the handoff save afterward.
                WaitForAutosave();
                var source = World;
                var sourceSave = CaptureWorldSave(source, requireCompleteChunks: true);
                sourceSave.PlayerX = handoff.SourceSafePosition.X;
                sourceSave.PlayerY = handoff.SourceSafePosition.Y;
                sourceSave.PlayerZ = handoff.SourceSafePosition.Z;
                SetWorldLink(sourceSave, handoff.Side, handoff.DestinationSaveName);

                WorldSave? existingDestinationSave = null;
                if (!handoff.DestinationIsNew)
                {
                    existingDestinationSave = WorldSave.Load(GetWorldSavePath(handoff.DestinationSaveName));
                    if (existingDestinationSave == null)
                        throw new IOException("The linked world save could not be read.");
                    if (existingDestinationSave.IsMinecraftMode != source.IsMinecraftMode
                        || existingDestinationSave.FrontierProgression != source.FrontierProgression)
                        throw new IOException("The linked world uses a different block pack and cannot be entered safely.");
                }

                int seed = existingDestinationSave?.Seed ?? handoff.NewSeed;
                string destinationWorldName = existingDestinationSave?.Name ?? handoff.DestinationSaveName;
                GameMode destinationMode = existingDestinationSave == null
                    ? source.Mode : (GameMode)existingDestinationSave.Mode;
                bool destinationIsMinecraft = existingDestinationSave?.IsMinecraftMode ?? source.IsMinecraftMode;
                bool destinationFrontier = existingDestinationSave?.FrontierProgression ?? source.FrontierProgression;
                candidate = CreateWorldCandidate(seed, destinationWorldName, destinationMode,
                    destinationIsMinecraft, destinationFrontier);

                if (existingDestinationSave != null) ApplyWorldSave(candidate, existingDestinationSave);
                string reciprocal = GetWorldLink(candidate, OppositeSide(handoff.Side));
                if (!string.IsNullOrWhiteSpace(reciprocal)
                    && !string.Equals(SafeSaveKey(reciprocal), handoff.SourceSaveName, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The linked destination side already belongs to a different world.");
                SetWorldLink(candidate, OppositeSide(handoff.Side), handoff.SourceSaveName);

                // Player identity crosses the boundary; world terrain, edits, mobs, and time do
                // not. A destination save remains untouched except for missing reciprocal metadata.
                CopyPlayerInventory(source, candidate);
                candidate.LocalPlayer.Health = handoff.Health;
                candidate.PlayerYaw = handoff.Yaw;
                candidate.PlayerPitch = handoff.Pitch;
                candidate.LocalPlayer.BodyYaw = handoff.BodyYaw;
                candidate.LocalPlayer.WalkPhase = handoff.WalkPhase;
                candidate.LocalPlayer.WalkAmount = handoff.WalkAmount;
                candidate.NoclipMode = handoff.NoclipMode;
                candidate.CreativeFlightMode = handoff.CreativeFlightMode;

                double feetY = handoff.ArrivalPosition.Y - GameWorld.EyeHeight;
                bool exactPosition = candidate.TryTeleportPlayerToFeet(
                    handoff.ArrivalPosition.X, feetY, handoff.ArrivalPosition.Z, out _);
                if (!exactPosition)
                {
                    if (!candidate.TryTeleportPlayerToTerrain(handoff.ArrivalPosition.X,
                            handoff.ArrivalPosition.Z, out string landingError))
                        throw new IOException("No safe destination landing could be found: " + landingError);
                }
                // If the same height is clear but the destination terrain is lower, keep the
                // crossing height and let gravity carry the player down naturally. Only snap to
                // a verified surface when the exact arrival AABB would intersect solid blocks;
                // random worlds can have very different edge elevations, so imposing a tiny
                // vertical-adjustment limit would make most valid crossings fail.
                candidate.PlayerYaw = handoff.Yaw;
                candidate.PlayerPitch = handoff.Pitch;
                candidate.LocalPlayer.BodyYaw = handoff.BodyYaw;
                var carriedVelocity = handoff.Velocity;
                if (candidate.PlayerGrounded && carriedVelocity.Y < 0)
                    carriedVelocity = new Point3D(carriedVelocity.X, 0, carriedVelocity.Z);
                candidate.PlayerVelocity = carriedVelocity;
                candidate.LocalPlayer.WalkPhase = handoff.WalkPhase;
                candidate.LocalPlayer.WalkAmount = handoff.WalkAmount;
                candidate.NoclipMode = handoff.NoclipMode;
                candidate.CreativeFlightMode = handoff.CreativeFlightMode;

                WorldSave? destinationSnapshot = null;
                bool writeDestination = handoff.DestinationIsNew;
                if (handoff.DestinationIsNew)
                {
                    destinationSnapshot = CaptureWorldSave(candidate, requireCompleteChunks: true);
                }
                else if (!string.Equals(GetWorldLink(existingDestinationSave!, OppositeSide(handoff.Side)),
                             handoff.SourceSaveName, StringComparison.OrdinalIgnoreCase))
                {
                    // Missing reciprocal metadata is repaired without changing the destination's
                    // saved player position, inventory, terrain, or time.
                    SetSaveWorldLink(existingDestinationSave!, OppositeSide(handoff.Side), handoff.SourceSaveName);
                    destinationSnapshot = existingDestinationSave!;
                    writeDestination = true;
                }

                string sourcePath = GetWorldSavePath(handoff.SourceSaveName);
                string destinationPath = GetWorldSavePath(handoff.DestinationSaveName);
                lock (_saveWriteLock)
                {
                    // Stage and read-back verify every new file before either live save is replaced.
                    if (writeDestination)
                        stagedDestination = StageSaveToDisk(destinationSnapshot!, destinationPath);
                    stagedSource = StageSaveToDisk(sourceSave, sourcePath);
                    if (writeDestination)
                    {
                        CommitStagedSave(stagedDestination!, destinationPath,
                            requireNewFile: handoff.DestinationIsNew);
                        stagedDestination = null;
                    }
                    CommitStagedSave(stagedSource!, sourcePath);
                    stagedSource = null;
                }

                SetWorldLink(source, handoff.Side, handoff.DestinationSaveName);
                _saveToastTimer = 3f;
                _autosaveTimer = 0f;
                _pendingName = "";
                _loadingScreenShown = false;
                SetStatusToast("Entered " + destinationWorldName + ".");
                ActivateWorld(candidate, handoff.DestinationSaveName, skipSpawn: true);
                _pendingBoundaryHandoff = null;
                candidate = null;
            }
            catch (Exception ex)
            {
                TryLogSaveError(ex);
                CancelBoundaryHandoff("Boundary crossing canceled: " + ex.Message);
            }
            finally
            {
                try { candidate?.Dispose(); }
                catch (Exception ex) { TryLogSaveError(ex); }
                try { if (stagedDestination != null && File.Exists(stagedDestination)) File.Delete(stagedDestination); } catch { }
                try { if (stagedSource != null && File.Exists(stagedSource)) File.Delete(stagedSource); } catch { }
            }
        }

        private void CancelBoundaryHandoff(string message)
        {
            if (World != null)
            {
                World.PlayerPosition = _pendingBoundaryHandoff?.SourceSafePosition ?? World.PlayerPosition;
                var v = World.PlayerVelocity;
                var side = _pendingBoundaryHandoff?.Side;
                if (side is BoundarySide.PositiveX or BoundarySide.NegativeX) v = new Point3D(0, v.Y, v.Z);
                else if (side is BoundarySide.PositiveZ or BoundarySide.NegativeZ) v = new Point3D(v.X, v.Y, 0);
                World.PlayerVelocity = v;
            }
            _pendingBoundaryHandoff = null;
            _pendingName = "";
            _pendingWorldSave = null;
            _pendingWorldSaveName = "";
            _pendingWorldFromSave = false;
            _loadingScreenShown = false;
            menu.LoadingPhase = "";
            SetStatusToast(message);
            ResumeToPlaying();
        }

        private void ClampAtBoundary(BoundarySide side, Point3D position)
        {
            if (World == null) return;
            var velocity = World.PlayerVelocity;
            switch (side)
            {
                case BoundarySide.PositiveX: World.PlayerPosition = new Point3D(WorldBoundaryLimit - 1, position.Y, position.Z); velocity = new Point3D(0, velocity.Y, velocity.Z); break;
                case BoundarySide.NegativeX: World.PlayerPosition = new Point3D(-WorldBoundaryLimit + 1, position.Y, position.Z); velocity = new Point3D(0, velocity.Y, velocity.Z); break;
                case BoundarySide.PositiveZ: World.PlayerPosition = new Point3D(position.X, position.Y, WorldBoundaryLimit - 1); velocity = new Point3D(velocity.X, velocity.Y, 0); break;
                case BoundarySide.NegativeZ: World.PlayerPosition = new Point3D(position.X, position.Y, -WorldBoundaryLimit + 1); velocity = new Point3D(velocity.X, velocity.Y, 0); break;
            }
            World.PlayerVelocity = velocity;
        }

        private static (string name, int seed) CreateUniqueFrontierSaveName(string sourceName, BoundarySide side)
        {
            string sideText = side switch
            {
                BoundarySide.PositiveX => "+X",
                BoundarySide.NegativeX => "-X",
                BoundarySide.PositiveZ => "+Z",
                _ => "-Z",
            };
            for (int attempt = 0; attempt < 128; attempt++)
            {
                string name = SafeSaveKey($"{sourceName} Beyond {sideText} {DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}");
                if (!File.Exists(GetWorldSavePath(name))) return (name, Random.Shared.Next());
            }
            throw new IOException("Could not allocate a unique destination world-save name.");
        }

        private static string SafeSaveKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string key = Path.GetFileName(value.Trim().Replace('\\', '/'));
            if (key.EndsWith(".cubuild", StringComparison.OrdinalIgnoreCase))
                key = key[..^".cubuild".Length];
            return SanitizeFileName(key);
        }

        private static BoundarySide OppositeSide(BoundarySide side) => side switch
        {
            BoundarySide.PositiveX => BoundarySide.NegativeX,
            BoundarySide.NegativeX => BoundarySide.PositiveX,
            BoundarySide.PositiveZ => BoundarySide.NegativeZ,
            _ => BoundarySide.PositiveZ,
        };

        private static string GetWorldLink(GameWorld world, BoundarySide side) => side switch
        {
            BoundarySide.PositiveX => world.LinkedPositiveXWorld,
            BoundarySide.NegativeX => world.LinkedNegativeXWorld,
            BoundarySide.PositiveZ => world.LinkedPositiveZWorld,
            _ => world.LinkedNegativeZWorld,
        };

        private static string GetWorldLink(WorldSave save, BoundarySide side) => side switch
        {
            BoundarySide.PositiveX => save.LinkedPositiveXWorld,
            BoundarySide.NegativeX => save.LinkedNegativeXWorld,
            BoundarySide.PositiveZ => save.LinkedPositiveZWorld,
            _ => save.LinkedNegativeZWorld,
        };

        private static void SetWorldLink(GameWorld world, BoundarySide side, string name)
        {
            name = SafeSaveKey(name);
            switch (side)
            {
                case BoundarySide.PositiveX: world.LinkedPositiveXWorld = name; break;
                case BoundarySide.NegativeX: world.LinkedNegativeXWorld = name; break;
                case BoundarySide.PositiveZ: world.LinkedPositiveZWorld = name; break;
                case BoundarySide.NegativeZ: world.LinkedNegativeZWorld = name; break;
            }
        }

        private static void SetWorldLink(WorldSave save, BoundarySide side, string name)
            => SetSaveWorldLink(save, side, name);

        private static void SetSaveWorldLink(WorldSave save, BoundarySide side, string name)
        {
            name = SafeSaveKey(name);
            switch (side)
            {
                case BoundarySide.PositiveX: save.LinkedPositiveXWorld = name; break;
                case BoundarySide.NegativeX: save.LinkedNegativeXWorld = name; break;
                case BoundarySide.PositiveZ: save.LinkedPositiveZWorld = name; break;
                case BoundarySide.NegativeZ: save.LinkedNegativeZWorld = name; break;
            }
        }

        private static void CopyPlayerInventory(GameWorld source, GameWorld destination)
        {
            Array.Copy(source.Hotbar, destination.Hotbar, Math.Min(source.Hotbar.Length, destination.Hotbar.Length));
            Array.Copy(source.HotbarCounts, destination.HotbarCounts,
                Math.Min(source.HotbarCounts.Length, destination.HotbarCounts.Length));
            destination.SetSelectedSlot(Math.Clamp(source.SelectedSlot, 0, GameWorld.HotbarSlots - 1));
            destination.RestoreBag(source.BagSlots.ToArray());
            destination.HeldStack = source.HeldStack;
        }

        private void SetStatusToast(string message)
        {
            _statusToast = message;
            _statusToastTimer = 8f;
        }

        /// <summary>
        /// Old saves may place the player beyond the new safe streaming boundary. Clamp those
        /// positions before the load pipeline requests neighboring chunks, then let the player
        /// step across normally once loaded. This prevents the first post-update load from
        /// overflowing chunk-origin arithmetic near int.MaxValue/int.MinValue.
        /// </summary>
        private void ClampLoadedWorldToSafeBoundary(GameWorld world)
        {
            var position = world.PlayerPosition;
            double x = position.X;
            double z = position.Z;
            var velocity = world.PlayerVelocity;
            bool clamped = false;

            if (x >= WorldBoundaryLimit)
            {
                x = WorldBoundaryLimit - 1.0;
                velocity = new Point3D(0, velocity.Y, velocity.Z);
                clamped = true;
            }
            else if (x <= -WorldBoundaryLimit)
            {
                x = -WorldBoundaryLimit + 1.0;
                velocity = new Point3D(0, velocity.Y, velocity.Z);
                clamped = true;
            }

            if (z >= WorldBoundaryLimit)
            {
                z = WorldBoundaryLimit - 1.0;
                velocity = new Point3D(velocity.X, velocity.Y, 0);
                clamped = true;
            }
            else if (z <= -WorldBoundaryLimit)
            {
                z = -WorldBoundaryLimit + 1.0;
                velocity = new Point3D(velocity.X, velocity.Y, 0);
                clamped = true;
            }

            if (!clamped) return;
            world.PlayerPosition = new Point3D(x, position.Y, z);
            world.PlayerVelocity = velocity;
            SetStatusToast("Loaded at the world edge; move outward to continue into a new world.");
        }
    }
}
