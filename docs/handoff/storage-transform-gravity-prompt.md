# Session task — Storage transforms: two bugs and a crop anchor

> **Status: shipped.** 2026-09-04 — see `docs/handoff/storage-transform-gravity-report.md`. One
> deviation from scope: `?background=` was dropped in favor of a fixed white flatten (the prompt's own
> sanctioned fallback) — see the report's "Deviations" section.

## Why this exists

Storage Phase 3 shipped image transforms (`?width=`/`?height=`/`?format=`/`?quality=`). Comparing the
result against Appwrite's `getFilePreview` surfaced **two correctness bugs** and **one genuine feature
gap**. This is a small follow-up, not a phase: read `docs/research/storage.md`'s "Phase 3" section for
the design this builds on, then `CLAUDE.md`. Work on a new branch off `main`.

The bugs are worth fixing whether or not you do the feature. Both were observed on a running instance,
not inferred.

## 1. Transparent pixels become black on JPEG conversion (bug)

Verified: a PNG whose right half is fully transparent, requested as
`?width=128&format=jpeg`, comes back with that half **`rgb(0,0,0)`**. JPEG has no alpha channel so
*something* must fill it — but black is a surprising default, and transparent-background logos and
icons are exactly what people thumbnail. Most services flatten onto white.

`ImageTransformer` makes no explicit choice today (`grep` for background/alpha/SKColors finds nothing
but a `SKBitmap` constructor inheriting `AlphaType`), so this is falling out of Skia's defaults rather
than being decided.

**Fix**: flatten onto an explicit background when the target format has no alpha. Default **white**,
and take Appwrite's lead by making it settable — see item 3.

## 2. EXIF orientation is ignored (bug)

`grep -i exif|orientation|EncodedOrigin` across `src/Praxy.Storage` finds nothing. A photo from a phone
carries its rotation in an EXIF tag rather than in the pixel data, so every such image will transform
sideways or upside down. This is the single most likely "the thumbnails are broken" report from real
use, and it affects the plain-download path's *appearance* not at all — only transforms — which makes
it easy to miss in testing with synthetic images.

**Fix**: `SKCodec` exposes `EncodedOrigin`; apply it before resizing. Test with a real EXIF-tagged
JPEG, not a canvas-generated one — a synthetic image has no orientation tag and will pass a broken
implementation.

## 3. `gravity` — the crop anchor (feature)

When both `width` and `height` are given and the aspect ratios differ, the current implementation
crops centred. Cropping a portrait to a square centred cuts heads off, which is precisely the avatar
case transforms exist for.

Add a `gravity` parameter matching Appwrite's vocabulary — `center` (default), `top-left`, `top`,
`top-right`, `left`, `right`, `bottom-left`, `bottom`, `bottom-right`. A small closed enum, so it
costs the derivative key space almost nothing.

## Non-goals — and the reason matters

**Do not add `borderWidth`, `borderColor`, `borderRadius`, `opacity`, or `rotation`**, even though
Appwrite has them. Two reasons, and the second is the important one:

1. CSS does all of them better, on the client, with no cache cost.
2. **Every transform parameter multiplies the cached-derivative key space**, which is the exact thing
   `DimensionLadder` exists to bound. `rotation` alone is 360 values; `background` is 16M colours;
   `opacity` is 100. Adding them re-opens the storage-amplification vector Phase 3 was designed to
   close. `gravity` is acceptable *because* it is a nine-value enum.

This constraint is why `background` (item 1) must be **bounded**: accept a short hex colour and
**include it in the derivative key**, so two different backgrounds are two cache entries rather than
one being silently served for the other. If bounding it proves awkward, shipping only a fixed white
flatten is a perfectly good outcome — say so in the report rather than accepting an unbounded key.

## Scope

1. Flatten to an explicit background when encoding to a format without alpha. Default white.
2. Honour `SKCodec.EncodedOrigin` before resize/crop.
3. `?gravity=` with the nine-value enum, defaulting to `center`, folded into the derivative key.
4. `?background=` as a short hex (`RRGGBB`), folded into the derivative key, rejected with a clean
   `400` if malformed — the same way `?width=abc` already is.
5. Console: expose `gravity` where the file sheet shows transforms, if that surface exists; otherwise
   skip the console entirely and say so — this is a URL-level feature and does not need UI.
6. SDKs: add the parameters to the download-URL builders in `@praxy/core` and the Flutter SDK.

## Tests

- Unit: gravity offset arithmetic for all nine anchors against both a wider-than-target and
  taller-than-target source; hex parsing including malformed input; the derivative key differing by
  gravity and by background.
- Integration: a transparent PNG to JPEG lands on **white** by default and on the requested colour
  when asked; a `gravity=top` crop of a tall source keeps the top rather than the middle (assert
  pixels, not just dimensions); **an EXIF-rotated JPEG comes out upright** — commit a small real
  fixture, since a generated image cannot exercise this.

## Done means

- `dotnet test` green.
- Console build clean; SDK typecheck/test green if touched.
- **Owner test, actually run**: transform a real phone photo (one with EXIF orientation) and confirm
  it is upright; convert a transparent-background logo to JPEG and confirm it is on white, not black;
  crop a portrait to a square with `gravity=top` and confirm the subject survives.
- `git status` clean, conventional commits, branch off `main`.
- Write `docs/handoff/storage-transform-gravity-report.md`, and state explicitly whether `background`
  was bounded into the key or dropped in favour of a fixed white flatten.
