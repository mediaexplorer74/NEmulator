using System;
using System.Collections.Generic;
using System.Text;
using csPixelGameEngineCore.Enums;

namespace csPixelGameEngineCore
{
    /// <summary>
    /// This will serve a different purpose than the original, since .NET Core is already platform independent. 
    /// 
    /// We will mainly use this for attaching window event handlers to the underlying window system.
    /// </summary>
    public class IPlatform
    {
        public RCode CreateGraphics(bool fullscreen, bool enableVSync,
            vec2d_i viewPos, vec2d_i viewSize)
        {
            return default;
        }
        public RCode SetWindowTitle(string title)
        {
            return default;
        }

        public RCode StartSystemEventLoop()
        {
            return default;
        }

        public int WindowWidth  { get; set; }
        public int WindowHeight { get; set;  }

        // The C++ version does not have these, but we need them for C#
        public KeyboardState KeyboardState { get; set; }
        public event EventHandler<EventArgs> Closed;
        public event EventHandler<EventArgs> Resize;
        public event EventHandler<MouseMoveEventArgs> MouseMove;
        public event EventHandler<MouseWheelEventArgs> MouseWheel;
        public event EventHandler<MouseButtonEventArgs> MouseDown;
        public event EventHandler<MouseButtonEventArgs> MouseUp;
        public event EventHandler<KeyboardEventArgs> KeyDown;
        public EventHandler<FrameUpdateEventArgs> UpdateFrame { get; set; }
    }
}
