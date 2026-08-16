# CubeApp Dynamic Graphics Backend Switching

## Objective

Implement automatic graphics backend selection at startup so the game runs on both high-end GPUs (Direct3D11) and integrated GPUs like Intel HD Graphics (OpenGL), without requiring user configuration.

## Current State

- **File**: `CubeApp/Program.cs` line 763
- **Code**: `GraphicsBackend.Direct3D11` hardcoded
- **Problem**: On laptop with Intel HD Graphics, Direct3D11 fails with buffer size errors ("UpdateBuffer data too large - can only hold 512 bytes")
- **Result**: Terrain/blocks invisible, game runs but with rendering issues

## Solution Architecture

Use Veldrid's `Veldrid.StartupUtilities.CreateWindowAndGraphicsDevice()` with backend fallback ordering:

```
Direct3D11 → OpenGL → Vulkan (try in order, use first that succeeds)
```

## Implementation

### Phase 1: Modify Program.cs Backend Initialization

**File**: `C:\Users\User\CubeApp\Program.cs`

**Current code (lines 762-763)**:
```csharp
VeldridStartup.CreateWindowAndGraphicsDevice(windowCreateInfo, graphicsDeviceOptions,
    GraphicsBackend.Direct3D11, out var createdWindow, out var createdGraphicsDevice);
```

**Replace with**:
```csharp
// Dynamic backend selection: try Direct3D11 first, fall back to OpenGL on integrated GPUs
GraphicsBackend[] backends = new GraphicsBackend[] {
    GraphicsBackend.Direct3D11,
    GraphicsBackend.OpenGL,
    GraphicsBackend.Vulkan
};

GraphicsBackend? selectedBackend = null;
GraphicsDevice? gd = null;

foreach (var backend in backends)
{
    try
    {
        var fallbackGdo = new GraphicsDeviceOptions(
            debug: false, swapchainDepthFormat: PixelFormat.D24_UNorm_S8_UInt,
            syncToVerticalBlank: false, resourceBindingModel: ResourceBindingModel.Improved,
            preferDepthRangeZeroToOne: true, preferStandardClipSpaceYDirection: true);
        
        var result = VeldridStartup.CreateWindowAndGraphicsDevice(
            windowCreateInfo, fallbackGdo, backend, out var fallbackWindow, out var fallbackDevice);
        
        if (result == ReturnCode.Success)
        {
            selectedBackend = backend;
            gd = fallbackDevice;
            window = fallbackWindow;
            break;
        }
    }
    catch (Exception)
    {
        // Try next backend
    }
}

// If no backend succeeded, fail gracefully
if (gd == null)
{
    throw new Exception("No supported graphics backend found on this system");
}
```

### Phase 2: Update Rest of Program.cs

Ensure all subsequent code uses the dynamically-selected `window` and `gd` variables (created in Phase 1) instead of the old hardcoded instances.

### Phase 3: Add Fallback UI (Optional)

```csharp
// After backend selection, add:
Console.WriteLine($"[Graphics] Using backend: {selectedBackend}");
if (selectedBackend == GraphicsBackend.OpenGL)
{
    Console.WriteLine("[Graphics] Note: Using OpenGL backend for integrated GPU support");
    Console.WriteLine("[Graphics] Some features may be limited compared to Direct3D11");
}
```

### Phase 4: Testing Strategy

**Test on High-End PC (NVIDIA/AMD GPU)**:
- Expect: Direct3D11 selected
- Verify: Full performance, all features work, terrain renders correctly

**Test on Laptop (Intel HD Graphics)**:
- Expect: Falls back to OpenGL after Direct3D11 fails
- Verify: Terrain renders correctly with OpenGL, no buffer size errors

**Test Both Machines with Same Executable**:
- Build once, run on both machines
- Backend automatically selected per hardware

## Files to Modify

