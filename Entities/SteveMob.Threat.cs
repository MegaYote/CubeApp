using System;
using System.Collections.Generic;

namespace Cubuild
{
    public sealed partial class SteveMob
    {
        private enum ThreatMode { None, Retreat, Defend }

        private ThreatMode _threatMode;
        private MobEntity? _threatTarget;
        private float _threatScanTimer;
        private float _threatAttackCooldown;
        private bool _hasThreatStand;
        private int _threatStandX, _threatStandY, _threatStandZ;
        private float _threatStandRefreshTimer;
        private float _threatNoStandRetryTimer;
        private readonly HashSet<(int X, int Y, int Z)> _failedThreatStands = new();

        internal bool HasActiveThreat => _threatMode != ThreatMode.None;

        private void UpdateSteveThreat(GameWorld world, float dt)
        {
            _threatScanTimer = Math.Max(0f, _threatScanTimer - Math.Max(0f, dt));
            _threatAttackCooldown = Math.Max(0f, _threatAttackCooldown - Math.Max(0f, dt));
            _threatStandRefreshTimer = Math.Max(0f, _threatStandRefreshTimer - Math.Max(0f, dt));
            _threatNoStandRetryTimer = Math.Max(0f, _threatNoStandRetryTimer - Math.Max(0f, dt));
            if (_threatScanTimer > 0f) return;
            _threatScanTimer = 0.35f;

            MobEntity? nearest = null;
            double nearestSq = 14.0 * 14.0;
            int closeCount = 0;
            int strongestDamage = 0;
            foreach (var renderable in world.Entities.Mobs)
            {
                if (renderable is not MobEntity mob || !mob.Hostile || mob.IsDead || mob.Removed) continue;
                double dx = mob.Position.X - Position.X;
                double dy = mob.Position.Y - Position.Y;
                double dz = mob.Position.Z - Position.Z;
                double distanceSq = dx * dx + dy * dy + dz * dz;
                if (distanceSq < nearestSq)
                {
                    nearest = mob;
                    nearestSq = distanceSq;
                }
                if (distanceSq <= 14.0 * 14.0)
                {
                    if (distanceSq <= 9.0 * 9.0) closeCount++;
                    strongestDamage = Math.Max(strongestDamage, mob.AttackDamage);
                }
            }

            if (nearest == null)
            {
                if (_threatMode != ThreatMode.None)
                {
                    _threatMode = ThreatMode.None;
                    _threatTarget = null;
                    ResetThreatStandState();
                    _plannerDecisionTimer = _plannerScanTimer = 0f;
                }
                return;
            }

            bool targetChanged = !ReferenceEquals(_threatTarget, nearest);
            if (targetChanged)
            {
                _threatTarget = nearest;
                ResetThreatStandState();
            }
            int weaponDamage = GetSteveMeleeDamage();
            int nearbyPartners = 0;
            foreach (var renderable in world.Entities.Mobs)
            {
                if (renderable is not SteveMob partner || ReferenceEquals(partner, this)
                    || partner.IsDead || partner.Removed) continue;
                double dx = partner.Position.X - Position.X;
                double dz = partner.Position.Z - Position.Z;
                if (dx * dx + dz * dz <= 8.0 * 8.0) nearbyPartners++;
            }

            bool hasClearLine = HasThreatLineOfSight(world, nearest);
            bool retreat = Health <= Math.Max(5, MaxHealth * 0.45)
                || closeCount >= 2
                || (strongestDamage >= 8 && Health < MaxHealth * 0.7 && nearbyPartners == 0)
                || (!hasClearLine && Health < MaxHealth * 0.4)
                || (weaponDamage <= 1 && nearbyPartners == 0 && nearestSq > 3.0 * 3.0);
            var nextMode = retreat ? ThreatMode.Retreat : ThreatMode.Defend;
            if (_threatMode != nextMode)
            {
                _threatMode = nextMode;
                ResetThreatStandState();
                _taskExecutor.SetPlan();
                _plannerDecisionTimer = 0f;
            }
        }

        private int GetSteveMeleeDamage()
        {
            int damage = 1;
            int spearId = ItemRegistry.GetId("flint_spear");
            foreach (var slot in _shelterInventory)
            {
                if (slot.Count <= 0 || slot.ItemId <= 0) continue;
                if (slot.ItemId == spearId) damage = Math.Max(damage, 4);
                else if (string.Equals(ItemRegistry.ToolTypeOf(slot.ItemId), "axe", StringComparison.OrdinalIgnoreCase))
                    damage = Math.Max(damage, 2 + Math.Clamp(ItemRegistry.ToolLevelOf(slot.ItemId), 0, 2));
            }
            return damage;
        }

        private void PrepareSteveThreatResponse(GameWorld world)
        {
            if (_threatTarget == null || _threatTarget.IsDead || _threatTarget.Removed)
            {
                ResetThreatStandState();
                _taskExecutor.SetPlan(AgentTask.Hold((int)Math.Floor(Position.X),
                    (int)Math.Floor(Position.Y), (int)Math.Floor(Position.Z)));
                PrepareShelterTasks();
                return;
            }

            if (_taskExecutor.Failed)
            {
                if (_hasThreatStand)
                    _failedThreatStands.Add((_threatStandX, _threatStandY, _threatStandZ));
                _hasThreatStand = false;
                _threatStandRefreshTimer = 0f;
                _threatNoStandRetryTimer = 0f;
            }

            if (_threatMode == ThreatMode.Retreat)
            {
                PrepareThreatStandPlan(world, _threatTarget, fleeing: true);
            }
            else
            {
                double dx = _threatTarget.Position.X - Position.X;
                double dy = _threatTarget.Position.Y + 0.8 - (Position.Y + Height * 0.8);
                double dz = _threatTarget.Position.Z - Position.Z;
                if (dx * dx + dy * dy + dz * dz <= 2.15 * 2.15)
                {
                    _hasThreatStand = false;
                    _taskExecutor.SetPlan(AgentTask.Hold((int)Math.Floor(Position.X),
                        (int)Math.Floor(Position.Y), (int)Math.Floor(Position.Z)));
                }
                else
                    PrepareThreatStandPlan(world, _threatTarget, fleeing: false);
            }
            PrepareShelterTasks();
        }

