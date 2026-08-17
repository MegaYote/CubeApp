using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Veldrid;

namespace Cubuild
{
    public readonly struct FrameInputState
    {
        public bool ToggleMouseCapturePressed { get; }
        public bool ToggleDebugPressed { get; }
        public bool ToggleInventoryPressed { get; }
        public bool ToggleBiomeMenuPressed { get; }
        public bool ToggleHandEditorPressed { get; }
        public bool CycleRenderDistancePressed { get; }
        public bool SpawnMobPressed { get; }
        public bool SpawnCoyotePressed { get; }
        public bool SpawnStevePressed { get; }
        public bool SpawnZombiePressed { get; }
        public bool DamageSelfPressed { get; }
        public bool ToggleThirdPersonPressed { get; }
        public bool ToggleFlyPressed { get; }
        public bool ToggleFullbrightPressed { get; }
        public bool AdvanceTimePressed { get; }
        public bool ToggleGpuCullPressed { get; }
        public bool ToggleFullscreenPressed { get; }
        public bool BreakBlockPressed { get; }
        public bool PlaceBlockPressed { get; }
        public bool DropItemPressed { get; }
        public int? SelectedSlot { get; }
        public int HotbarScroll { get; }

        public FrameInputState(
            bool toggleMouseCapturePressed,
            bool toggleDebugPressed,
            bool toggleInventoryPressed,
            bool toggleBiomeMenuPressed,
            bool toggleHandEditorPressed,
            bool cycleRenderDistancePressed,
            bool spawnMobPressed,
            bool spawnCoyotePressed,
            bool spawnStevePressed,
            bool spawnZombiePressed,
            bool damageSelfPressed,
            bool toggleThirdPersonPressed,
            bool toggleFlyPressed,
            bool toggleFullbrightPressed,
            bool advanceTimePressed,
            bool toggleGpuCullPressed,
            bool toggleFullscreenPressed,
            bool breakBlockPressed,
            bool placeBlockPressed,
            bool dropItemPressed,
            int? selectedSlot,
            int hotbarScroll)
        {
            ToggleMouseCapturePressed = toggleMouseCapturePressed;
            ToggleDebugPressed = toggleDebugPressed;
            ToggleInventoryPressed = toggleInventoryPressed;
            ToggleBiomeMenuPressed = toggleBiomeMenuPressed;
            ToggleHandEditorPressed = toggleHandEditorPressed;
            CycleRenderDistancePressed = cycleRenderDistancePressed;
            SpawnMobPressed = spawnMobPressed;
            SpawnCoyotePressed = spawnCoyotePressed;
            SpawnStevePressed = spawnStevePressed;
            SpawnZombiePressed = spawnZombiePressed;
            DamageSelfPressed = damageSelfPressed;
            ToggleThirdPersonPressed = toggleThirdPersonPressed;
            ToggleFlyPressed = toggleFlyPressed;
            ToggleFullbrightPressed = toggleFullbrightPressed;
            AdvanceTimePressed = advanceTimePressed;
            ToggleGpuCullPressed = toggleGpuCullPressed;
            ToggleFullscreenPressed = toggleFullscreenPressed;
            BreakBlockPressed = breakBlockPressed;
            PlaceBlockPressed = placeBlockPressed;
            DropItemPressed = dropItemPressed;
            SelectedSlot = selectedSlot;
            HotbarScroll = hotbarScroll;
        }
    }

    public readonly struct TickInputState
    {
        public bool MoveForward { get; }
        public bool MoveBackward { get; }
        public bool MoveLeft { get; }
        public bool MoveRight { get; }
        public bool JumpPressed { get; }
        public bool MoveUp { get; }
        public bool MoveDown { get; }
        public bool BreakHeld { get; }
        public bool PlaceHeld { get; }
        public Vector2 LookDelta { get; }

        public TickInputState(
            bool moveForward,
            bool moveBackward,
            bool moveLeft,
            bool moveRight,
            bool jumpPressed,
            bool moveUp,
            bool moveDown,
            bool breakHeld,
            bool placeHeld,
            Vector2 lookDelta)
        {
            MoveForward = moveForward;
            MoveBackward = moveBackward;
            MoveLeft = moveLeft;
            MoveRight = moveRight;
            JumpPressed = jumpPressed;
            MoveUp = moveUp;
            MoveDown = moveDown;
            BreakHeld = breakHeld;
            PlaceHeld = placeHeld;
            LookDelta = lookDelta;
        }
    }

