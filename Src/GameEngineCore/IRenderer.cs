using System;
using System.Collections.Generic;
using System.Text;
using csPixelGameEngineCore.Enums;

namespace csPixelGameEngineCore
{
    /// <summary>
    /// Representation of the rendering system.
    /// </summary>
    /// <remarks>
    /// This roughly correlates to the Renderer class in PGE, which is pure abstract.
    /// </remarks>
    public /*interface*/ class IRenderer
    {
        public void PrepareDevice()
        { 
            //
        }

        public RCode CreateDevice(bool bFullScreen, bool bVSYNC, params object[] p)
        {    
            // Slight deviation, but more C#-style

            //

            return default;
        }

        public RCode DestroyDevice()
        {
            //

            return default;
        }

        public void ResizeWindow(int width, int height)
        { 
        }

        public void DisplayFrame()
        { 
        }

        public void PrepareDrawing()
        { 
        }

        public void DrawLayerQuad(vec2d_f offset, vec2d_f scale, Pixel tint)
        { 
        }

        public void DrawDecalQuad(DecalInstance decal)
        { 
        }

        public uint CreateTexture(uint width, uint height)
        {
            return 0;
        }
        
        public void UpdateTexture(uint id, Sprite spr)
        { 
        }

        public uint DeleteTexture(uint id)
        {
            return 0;
        }

        public void ApplyTexture(uint id)
        { 
        }

        public void UpdateViewport(vec2d_i pos, vec2d_i size)
        { 
        }

        public void ClearBuffer(Pixel p, bool bDepth)
        { 
        }

        public EventHandler<FrameUpdateEventArgs> RenderFrame { get; set; }

        public static implicit operator IRenderer(GLWindow v)
        {
            return new IRenderer();
        }
    }
}
