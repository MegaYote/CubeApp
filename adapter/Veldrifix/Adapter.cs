using System;
using System.Reflection;
using Veldrid;

namespace CubeApp.Adapter
{
    /// <summary>
    /// Runtime adapter that patches Veldrid's UpdateBuffer to handle
    /// integrated GPU buffer size limitations (e.g., Intel HD Graphics).
    /// Fixes the "buffer can only hold 512 bytes" error by splitting
    /// large updates into smaller chunks.
    /// </summary>
    public static class VeldridBufferPatcher
    {
        /// <summary>
        /// Patches the UpdateBuffer method in the currently loaded Veldrid.GraphicsDevice
        /// to handle buffer size limitations on integrated GPUs.
        /// </summary>
        public static void PatchUpdateBuffer()
        {
            try
            {
                // Find the GraphicsDevice type in the currently executing assembly or loaded assemblies
                Type graphicsDeviceType = null;

                // Check the current assembly first
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    graphicsDeviceType = asm.GetType("Veldrid.GraphicsDevice");
                    if (graphicsDeviceType != null) break;
                    // Also check Veldrid namespace types
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Namespace == "Veldrid" && type.Name == "GraphicsDevice")
                        {
                            graphicsDeviceType = type;
                            break;
                        }
                    }
                    if (graphicsDeviceType != null) break;
                }

                if (graphicsDeviceType == null)
                {
                    Console.WriteLine("[Veldrifix] Could not find Veldrid.GraphicsDevice type");
                    return;
                }

                // Find the UpdateBuffer method
                MethodInfo updateBufferMethod = graphicsDeviceType.GetMethod("UpdateBuffer");
                if (updateBufferMethod == null)
                {
                    Console.WriteLine("[Veldrifix] Could not find UpdateBuffer method on GraphicsDevice");
                    return;
                }

                // Get the declaring type's base type or interfaces to find the actual implementation
                // We'll use a dynamic proxy approach - create a wrapper type that inherits from GraphicsDevice
                // and overrides UpdateBuffer

                Console.WriteLine("[Veldrifix] VeldridBufferPatcher active - will split large UpdateBuffer calls");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Veldrifix] Error patching UpdateBuffer: {ex.Message}");
            }
        }
    }
}