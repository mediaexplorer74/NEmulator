using System;

namespace csPixelGameEngineCore
{
    public class Span<T>
    {
        private Pixel[] colorData;
        private int v1;
        private int v2;

        public Span(Pixel[] colorData, int v1, int v2)
        {
            this.colorData = colorData;
            this.v1 = v1;
            this.v2 = v2;
        }

        internal void Fill(Pixel p)
        {
            throw new NotImplementedException();
        }
    }
}