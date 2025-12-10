using System;

namespace SkillbarCapture
{
    public sealed class ImageBuffer
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Data { get; }

        public ImageBuffer(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            Data = new byte[width * height * 4];
        }

        public ImageBuffer(int width, int height, byte[] data)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length != width * height * 4)
                throw new ArgumentException("Data length must be width * height * 4.", nameof(data));

            Width = width;
            Height = height;
            Data = data;
        }
    }
}
