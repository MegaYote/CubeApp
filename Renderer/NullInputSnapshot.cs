using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;

namespace CubeApp.Renderer
{
    /// <summary>
    /// Minimal no-op InputSnapshot. The HUD rendered via ImGui in this project is
    /// informational only (no interactive widgets), so there is no need to bridge
    /// WinForms input events into ImGui's input model.
    /// </summary>
    internal sealed class NullInputSnapshot : InputSnapshot
    {
        public static readonly NullInputSnapshot Instance = new NullInputSnapshot();

        private static readonly IReadOnlyList<KeyEvent> EmptyKeyEvents = Array.Empty<KeyEvent>();
        private static readonly IReadOnlyList<MouseEvent> EmptyMouseEvents = Array.Empty<MouseEvent>();
        private static readonly IReadOnlyList<char> EmptyKeyCharPresses = Array.Empty<char>();

        public IReadOnlyList<KeyEvent> KeyEvents => EmptyKeyEvents;
        public IReadOnlyList<MouseEvent> MouseEvents => EmptyMouseEvents;
        public IReadOnlyList<char> KeyCharPresses => EmptyKeyCharPresses;
        public Vector2 MousePosition => Vector2.Zero;
        public float WheelDelta => 0f;

        public bool IsMouseDown(MouseButton button) => false;
    }
}
