using System.Numerics;
using System.Reflection;
using Veldrid;

namespace CubeApp
{
    public readonly struct FrameInputState
    {
        public bool ToggleMouseCapturePressed { get; }
        public bool ToggleDebugPressed { get; }
        public bool ToggleInventoryPressed { get; }
        public bool CycleRenderDistancePressed { get; }
        public bool SpawnMobPressed { get; }
        public bool BreakBlockPressed { get; }
        public bool PlaceBlockPressed { get; }
        public int? SelectedSlot { get; }

        public FrameInputState(
            bool toggleMouseCapturePressed,
            bool toggleDebugPressed,
            bool toggleInventoryPressed,
            bool cycleRenderDistancePressed,
            bool spawnMobPressed,
            bool breakBlockPressed,
            bool placeBlockPressed,
            int? selectedSlot)
        {
            ToggleMouseCapturePressed = toggleMouseCapturePressed;
            ToggleDebugPressed = toggleDebugPressed;
            ToggleInventoryPressed = toggleInventoryPressed;
            CycleRenderDistancePressed = cycleRenderDistancePressed;
            SpawnMobPressed = spawnMobPressed;
            BreakBlockPressed = breakBlockPressed;
            PlaceBlockPressed = placeBlockPressed;
            SelectedSlot = selectedSlot;
        }
    }

    public readonly struct TickInputState
    {
        public bool MoveForward { get; }
        public bool MoveBackward { get; }
        public bool MoveLeft { get; }
        public bool MoveRight { get; }
        public bool JumpPressed { get; }
        public Vector2 LookDelta { get; }

        public TickInputState(
            bool moveForward,
            bool moveBackward,
            bool moveLeft,
            bool moveRight,
            bool jumpPressed,
            Vector2 lookDelta)
        {
            MoveForward = moveForward;
            MoveBackward = moveBackward;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            JumpPressed = jumpPressed;
            LookDelta = lookDelta;
        }
    }

    // Collects per-frame snapshots and exposes deterministic per-tick input for fixed-step simulation.
    public sealed class InputProcessor
    {
        private static readonly string[] MouseDeltaMemberNames = ["MouseDelta", "currentMouseDelta", "CurrentMouseDelta"];

        private bool moveForward;
        private bool moveBackward;
        private bool moveLeft;
        private bool moveRight;
        private bool jumpPressed;

        private bool toggleMouseCapturePressed;
        private bool toggleDebugPressed;
        private bool toggleInventoryPressed;
        private bool cycleRenderDistancePressed;
        private bool spawnMobPressed;
        private bool breakBlockPressed;
        private bool placeBlockPressed;
        private int? selectedSlot;
        private Vector2 lookDeltaAccum;
        private Vector2 lastMousePosition;
        private bool hasLastMousePosition;

        public void BeginFrame()
        {
            toggleMouseCapturePressed = false;
            toggleDebugPressed = false;
            toggleInventoryPressed = false;
            cycleRenderDistancePressed = false;
            spawnMobPressed = false;
            breakBlockPressed = false;
            placeBlockPressed = false;
            selectedSlot = null;
            lookDeltaAccum = Vector2.Zero;
        }

        public void ResetMouseTracking()
        {
            hasLastMousePosition = false;
        }

