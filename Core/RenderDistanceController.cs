using System;

namespace Cubuild
{
    /// <summary>
    /// Manages the render distance preset cycling and exposes the current radius.
    /// Extracted from Program.cs so the main loop doesn't need to know about render-distance UI details.
    /// </summary>
    public sealed class RenderDistanceController : IDisposable
    {
        private readonly Action<int> _onRadiusChanged;

        // presets in their cycle order: Super Far → Far → Normal → Short → Tiny → Super Far...
        private static readonly int[] Presets = { 32, 16, 8, 4, 2 };
        private static readonly string[] Names    = { "Super Far", "Far", "Normal", "Short", "Tiny" };

        private int _index;

        public RenderDistanceController(Action<int> onRadiusChanged)
        {
            _onRadiusChanged = onRadiusChanged ?? throw new ArgumentNullException(nameof(onRadiusChanged));
            _index = 1; // start at Normal (8 chunks)
        }

        public int CurrentIndex => _index;
        public int Radius => Presets[_index];
        public string Name => Names[_index];

        /// <summary>
        /// Cycles to the next preset. Returns the new radius so callers can apply it immediately.
        /// </summary>
        public int CycleNext()
        {
            _index = (_index + 1) % Presets.Length;
            int newRadius = Radius;
            _onRadiusChanged?.Invoke(newRadius);
            return newRadius;
        }

        public void Dispose() { }
    }
}
