# Backlog

What is left to do across the app and its Home Assistant repositories
([Etsy-Shop-Manager-HA](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA) and
[Etsy-Shop-Manager-HA-Card](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA-Card)). This file is the one list:
every agent updates it in the same PR when it adds, finishes or drops work (see `AGENTS.md`). Finished work leaves this
list and is described in `docs/PLAN.md`.

## To do

*(No open items at present.)*

## Unverified until real pushes
Behaviour that can only be confirmed by TopHATTwaffle's own pushes to the real shops (the app is never tested against
them). The code makes a reasonable guess for each; correct it here and in the code once a push shows the answer.

- **New colour in a variation dropdown:** where Etsy puts a value added from a shared variation list in the buyer's
  dropdown (the app sends it at the end).
- **Replaced photo template image:** whether the new upload lands in the template's position from the end after the
  old copy is deleted, or Etsy places it last.
- **Variation added from a list with shared stock:** whether Etsy accepts the inventory when a whole new variation is
  added from a list and stock stays shared (no quantity on property).
- **Second listing video:** whether an API upload lands after the existing video (Etsy shows videos in upload order).

## Known limits (not planned)
- Styles and featured rank can't be changed on an existing listing: Etsy's API has no write for them.
- Typed category attributes without a value list (Height, Width, Depth) can be removed but not set.
- New listings and clones are created as Etsy drafts only; the app never publishes them.

## Dropped
- **Shipping labels and tracking** (buying labels, adding tracking): out of scope for good.