        public void ProcessSnapshot(InputSnapshot snapshot, bool mouseCaptured, float sensitivity)
        {
            foreach (var keyEvent in snapshot.KeyEvents)
            {
                bool down = keyEvent.Down;
                switch (keyEvent.Key)
                {
                    case Key.W:
                        moveForward = down;
                        break;
                    case Key.S:
                        moveBackward = down;
                        break;
                    case Key.A:
                        moveLeft = down;
                        break;
                    case Key.D:
                        moveRight = down;
                        break;
                    case Key.Space:
                        if (down) jumpPressed = true;
                        break;
                    case Key.Escape:
                        if (down) toggleMouseCapturePressed = true;
                        break;
                    case Key.F3:
                        if (down) toggleDebugPressed = true;
                        break;
                    case Key.E:
                        if (down) toggleInventoryPressed = true;
                        break;
                    case Key.F:
                        if (down) cycleRenderDistancePressed = true;
                        break;
                    case Key.G:
                        if (down) spawnMobPressed = true;
                        break;
                    case Key.Number1:
                        if (down) selectedSlot = 0;
                        break;
                    case Key.Number2:
                        if (down) selectedSlot = 1;
                        break;
                    case Key.Number3:
                        if (down) selectedSlot = 2;
                        break;
                    case Key.Number4:
                        if (down) selectedSlot = 3;
                        break;
                    case Key.Number5:
                        if (down) selectedSlot = 4;
                        break;
                    case Key.Number6:
                        if (down) selectedSlot = 5;
                        break;
                    case Key.Number7:
                        if (down) selectedSlot = 6;
                        break;
                    case Key.Number8:
                        if (down) selectedSlot = 7;
                        break;
                    case Key.Number9:
                        if (down) selectedSlot = 8;
                        break;
                    case Key.Number0:
                        if (down) selectedSlot = 9;
                        break;
                }
            }

            foreach (var mouseEvent in snapshot.MouseEvents)
            {
                if (!mouseEvent.Down)
                {
                    continue;
                }

                if (mouseEvent.MouseButton == MouseButton.Left)
                {
                    breakBlockPressed = true;
                }
                else if (mouseEvent.MouseButton == MouseButton.Right)
                {
                    placeBlockPressed = true;
                }
            }

            if (!mouseCaptured)
            {
                hasLastMousePosition = false;
                return;
            }

            if (TryGetMouseDelta(snapshot, out var delta))
            {
                lookDeltaAccum += delta * sensitivity;
                return;
            }

            // Fallback for builds/snapshots where relative delta isn't surfaced.
            if (hasLastMousePosition)
            {
                var positionDelta = snapshot.MousePosition - lastMousePosition;
                lookDeltaAccum += positionDelta * sensitivity;
            }

            lastMousePosition = snapshot.MousePosition;
            hasLastMousePosition = true;
        }

        private static bool TryGetMouseDelta(InputSnapshot snapshot, out Vector2 delta)
        {
            var snapshotType = snapshot.GetType();

            foreach (var memberName in MouseDeltaMemberNames)
            {
                var prop = snapshotType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.PropertyType == typeof(Vector2))
                {
                    if (prop.GetValue(snapshot) is Vector2 vectorValue)
                    {
                        delta = vectorValue;
                        return true;
                    }
                }

                var field = snapshotType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(Vector2))
                {
                    if (field.GetValue(snapshot) is Vector2 vectorFieldValue)
                    {
                        delta = vectorFieldValue;
                        return true;
                    }
                }
            }

            delta = Vector2.Zero;
            return false;
        }

        public FrameInputState CaptureFrameInput()
        {
            var result = new FrameInputState(
                toggleMouseCapturePressed,
                toggleDebugPressed,
                toggleInventoryPressed,
                cycleRenderDistancePressed,
                spawnMobPressed,
                breakBlockPressed,
                placeBlockPressed,
                selectedSlot);

            toggleMouseCapturePressed = false;
            toggleDebugPressed = false;
            toggleInventoryPressed = false;
            cycleRenderDistancePressed = false;
            spawnMobPressed = false;
            breakBlockPressed = false;
            placeBlockPressed = false;
            selectedSlot = null;

            return result;
        }

        public Vector2 CaptureLookDelta()
        {
            var look = lookDeltaAccum;
            lookDeltaAccum = Vector2.Zero;
            return look;
        }

        public TickInputState CaptureTickInput()
        {
            var result = new TickInputState(
                moveForward,
                moveBackward,
                moveLeft,
                moveRight,
                jumpPressed,
                Vector2.Zero);

            jumpPressed = false;

            return result;
        }
    }
}
