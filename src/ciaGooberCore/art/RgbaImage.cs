namespace CiaGoober.Core;

// Straight (non-premultiplied) RGBA, 4 bytes per pixel, row-major.
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);
