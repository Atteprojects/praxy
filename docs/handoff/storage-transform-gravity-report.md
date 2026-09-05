# Storage transforms: two bugs and a crop anchor — report

**Status: complete.** Every item in
`docs/handoff/storage-transform-gravity-prompt.md`'s scope shipped except `?background=` (see
"Deviations" — the fallback the prompt itself sanctioned). Targeted `dotnet test` green: **604/604
unit, 10/10 `StorageDerivativesTests`, 4/4 `OpenApiDocumentTests`** (real Postgres via Testcontainers).
Console `tsc --noEmit` and `npm run build --prefix console` clean. `@praxy/core` (`npm test -w
packages/core`): 81/81. Flutter `praxy_core` (`dart test praxy_core`): 80/80, `dart analyze .` clean
(pre-existing, unrelated `praxy_flutter` lints only).

## What shipped

**Bug #1 — transparent PNG → JPEG landed on black.** [`ImageTransformer.Transform`](../../src/Praxy.Storage/ImageTransformer.cs)
now flattens onto an opaque white bitmap (`FlattenOntoWhite`) whenever the target format is `jpeg`
(the only one of the three supported formats with no alpha channel); `png`/`webp` targets are
untouched. Verified end-to-end (`A_transparent_png_converted_to_jpeg_lands_on_white_not_black`): a
half-transparent PNG converted to JPEG comes back with the transparent half white and the opaque half
its original color.

**Bug #2 — EXIF orientation ignored.** `ImageTransformer.Transform` now reads `SKCodec.EncodedOrigin`
and corrects the decoded bitmap (`ApplyOrientation`) before any resize/crop, via one affine `SKMatrix`
per non-identity origin — each derived directly from where a source pixel must land (e.g. a 90°
clockwise rotation sends `(x, y)` to `(height − y, x)`), not assumed from memory, and cross-checked
against SkiaSharp 4.151.2's actual `SKMatrix` constructor argument order via reflection before writing
any of the eight cases. `DerivativesService.SourceDimensionsAsync`'s header-only probe (used to derive
the other axis on a single-dimension request) needed the same correction — it now swaps width/height
for the four orientations that are a 90°/270° rotation, or a rotated phone photo's single-axis request
(`?width=` alone) would derive the other axis against the wrong aspect ratio. `ImageTransformer.SwapsDimensions`
is the one shared piece of that logic.

Verified with a **real, committed EXIF-tagged fixture**
(`tests/Praxy.Tests.Integration/Fixtures/exif-orientation-6.jpg`, see its `README.md`), not a
canvas-generated image — the prompt's own warning that a synthetic image has no orientation tag and
would pass a broken implementation just as easily as a correct one. The fixture is a hand-built JPEG
(genuine `APP1`/EXIF `Orientation=6` segment inserted after the SOI marker) that reconstructs a
red-top/blue-bottom "flag" once corrected; the sanity checks that produced it (decode dimensions,
`EncodedOrigin`, and running the real `ImageTransformer` against it) are recorded in the fixture's
README so it can be regenerated or extended.

