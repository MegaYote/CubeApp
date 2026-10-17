using System;

namespace Cubuild
{
    /// <summary>
    /// Cart linkage ("linkage" item): while holding a linkage, right-click one cart to pick it as the
    /// HEAD of a new train, then right-click a second, nearby cart to couple the pair. The first cart
    /// then tows the second (see CartEntity.Leader / Follower and EntityManager.ResolveTrainLinks).
    /// The coupling is never broken by physics; left-clicking the beam between the carts severs it.
    /// </summary>
    public sealed partial class GameWorld
    {
        private static int _linkageItemIdCache = -1;
        private static int LinkageItemId
        {
            get { if (_linkageItemIdCache < 0) _linkageItemIdCache = ItemRegistry.TryGet("linkage", out var d) ? d.NumericId : 0; return _linkageItemIdCache; }
            set => _linkageItemIdCache = value;
        }

        /// <summary>True while the selected hotbar item is the linkage coupler.</summary>
        public bool HoldingLinkage => LinkageItemId > 0 && SelectedBlock == LinkageItemId;

        /// <summary>First cart picked while holding a linkage, awaiting its partner. Null = none waiting.</summary>
        private CartEntity? _linkageAnchor;

        /// <summary>The cart currently picked as the head of a pending coupling (for the HUD, if we ever show it).</summary>
        public CartEntity? LinkageAnchor => _linkageAnchor;

        /// <summary>How close two carts must be to couple, in blocks.</summary>
        public const double LinkRange = 4.0;

        /// <summary>How far the crosshair reaches to grab a cart for coupling (any cart, not just a rideable one).</summary>
        private const double LinkReach = 4.5;

        /// <summary>Crosshair pick slack for the linkage grab, matching the minecart grab.</summary>
        private const double LinkPickPerp = 1.2;

        /// <summary>
        /// Right-click handler while holding a linkage. First click stores a cart as the head; the
        /// second click on a DIFFERENT cart within <see cref="LinkRange"/> couples them (the second
        /// cart becomes the trailer). Returns true when the click was consumed (so the caller skips
        /// boarding a cart / placing a block). Clicking the same cart again, or empty space, cancels.
        /// </summary>
        public bool TryLinkCarts(Point3D origin, Point3D direction)
        {
            if (!HoldingLinkage) return false;

            var cart = FindAnyCartUnderCrosshair(origin, direction);
            if (cart == null)
            {
                _linkageAnchor = null;   // clicking empty space cancels a pending pick
                return false;
            }

            if (_linkageAnchor == null)
            {
                _linkageAnchor = cart;   // pick the head
                return true;
            }

            if (_linkageAnchor == cart)
            {
                _linkageAnchor = null;   // click the same cart again to cancel
                return true;
            }

            LinkCarts(_linkageAnchor, cart);
            _linkageAnchor = null;
            return true;
        }

        /// <summary>
        /// Couples <paramref name="trailer"/> behind <paramref name="head"/>. Requires the pair to be
        /// close, neither end already committed (the head tows nothing, the trailer is towed by
        /// nothing), and refuses a self-link - which together make cycles impossible, so the result is
        /// always a simple chain (A-B-C-...). Returns false when the coupling is not allowed.
        /// </summary>
        public bool LinkCarts(CartEntity head, CartEntity trailer)
        {
            if (ReferenceEquals(head, trailer)) return false;
            if (head.Follower != null || trailer.Leader != null) return false;

            double dx = head.Position.X - trailer.Position.X;
            double dy = head.Position.Y - trailer.Position.Y;
            double dz = head.Position.Z - trailer.Position.Z;
            if (dx * dx + dy * dy + dz * dz > LinkRange * LinkRange) return false;

            // The LOCOMOTIVE is always the head of its train. It is the only member with the power to
            // haul (and the only one whose throttle the driver's controls are routed to - see
            // GameWorld.StepRidingCart), so if the player coupled the cars the other way round the
            // roles are quietly swapped rather than building a train that cannot move.
            if (head is not LocomotiveEntity && trailer is LocomotiveEntity)
            {
                (head, trailer) = (trailer, head);
            }

            // Face the head away from its new trailer so the first rigid placement (one cell opposite
            // the head's forward axis) lands the trailer on the back of the train, not in front of it.
            head.FaceAwayFrom(trailer);

            trailer.Leader = head;
            head.Follower = trailer;
            return true;
        }

        /// <summary>
        /// Nearest cart under the crosshair for a coupling grab. Unlike FindCartUnderCrosshair this
        /// accepts ANY cart (the passive ore cart too), because a train may be built from either kind.
        /// </summary>
        private CartEntity? FindAnyCartUnderCrosshair(Point3D origin, Point3D direction)
        {
            CartEntity? best = null;
            double bestDist = LinkReach;
            foreach (var mob in Entities.Mobs)
            {
                if (mob is not CartEntity cart || cart.IsDead || cart.Removed) continue;
                double dx = cart.Position.X - origin.X;
                double dy = cart.Position.Y + cart.Height * 0.5 - origin.Y;
                double dz = cart.Position.Z - origin.Z;
                double along = dx * direction.X + dy * direction.Y + dz * direction.Z;
                if (along < 0 || along > LinkReach) continue;
                double perp = Math.Sqrt(Math.Max(0, dx * dx + dy * dy + dz * dz - along * along));
                if (perp < LinkPickPerp && along < bestDist) { bestDist = along; best = cart; }
            }
            return best;
        }

        /// <summary>Drops any pending linkage pick (e.g. the player switched off the linkage item).</summary>
        public void ClearLinkageAnchor() => _linkageAnchor = null;
    }
}