1. **`C:\Users\User\CubeApp\Program.cs`** - Main change: backend selection logic (lines 762-763)
2. **`C:\Users\User\CubeApp\CubeApp.exe.config`** - May need updates
3. **`C:\Users\User\CubeApp\bin\Release\net9.0-windows\win-x64\`** - Rebuild after changes

## NuGet Packages (Already Included)

Verify these are in `CubeApp.csproj`:
- `Veldrid` Version `4.9.0` ✓
- `Veldrid.StartupUtilities` Version `4.9.0` ✓
- `Veldrid.SPIRV` Version `1.0.15` ✓
- `Veldrid.ImGui` Version `5.89.2-ga121087cad` ✓
- `Veldrid.SDL2` Version `4.9.0` ✓

## Option A: Voxel-Specific Renderer (Alternative Approach)

### Evaluation (August 2026)

**Project Created**: `C:\Users\User\CubeApp\renderer_test\MinimalVoxelRenderer\`

**Status**: Project structure set up with OpenTK 4.8.0 and OpenTK.Windowing.Desktop 4.8.0 packages.

**Project builds successfully** with:
- OpenTK.Graphics framework
- OpenTK.Windowing for GameWindow support
- OpenTK.Mathematics for matrix operations
- All native OpenTK DLLs included in build output

**Program.cs**: Contains stub Main method; OpenTK API calls for cube drawing can be added with AI assistance in ~5-10 minutes.

**Key Advantage**: Custom renderer tailored specifically to voxel/cube game workloads, with the ability to optimize buffer sizes, mesh generation, and rendering pipeline for this exact use case.

**Estimated Effort**: 2-4 weeks (reduced from 6-12 months due to:
- Focused workload (cubes/chunks/textures only)
- AI assistance for complex API code
- No need for PBR, particles, UI, post-processing systems

**Risk**: Low - can fall back to Veldrid + dynamic switching at any time. Separate folder `C:\Users\User\CubeApp\renderer_test\` keeps experiments isolated.

**Decision**: User proceeding with Option A - building custom voxel renderer alongside existing Veldrid implementation. Goal: proof of concept cube on screen, then evaluate whether to replace Veldrid or merge concepts.

### Phase 1: Project Setup (Completed)

- ✅ Created `C:\Users\User\CubeApp\renderer_test\MinimalVoxelRenderer\`
- ✅ Added OpenTK 4.8.0 and OpenTK.Windowing.Desktop 4.8.0 packages
- ✅ Project builds successfully
- ✅ Program.cs structure ready for renderer code

### Phase 2: Basic Renderer Functionality (Next)

**Goal**: Get a single cube rotating on screen on both GPU types.

**Step 1**: AI generates OpenGL + Direct3D11 cube drawing code
- Query GPU for max uniform buffer size
- Allocate appropriately sized buffers
- Draw cube with texture atlas

**Step 2**: Test on both machines
- PC: Direct3D11 backend (via Veldrid or native OpenTK)
- Laptop: OpenGL backend (via native OpenTK)

**Step 3**: Integrate with game world data
- Feed chunk mesh data to new renderer
- Compare performance/quality with Veldrid

### Phase 3: Decision Point (Week 4)

- **Keep custom renderer**: If it solves buffer issues and performs well
- **Revert to Veldrid**: If too much effort for too little gain
- **Hybrid approach**: Use custom renderer for specific features, Veldrid for rest

## Quick Reference for Another Opencode Instance

```text
TASK: Implement dynamic Veldrid graphics backend switching in CubeApp

SPECIFICS:
1. Open C:\Users\User\CubeApp\Program.cs
2. Find line 762-763: GraphicsBackend.Direct3D11
3. Replace with backend fallback loop trying: Direct3D11 → OpenGL → Vulkan
4. Capture selected backend and window/graphicsDevice variables
5. Ensure all subsequent code uses the dynamically-selected instances
6. Rebuild and test on both machines

DELIVERABLES:
- Modified Program.cs with backend selection logic
- Working executable that auto-selects best backend per hardware
- Documentation of which backend each machine uses

--- ALTERNATIVE: Voxel-Specific Renderer ---

TASK: Set up custom voxel renderer project

SPECIFICS:
1. Create new .NET project at C:\Users\User\CubeApp\renderer_test\MinimalVoxelRenderer\
2. Add OpenTK 4.8.0 and OpenTK.Windowing.Desktop 4.8.0 packages
3. Build project (verified working)
4. Add GameWindow subclass with OnLoad/OnRenderFrame/OnUpdateFrame
5. AI: Generate cube drawing code with proper buffer size querying
6. Test cube on both PC (D3D11) and laptop (OpenGL)
7. Integrate chunk mesh data from existing game

DELIVERABLES:
- Working .NET project with OpenTK references
- Cube rotating on screen via OpenGL/Direct3D11
- Proof of concept for custom voxel renderer
- Evaluation data: does it solve the buffer size issue?
```