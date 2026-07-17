# Realistic Masonry Design

Status: proposed architecture. This document records the agreed direction for
future work; it does not claim that every part is implemented today.

## Goals

- Preserve exact-looking cardinal and 45-degree masonry without introducing
  visual seams, stretched textures, or incorrect collision.
- Make repeated arrangements deterministic so derived geometry and meshes can
  be reused under bounded LRU caches.
- Keep live editing responsive while allowing frozen, canonical cells to use
  entityless static storage when that renderer is validated.
- Avoid unbounded server ticks, mesh memory, network snapshots, and chunk
  sidecar writes.

## Coordinate Model

Cardinal courses remain on their normal quarter-block pitch. Their start,
end, and transition pieces may use a one-sixteenth cardinal lattice.

Diagonal geometry must not be forced onto that cardinal lattice: a 45-degree
rectangle contains `1 / sqrt(2)` world-space coordinates. A diagonal work
frame instead stores integer `along` and `across` coordinates relative to a
stable anchor. Rendering converts that frame to world coordinates only at the
boundary with mesh construction.

The first freely placed diagonal establishes a quantized work frame. Later
diagonal pieces snap to that frame's course pitch and mortar spacing. This
permits useful free placement without allowing an unbounded set of floating
point poses.

## Canonical Arrangement Identity

Persisted and cache-key state must be sorted and independent of placement
order. A future compact pose contains:

- material palette index;
- kind and visual shape;
- cardinal or diagonal orientation;
- owner-relative integer anchor and local pose coordinates;
- canonical mortar and fill masks; and
- compact links for cross-cell reservations.

Random IDs, raw floating-point offsets, and string joint keys are not part of
the target identity. Existing saves remain readable; any new format must
migrate by decode-old/write-new on an ordinary later save.

## Interfaces and Fillers

One-sixteenth cardinal positions are a transition aid, not a guarantee that a
cardinal brick can meet every diagonal corner with a thin mortar seam. The
placement evaluator must measure the exact clearance, accept it only inside a
configured mortar band, then choose the smallest valid filler.

Half-bricks and wedges are the preferred bridge pieces. Rammed earth fills
only the remaining valid closed patches, represented as canonical rectangles
or masks at the chosen local resolution. At a vanilla-block boundary, the
occupancy and retention interface must align exactly to the cardinal face
lattice; a diagonal overhang never silently creates a vanilla-solid face.

## Meshing and Collision

Reduce quads only when adjacent faces have identical material, repeating UV
mapping, lighting metadata, render pass, and mortar state. Cardinal faces use
greedy rectangles. Diagonal faces use the same rule in their local rotated
frame; texture coordinates repeat rather than stretch across a merged strip.

Visual geometry is exact. Vintage Story collision remains an axis-aligned,
conservative approximation generated separately from the visual mesh. Its
selection representation should use greedily merged visible boxes so pointing
at masonry does not outline every individual quarter cell.

## Resource Policy

- Immutable derived meshes, collision unions, and transformed components use
  byte-budgeted LRU caches keyed by canonical arrangement identity.
- Freeze scheduling remains global and bounded; it must coalesce stale
  deadlines rather than create per-entity tick work.
- Frozen sidecar writes are batched/debounced per chunk before automatic
  static compaction is enabled outside profiling.
- Live edits may use coalesced deltas; full packed snapshots remain for
  chunk load and resynchronization.
- CPU mesh-buffer estimates are not VRAM measurements. Direct GPU evidence
  requires an in-process platform probe or external GPU telemetry correlated
  with the client capture markers.

## Evidence Commands

The server-only matrix is designed for unattended use:

```text
bbbprofile runtimereset
bbbprofile servermatrix 128 8
bbbprofile runtime
```

It writes live and compacted phase reports, emits client capture markers when
clients are attached, and cleans its own generated cells. Client-side markers
are available through `.bbbmeshprofile marker`.
