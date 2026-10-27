namespace CiaGoober.Core;

// Crop area in source-image pixels. It may extend past the image; the outside is transparent.
public readonly record struct CropRect(double Left, double Top, double Width, double Height);
