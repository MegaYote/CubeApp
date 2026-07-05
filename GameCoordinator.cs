using System;

namespace CubeApp
{
    /// <summary>
    /// Orchestrates game components and coordinates their interactions.
    /// </summary>
    public sealed class GameCoordinator
    {
        private readonly ChunkManager _chunkManager;
        private readonly MeshScheduler _meshScheduler;
        private readonly PlayerController _playerController;
        private readonly InputHandler _inputHandler;
        private readonly EntityManager _entityManager;
        private readonly BlockInteraction _blockInteraction;

        public GameCoordinator(
            ChunkManager chunkManager,
            MeshScheduler meshScheduler,
            PlayerController playerController,
            InputHandler inputHandler,
            EntityManager entityManager,
            BlockInteraction blockInteraction)
        {
            _chunkManager = chunkManager ?? throw new ArgumentNullException(nameof(chunkManager));
            _meshScheduler = meshScheduler ?? throw new ArgumentNullException(nameof(meshScheduler));
            _playerController = playerController ?? throw new ArgumentNullException(nameof(playerController));
            _inputHandler = inputHandler ?? throw new ArgumentNullException(nameof(inputHandler));
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));
            _blockInteraction = blockInteraction ?? throw new ArgumentNullException(nameof(blockInteraction));
        }

        public PlayerController Player => _playerController;
        public InputHandler Input => _inputHandler;
        public EntityManager Entities => _entityManager;
        public BlockInteraction BlockInteraction => _blockInteraction;

        public void Update(float deltaSeconds)
        {
            var tickInput = _inputHandler.CaptureTickInput();
            var moveInput = _inputHandler.GetMoveInput(tickInput);

            // Update player
            _playerController.SetCameraDirection(_inputHandler.CameraYaw, _inputHandler.CameraPitch);
            _playerController.Update(deltaSeconds, moveInput, tickInput.Jump);

            // Update entities
            _entityManager.Update(deltaSeconds);
        }

        public void HandleFrameInput(FrameInputState frameInput)
        {
            if (frameInput.ToggleMouseCapturePressed)
            {
                _inputHandler.ToggleMouseLook();
            }

            if (frameInput.LookDelta != null)
            {
                _inputHandler.ApplyLookInput(frameInput.LookDelta.Value);
            }

            if (frameInput.DeleteBlockPressed)
            {
                _blockInteraction.DeleteBlock(_playerController.Position, _inputHandler.GetCameraForward());
            }

            if (frameInput.PlaceBlockPressed)
            {
                _blockInteraction.PlaceBlock(_playerController.Position, _inputHandler.GetCameraForward());
            }

            if (frameInput.SpawnDuckPressed)
            {
                _entityManager.SpawnDuck(_playerController.Position, _inputHandler.CameraYaw);
            }
        }

        public Point3D GetCameraPosition()
        {
            return _playerController.Position;
        }

        public float GetCameraYaw()
        {
            return _inputHandler.CameraYaw;
        }

        public float GetCameraPitch()
        {
            return _inputHandler.CameraPitch;
        }

        public Point3D GetCameraForward()
        {
            return _inputHandler.GetCameraForward();
        }
    }
}
