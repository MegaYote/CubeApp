using System;

namespace Cubuild.Renderer
{
    /// <summary>Builds the transient GPU atlas used by block-rendering pipelines.</summary>
    internal static class BlockAtlasComposer
    {
        public const int LocalAtlasWidth = 256;
        public const int LocalAtlasHeight = 256;
        public const int RuntimeAtlasWidth = LocalAtlasWidth * 4;
        public const int RuntimeAtlasHeight = LocalAtlasHeight;
        private const int BytesPerPixel = 4;

        public static byte[] Compose(byte[] stockRgba, int stockWidth, int stockHeight, byte[] customRgba)
        {
            ArgumentNullException.ThrowIfNull(stockRgba);
            ArgumentNullException.ThrowIfNull(customRgba);
            if (stockWidth != LocalAtlasWidth || stockHeight != LocalAtlasHeight)
                throw new ArgumentException($"Stock block atlas must be {LocalAtlasWidth}x{LocalAtlasHeight}; got {stockWidth}x{stockHeight}.", nameof(stockRgba));
            int localBytes = LocalAtlasWidth * LocalAtlasHeight * BytesPerPixel;
            if (stockRgba.Length != localBytes)
                throw new ArgumentException("Stock block atlas pixel data does not match its dimensions.", nameof(stockRgba));
            int customBytes = localBytes * CustomBlockStore.CustomPageCount;
            if (customRgba.Length != customBytes)
                throw new ArgumentException("Custom block atlas must contain exactly three 256x256 RGBA pages.", nameof(customRgba));

            var combined = new byte[RuntimeAtlasWidth * RuntimeAtlasHeight * BytesPerPixel];
            int sourceRowBytes = LocalAtlasWidth * BytesPerPixel;
            int customRowBytes = sourceRowBytes * CustomBlockStore.CustomPageCount;
            int targetRowBytes = RuntimeAtlasWidth * BytesPerPixel;
            for (int y = 0; y < LocalAtlasHeight; y++)
            {
                Buffer.BlockCopy(stockRgba, y * sourceRowBytes, combined, y * targetRowBytes, sourceRowBytes);
                Buffer.BlockCopy(customRgba, y * customRowBytes,
                    combined, y * targetRowBytes + sourceRowBytes, customRowBytes);
            }
            return combined;
        }
    }
}
