# Fixtures

`exif-orientation-6.jpg` — a 64×48 JPEG (left half red, right half blue) carrying a genuine EXIF
`Orientation` tag (value `6`, `SKEncodedOrigin.RightTop`: rotate 90° clockwise to display upright).
Corrected, it reconstructs a 48×64 image with a red top half and a blue bottom half — used by
`StorageDerivativesTests` to assert the download endpoint actually applies `SKCodec.EncodedOrigin`
before resizing/cropping, rather than passing a canvas-generated image through untouched (which,
having no orientation tag at all, would pass a broken implementation just as easily as a correct one).

Regenerate with a throwaway console app referencing `Praxy.Storage` and `SkiaSharp`:

1. Build a 64×48 `SKBitmap`, left half `SKColors.Red`, right half `SKColors.Blue`.
2. Encode to JPEG.
3. Insert a hand-built EXIF `APP1` segment right after the SOI marker: `"Exif\0\0"` + a
   little-endian TIFF header (magic `42`, IFD0 at offset `8`) + one IFD0 entry
   (tag `0x0112` Orientation, type `SHORT`, count `1`, value `6`) + a `0` next-IFD offset.
4. Sanity-check with `SKCodec.Create` (`EncodedOrigin == RightTop`, raw `Info` is `64x48`) and by
   running the real `ImageTransformer.Transform` against it, asserting the output is `48x64` with a
   red pixel near the top and a blue one near the bottom.