**Feature #3 — `gravity`.** Nine-value enum (`ImageTransforms.Gravities`: `center`, `top-left`, `top`,
`top-right`, `left`, `right`, `bottom-left`, `bottom`, `bottom-right`), validated unconditionally (a
typo is a clean `400` even on a request that won't crop) and folded into the shared `"center"` default
whenever the resolved request doesn't actually crop — gravity has no visual effect there, so letting it
vary would fragment the cache with byte-identical rows, the same reasoning `quality`'s `png` sentinel
already uses. The crop-offset arithmetic (`ImageTransforms.GravityOffset`) is a pure function — no
Skia, no database — expressed as halves (`0`, `1`, `2` over `2`) so the `center` case reduces to
exactly the original centered-only implementation's integer division, keeping that path's output
byte-identical to before this change. `ImageTransformer.ResizeAndCrop` now takes a `gravity` and
anchors the crop box accordingly.

`file_derivatives` gained a `gravity` column (migration `20260905062807_StorageDerivativeGravity`),
folded into the unique cache-key index (`file_id, width, height, format, quality, gravity`). The
migration backfills existing rows with `"center"`, not an empty string — every derivative generated
before this change *was* centered (the only behavior that existed), so that's the true value for that
data, and it's what a post-migration request for the same crop resolves to; any other default would
silently orphan every pre-existing cropped derivative's cache entry.

**Console**: the file sheet's Derivatives list (the only surface that shows a transform — there is no
request-builder UI, so item 5's "if that surface exists" pointed here) now appends the gravity when
it isn't `center` (`console/src/screens/BucketFilesPage.tsx`), matching the existing pattern for
`quality`. `FileDerivative`'s TypeScript type and the `FileDerivativeResponse` API DTO both gained
`gravity` (always present — `"center"` for an uncropped derivative is the real value, not a missing
one). `docs/openapi/v1.json` regenerated; the diff is exactly this one field.

**SDKs**: `gravity?: ...` added to `@praxy/core`'s `FileTransformOptions` (`sdk/js/packages/core/src/services/storage.ts`)
and to Flutter's `FileTransform` (`sdk/flutter/praxy_core/lib/src/services/storage_service.dart`), both
threaded into the download URL's query string exactly like the four existing parameters. One test added
per SDK confirming the new parameter is sent.

## Deviations: `background` was dropped, not bounded

Item 4 asked for `?background=` (a short hex color, folded into the derivative key, rejected on
malformed input). **It was not implemented.** The prompt itself flagged this as the likely outcome and
explicitly sanctioned it: "if bounding it proves awkward, shipping only a fixed white flatten is a
perfectly good outcome." Bounding turned out to be more than awkward — it's the one parameter of the
three that genuinely can't be bounded the way `gravity` was, for a reason specific to what it is:

- `gravity` is acceptable as a free cache-key dimension because it's *inherently* a small closed set —
  nine physically meaningful anchors, no more exist to add.
- `background` is a color. Bounding it to a small enum (say, `white`/`black`/`transparent`) would
  satisfy "bounded" but not "settable" — the entire point of the parameter, per the prompt's own
  framing ("take Appwrite's lead by making it settable"), is that a caller picks *their* brand's exact
  hex. Any enum small enough to bound the key space is too small to be the feature being asked for.
- The only way to keep an arbitrary hex genuinely settable *and* bounded would be to stop caching
  non-default backgrounds — generate them on the fly, uncached, so a walked `?background=000001..FFFFFF`
  produces zero new rows instead of up to 16.7 million. That's a real, coherent design, but it's a
  second code path (a cache-bypass branch in `DerivativesService.ResolveAsync`) and a different feature
  from what item 4 literally describes ("folded into the derivative key") — inventing it wasn't this
  follow-up's call to make unprompted, and the prompt's own escape hatch was to ship the fixed flatten
  instead and say so here.

So: **background is fixed white, not a parameter.** Bug #1 is fully fixed (transparent → white, not
black); the "or the requested colour" half of feature request is the one piece of the prompt not done.
If a caller-chosen background becomes a real ask later, the uncached-generation approach above is the
starting point.

## Tests

- **Unit** (`tests/Praxy.Tests.Unit/ImageTransformsTests.cs`): gravity defaults to `center`; is kept
  when the request actually crops; is normalized to `center` when it can't have an effect (only one
  axis given, or neither); an unsupported value is rejected unconditionally; two otherwise-identical
  crops differing only in gravity resolve to different keys. `GravityOffset` exercised directly (no
  Skia) against both a wider-than-target and a taller-than-target source, all nine anchors each —
  18 cases total, matching the prompt's explicit ask.
- **Integration** (`tests/Praxy.Tests.Integration/StorageDerivativesTests.cs`, three new tests): the
  transparent-PNG-to-JPEG flatten (pixel-sampled, not just "it didn't error"); a `gravity=top` crop of
  a deliberately two-tone tall source lands on the top color at the center of the output while the
  default (centered) crop lands on the bottom color at the same point — proof gravity actually moved
  the crop box, not just that dimensions came out right; the EXIF fixture comes out upright (`48x64`,
  not the stored `64x48`) with the right colors in the right places.

## Commands

No new config knobs. `dotnet ef migrations add StorageDerivativeGravity` (from `src/Praxy.Persistence`,
per `CLAUDE.md`) is how `20260905062807_StorageDerivativeGravity` was generated — the migration's
backfill default was hand-edited afterward from EF's auto-generated `""` to `"center"` (see above); no
other change to the dev/self-host workflow.

## Owner-test checklist

(Repeating the prompt's own "Done means" — this needs a real image and a real running instance, not
something this session's automated tests can substitute for.)

- Transform a real phone photo that actually has EXIF orientation (most modern phone cameras write it
  by default) via `?width=256` or similar and confirm it comes out upright, not sideways.
- Convert a transparent-background logo/icon to JPEG (`?format=jpeg`) and confirm the transparent area
  is white, not black.
- Crop a portrait photo to a square with `?width=256&height=256&gravity=top` and confirm the subject's
  head survives, versus the same request without `gravity` (or `gravity=center`) cutting it off.
- Open a file with at least one cached derivative in the console (Storage → a bucket → Files → a
  file's permissions button → Derivatives) and confirm a non-default gravity value shows in the list.

## Next

No further prompt is written — this was a standalone gap-closing follow-up, not part of a numbered
phase or a new initiative. The one concrete, separately-scoped idea worth a future kickoff if a
caller-chosen background actually gets requested: uncached, on-the-fly generation for any
`?background=` other than the default, so the parameter can be genuinely free-form without reopening
the derivative-cache amplification vector (see "Deviations" above).