        private void PrepareThreatStandPlan(GameWorld world, MobEntity threat, bool fleeing)
        {
            if (!_hasThreatStand && _threatNoStandRetryTimer > 0f)
            {
                SetThreatHoldPlan();
                return;
            }

            bool arrived = _hasThreatStand && PathStatus == NavigationStatus.Arrived;
            if (!_hasThreatStand || _threatStandRefreshTimer <= 0f || arrived)
            {
                if (!TryFindThreatStand(world, threat, fleeing,
                    out int x, out int y, out int z))
                {
                    _hasThreatStand = false;
                    _threatNoStandRetryTimer = 1.0f;
                    _threatStandRefreshTimer = 1.0f;
                    SetThreatHoldPlan();
                    return;
                }

                _hasThreatStand = true;
                _threatStandX = x;
                _threatStandY = y;
                _threatStandZ = z;
                _threatStandRefreshTimer = 1.0f;
                _threatNoStandRetryTimer = 0f;
            }

            _taskExecutor.SetPlan(AgentTask.Navigate(_threatStandX, _threatStandY, _threatStandZ),
                AgentTask.Hold(_threatStandX, _threatStandY, _threatStandZ));
        }

        private void SetThreatHoldPlan()
        {
            _taskExecutor.SetPlan(AgentTask.Hold((int)Math.Floor(Position.X),
                (int)Math.Floor(Position.Y), (int)Math.Floor(Position.Z)));
        }

        private void ResetThreatStandState()
        {
            _hasThreatStand = false;
            _threatStandRefreshTimer = 0f;
            _threatNoStandRetryTimer = 0f;
            _failedThreatStands.Clear();
        }

        private bool TryFindThreatStand(GameWorld world, MobEntity threat, bool fleeing,
            out int bestX, out int bestY, out int bestZ)
        {
            bestX = bestY = bestZ = 0;
            double bestScore = double.NegativeInfinity;
            int cx = (int)Math.Floor(Position.X), cy = (int)Math.Floor(Position.Y), cz = (int)Math.Floor(Position.Z);
            double currentThreatSq = DistanceSquared(Position, threat.Position);
            for (int ring = 1; ring <= (fleeing ? 5 : 2); ring++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    for (int dy = 1; dy >= -2; dy--)
                    {
                        int x = cx + dx, y = cy + dy, z = cz + dz;
                        if (_failedThreatStands.Contains((x, y, z))) continue;
                        if (!IsWalkableStand(world, x, y, z)) continue;
                        double threatSq = DistanceSquared(new Point3D(x + 0.5, y, z + 0.5), threat.Position);
                        if (fleeing && threatSq <= currentThreatSq + 1.0) continue;
                        if (!fleeing && (threatSq > 3.2 * 3.2 || threatSq < 1.8 * 1.8)) continue;
                        double selfSq = (x + 0.5 - Position.X) * (x + 0.5 - Position.X)
                            + (y - Position.Y) * (y - Position.Y)
                            + (z + 0.5 - Position.Z) * (z + 0.5 - Position.Z);
                        double score = fleeing ? threatSq - selfSq * 0.12 : -selfSq;
                        if (score <= bestScore) continue;
                        bestScore = score;
                        bestX = x; bestY = y; bestZ = z;
                    }
                }
                if (!double.IsNegativeInfinity(bestScore)) return true;
            }
            return false;
        }

        private static double DistanceSquared(Point3D a, Point3D b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private void FinishSteveThreatResponse(GameWorld world)
        {
            if (_threatMode != ThreatMode.Defend || _threatTarget == null
                || _threatTarget.IsDead || _threatTarget.Removed || _threatAttackCooldown > 0f) return;
            double dx = _threatTarget.Position.X - Position.X;
            double dy = _threatTarget.Position.Y + 0.8 - (Position.Y + Height * 0.8);
            double dz = _threatTarget.Position.Z - Position.Z;
            if (dx * dx + dy * dy + dz * dz > 2.15 * 2.15
                || !HasThreatLineOfSight(world, _threatTarget)) return;
            _threatAttackCooldown = 0.9f;
            _threatTarget.Damage(GetSteveMeleeDamage(), Position.X, Position.Z, true);
        }

        private bool HasThreatLineOfSight(GameWorld world, MobEntity threat)
        {
            double sx = Position.X, sy = Position.Y + Height * 0.8, sz = Position.Z;
            double ex = threat.Position.X, ey = threat.Position.Y + threat.Height * 0.55, ez = threat.Position.Z;
            double dx = ex - sx, dy = ey - sy, dz = ez - sz;
            int steps = Math.Max(1, (int)(Math.Sqrt(dx * dx + dy * dy + dz * dz) * 6.0));
            for (int i = 1; i < steps; i++)
            {
                double t = i / (double)steps;
                int x = (int)Math.Floor(sx + dx * t);
                int y = (int)Math.Floor(sy + dy * t);
                int z = (int)Math.Floor(sz + dz * t);
                if (!world.Chunks.TryGetLoadedBlock(x, y, z, out int blockId)) return false;
                if (BlockRegistry.IsSolid(blockId)) return false;
            }
            return true;
        }
    }
}
