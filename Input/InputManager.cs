using System;
using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;

namespace Cubuild
{
    /// <summary>
    /// Events dispatched when the player performs significant actions.
    /// Decoupled from raw input so downstream systems (renderer, collision, spawning) can query state without coupling to Program.cs.
    /// </summary>
    public struct PlayerAction
    {
        public bool ToggleMouseLookRequested;
        public bool ToggleDebugRequested;
        public bool ToggleInventoryRequested;
        public bool ToggleFlyRequested;
        public bool CycleRenderDistanceRequested;
        public bool SpawnMobRequested;
        public int? SelectedSlotChangedTo;
        public bool BreakBlockRequested;
        public bool PlaceBlockRequested;
    }

    /// <summary>
    /// Reads raw input state and produces PlayerAction events.
    /// Intended to replace the sprawling ApplyFrameInput() method in Program.cs.
    /// </summary>
    public sealed class InputManager : IDisposable
    {
        private readonly InputProcessor _input = new();
        private bool _mouseLook;
        private Sdl2Window? _window;

        // Exposed so the player controller / step simulation can query raw movement deltas.
        public TickInputState TickInput => _input.CaptureTickInput();
        public Vector2 LookDelta => _input.CaptureLookDelta();
        public FrameInputState FrameInput => _input.CaptureFrameInput();

        /// <summary>
        /// Whether mouse look is currently enabled (capturing mouse input).
        /// </summary>
        public bool IsMouseLookEnabled => _mouseLook;

        public void SetWindow(Sdl2Window window)
        {
            _window = window;
        }

        public void BeginFrame() => _input.BeginFrame();
        public void ProcessSnapshot(InputSnapshot snapshot, float mouseSensitivity)
            => _input.ProcessSnapshot(snapshot, _mouseLook, mouseSensitivity);

        /// <summary>
        /// Translates raw frame input into higher-level PlayerAction events.
        /// Returns null if no significant action was taken this frame.
        /// </summary>
        public PlayerAction? CaptureActions(bool needsMouseLookOnInteract)
        {
            var fi = FrameInput;
            if (!FrameInputStateExtensions.HasAny(fi))
                return null;

            var action = new PlayerAction();

            // --- Mouse capture toggle (usually ESC) ---
            if (fi.ToggleMouseCapturePressed)
            {
                DisableMouseLook();
                return null;
            }

            // Auto-enroll mouse look when the player tries to interact with blocks.
            if (!_mouseLook && needsMouseLookOnInteract &&
                (fi.BreakBlockPressed || fi.PlaceBlockPressed))
            {
                EnableMouseLook();
                return null; // defer other processing until mouse lock engages
            }

            if (fi.ToggleDebugPressed) action.ToggleDebugRequested = true;
            if (fi.ToggleInventoryPressed) action.ToggleInventoryRequested = true;
            if (fi.ToggleFlyPressed) action.ToggleFlyRequested = true;
            if (fi.CycleRenderDistancePressed) action.CycleRenderDistanceRequested = true;
            if (fi.SpawnMobPressed) action.SpawnMobRequested = true;
            if (fi.SelectedSlot.HasValue) action.SelectedSlotChangedTo = fi.SelectedSlot.Value;

            // Break/Place are handled by downstream systems once mouse look is active.
            if (_mouseLook && fi.BreakBlockPressed) action.BreakBlockRequested = true;
            if (_mouseLook && fi.PlaceBlockPressed) action.PlaceBlockRequested = true;

            return action;
        }

        public void EnableMouseLook()
        {
            if (_mouseLook) return;
            _mouseLook = true;
            if (_window != null)
            {
                ApplyMouseCapture(_window, true);
            }
            _input.ResetMouseTracking();
        }

        public void DisableMouseLook()
        {
            if (!_mouseLook) return;
            _mouseLook = false;
            if (_window != null)
            {
                ApplyMouseCapture(_window, false);
            }
            _input.ResetMouseTracking();
        }

        public void ResetMouseTracking() => _input.ResetMouseTracking();

        public void Dispose() { }

        private static void ApplyMouseCapture(Sdl2Window window, bool capture)
        {
            window.CursorVisible = !capture;
            Sdl2Native.SDL_ShowCursor(capture ? 0 : 1);
            Sdl2Native.SDL_CaptureMouse(capture);
            Sdl2Native.SDL_SetRelativeMouseMode(capture);
        }
    }

    /// <summary>
    /// Extension methods for FrameInputState.
    /// </summary>
    internal static class FrameInputStateExtensions
    {
        /// <summary>
        /// Convenience: returns true when at least one frame-input flag is set.
        /// </summary>
        public static bool HasAny(this FrameInputState fi) =>
            fi.ToggleMouseCapturePressed
            || fi.ToggleDebugPressed
            || fi.ToggleInventoryPressed
            || fi.ToggleFlyPressed
            || fi.CycleRenderDistancePressed
            || fi.SpawnMobPressed
            || fi.SelectedSlot.HasValue
            || fi.BreakBlockPressed
            || fi.PlaceBlockPressed;
    }
}