    // Collects per-frame snapshots and exposes deterministic per-tick input for fixed-step simulation.
    public sealed class InputProcessor
    {
        private static readonly string[] MouseDeltaMemberNames = ["MouseDelta", "currentMouseDelta", "CurrentMouseDelta"];

        // SDL2 key polling — bypasses broken SDL2 event delivery for movement keys.
        [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SDL_GetKeyboardState(out int numkeys);

        // SDL scancodes for movement keys
        private const int SDL_SCANCODE_W = 26;
        private const int SDL_SCANCODE_S = 22;
        private const int SDL_SCANCODE_A = 4;
        private const int SDL_SCANCODE_D = 7;
        private const int SDL_SCANCODE_SPACE = 44;
        private const int SDL_SCANCODE_LSHIFT = 225;

        private bool moveForward;
        private bool moveBackward;
        private bool moveLeft;
        private bool moveRight;
        private bool jumpPressed;
        private bool moveUp;
        private bool moveDown;

        private bool toggleMouseCapturePressed;
        private bool toggleDebugPressed;
        private bool toggleInventoryPressed;
        private bool toggleBiomeMenuPressed;
        private bool toggleHandEditorPressed;
        private bool cycleRenderDistancePressed;
        private bool spawnMobPressed;
        private bool spawnCoyotePressed;
        private bool spawnStevePressed;
        private bool spawnZombiePressed;
        private bool damageSelfPressed;
        private bool toggleThirdPersonPressed;
        private bool toggleFlyPressed;
        private bool toggleFullbrightPressed;
        private bool advanceTimePressed;
        private bool toggleGpuCullPressed;
        private bool toggleFullscreenPressed;
        private bool breakBlockPressed;
        private bool placeBlockPressed;
        private bool dropItemPressed;
        private bool breakHeld;
        private bool placeHeld;
        private int? selectedSlot;
        private int hotbarScroll;
        private Vector2 lookDeltaAccum;
        private Vector2 lastMousePosition;
        private bool hasLastMousePosition;

        public void BeginFrame()
        {
            toggleMouseCapturePressed = false;
            toggleDebugPressed = false;
            toggleInventoryPressed = false;
            toggleBiomeMenuPressed = false;
            toggleHandEditorPressed = false;
            cycleRenderDistancePressed = false;
            spawnMobPressed = false;
            spawnCoyotePressed = false;
            spawnStevePressed = false;
            spawnZombiePressed = false;
            damageSelfPressed = false;
            toggleThirdPersonPressed = false;
            toggleFlyPressed = false;
            toggleFullbrightPressed = false;
            toggleFullscreenPressed = false;
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
            // Poll SDL2 keyboard state directly for movement keys.
            // SDL2 event delivery is broken when multiple keys are held: it stops generating
            // key-repeat events for one key when another is pressed.  SDL_GetKeyboardState
            // reads the hardware state and always reflects reality.
            IntPtr keyStatePtr = SDL_GetKeyboardState(out int numkeys);
            unsafe
            {
                byte* keyState = (byte*)keyStatePtr;
                moveForward = keyState[SDL_SCANCODE_W] != 0;
                moveBackward = keyState[SDL_SCANCODE_S] != 0;
                moveLeft = keyState[SDL_SCANCODE_A] != 0;
                moveRight = keyState[SDL_SCANCODE_D] != 0;
                moveUp = keyState[SDL_SCANCODE_SPACE] != 0;
                moveDown = keyState[SDL_SCANCODE_LSHIFT] != 0;

                // jumpPressed is consumed each tick (one-shot).  Set it whenever Space is
                // physically held so StepPlayer can trigger a jump each time the player lands.
                jumpPressed = keyState[SDL_SCANCODE_SPACE] != 0;
            }

            // Process key events only for non-movement actions (toggles, one-shots).
            foreach (var keyEvent in snapshot.KeyEvents)
            {
                if (!keyEvent.Down) continue;
                switch (keyEvent.Key)
                {
                    case Key.Escape:
                        toggleMouseCapturePressed = true;
                        break;
                    case Key.F3:
                        toggleDebugPressed = true;
                        break;
                    case Key.E:
                        toggleInventoryPressed = true;
                        break;
                    case Key.B:
                        toggleBiomeMenuPressed = true;
                        break;
                    case Key.F8:
                        toggleHandEditorPressed = true;
                        break;
                    case Key.F:
                        cycleRenderDistancePressed = true;
                        break;
                    case Key.G:
                        spawnMobPressed = true;
                        break;
                    case Key.H:
                        spawnCoyotePressed = true;
                        break;
                    case Key.P:
                        spawnStevePressed = true;
                        break;
                    case Key.J:
                        spawnZombiePressed = true;
                        break;
                    case Key.O:
                        damageSelfPressed = true;
                        break;
                    case Key.Q:
                        dropItemPressed = true;
                        break;
                    case Key.F5:
                        toggleThirdPersonPressed = true;
                        break;
                    case Key.F6:
                        toggleFullbrightPressed = true;
                        break;
                    case Key.T:
                        advanceTimePressed = true;
                        break;
                    case Key.F7:
                        toggleGpuCullPressed = true;
                        break;
                    case Key.F11:
                        toggleFullscreenPressed = true;
                        break;
                    case Key.Enter:
                        if (keyEvent.Modifiers.HasFlag(ModifierKeys.Alt)) toggleFullscreenPressed = true;
                        break;
                    case Key.F2:
                        toggleFlyPressed = true;
                        break;
                    case Key.Number1:
                        selectedSlot = 0;
                        break;
                    case Key.Number2:
                        selectedSlot = 1;
                        break;
                    case Key.Number3:
                        selectedSlot = 2;
                        break;
                    case Key.Number4:
                        selectedSlot = 3;
                        break;
                    case Key.Number5:
                        selectedSlot = 4;
                        break;
                    case Key.Number6:
                        selectedSlot = 5;
                        break;
                    case Key.Number7:
                        selectedSlot = 6;
                        break;
                    case Key.Number8:
                        selectedSlot = 7;
                        break;
                    case Key.Number9:
                        selectedSlot = 8;
                        break;
                    case Key.Number0:
                        selectedSlot = 9;
                        break;
                }
            }

            foreach (var mouseEvent in snapshot.MouseEvents)
            {
                // Track held state on BOTH press and release so breakHeld/placeHeld reset properly.
                // (Skipping release events left breakHeld stuck true forever -> auto-mining.)
                if (mouseEvent.MouseButton == MouseButton.Left)
                {
                    breakHeld = mouseEvent.Down;
                    if (mouseEvent.Down) breakBlockPressed = true;
                }
                else if (mouseEvent.MouseButton == MouseButton.Right)
                {
                    placeHeld = mouseEvent.Down;
                    if (mouseEvent.Down) placeBlockPressed = true;
                }
            }

            if (!mouseCaptured)
            {
                hasLastMousePosition = false;
                return;
            }

            // Mouse wheel cycles the hotbar (negative = next slot) while the mouse is captured;
            // when it's freed (menus open) the wheel goes to ImGui instead.
            double wheel = snapshot.WheelDelta;
            if (wheel != 0) hotbarScroll -= (int)Math.Round(wheel);

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
                toggleBiomeMenuPressed,
                toggleHandEditorPressed,
                cycleRenderDistancePressed,
                spawnMobPressed,
                spawnCoyotePressed,
                spawnStevePressed,
                spawnZombiePressed,
                damageSelfPressed,
                toggleThirdPersonPressed,
                toggleFlyPressed,
                toggleFullbrightPressed,
                advanceTimePressed,
                toggleGpuCullPressed,
                toggleFullscreenPressed,
                breakBlockPressed,
                placeBlockPressed,
                dropItemPressed,
                selectedSlot,
                hotbarScroll);

            toggleMouseCapturePressed = false;
            toggleDebugPressed = false;
            toggleInventoryPressed = false;
            toggleBiomeMenuPressed = false;
            toggleHandEditorPressed = false;
            cycleRenderDistancePressed = false;
            spawnMobPressed = false;
            spawnCoyotePressed = false;
            spawnStevePressed = false;
            spawnZombiePressed = false;
            damageSelfPressed = false;
            toggleThirdPersonPressed = false;
            toggleFlyPressed = false;
            toggleFullbrightPressed = false;
            advanceTimePressed = false;
            toggleGpuCullPressed = false;
            toggleFullscreenPressed = false;
            breakBlockPressed = false;
            placeBlockPressed = false;
            dropItemPressed = false;
            selectedSlot = null;
            hotbarScroll = 0;

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
                moveUp,
                moveDown,
                breakHeld,
                placeHeld,
                Vector2.Zero);

            jumpPressed = false;

            return result;
        }
    }
}

