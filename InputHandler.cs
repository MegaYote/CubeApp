using System;
using System.Numerics;

namespace CubeApp
{
    /// <summary>
    /// Handles input processing and camera control.
    /// </summary>
    public sealed class InputHandler
    {
        private readonly InputProcessor _input;
        private bool _mouseLook;

        public float CameraYaw { get; private set; }
        public float CameraPitch { get; private set; }

        private const float MouseSensitivity = 0.5f;

        public InputHandler(InputProcessor input)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            CameraYaw = 0f;
            CameraPitch = 0f;
        }

        public bool IsMouseLookEnabled => _mouseLook;

        public void ToggleMouseLook()
        {
            if (_mouseLook)
            {
                DisableMouseLook();
            }
            else
            {
                EnableMouseLook();
            }
        }

        public void EnableMouseLook()
        {
            if (_mouseLook)
            {
                return;
            }

            _mouseLook = true;
            _input.ResetMouseTracking();
        }

        public void DisableMouseLook()
        {
            if (!_mouseLook)
            {
                return;
            }

            _mouseLook = false;
        }

        public FrameInputState CaptureFrameInput()
        {
            return _input.CaptureFrameInput();
        }

        public TickInputState CaptureTickInput()
        {
            return _input.CaptureTickInput();
        }

        public void ApplyLookInput(Vector2 lookDelta)
        {
            if (!_mouseLook)
            {
                return;
            }

            CameraYaw += lookDelta.X * MouseSensitivity;
            CameraPitch = Math.Clamp(CameraPitch - lookDelta.Y, -89f, 89f);
        }

        public Point3D GetCameraForward()
        {
            float yawRad = CameraYaw * (float)Math.PI / 180f;
            float pitchRad = CameraPitch * (float)Math.PI / 180f;

            return new Point3D(
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)-Math.Sin(pitchRad),
                (float)(-Math.Cos(yawRad) * Math.Cos(pitchRad)));
        }

        public Point3D GetMoveInput(TickInputState tickInput)
        {
            var forward = GetCameraForward();
            var forwardHorizontal = new Point3D(forward.X, 0, forward.Z).Normalized();
            var right = new Point3D(forwardHorizontal.Z, 0, -forwardHorizontal.X).Normalized();

            var moveInput = new Point3D(0, 0, 0);
            if (tickInput.MoveForward) moveInput = new Point3D(moveInput.X + forwardHorizontal.X, 0, moveInput.Z + forwardHorizontal.Z);
            if (tickInput.MoveBackward) moveInput = new Point3D(moveInput.X - forwardHorizontal.X, 0, moveInput.Z - forwardHorizontal.Z);
            if (tickInput.MoveLeft) moveInput = new Point3D(moveInput.X - right.X, 0, moveInput.Z - right.Z);
            if (tickInput.MoveRight) moveInput = new Point3D(moveInput.X + right.X, 0, moveInput.Z + right.Z);

            return moveInput.Normalized();
        }
    }
}
