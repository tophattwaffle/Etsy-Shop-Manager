# Backlog

What is left to do across the app and its Home Assistant repositories
([Etsy-Shop-Manager-HA](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA) and
[Etsy-Shop-Manager-HA-Card](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA-Card)). This file is the one list:
every agent updates it in the same PR when it adds, finishes or drops work (see `AGENTS.md`). Finished work leaves this
list and is described in `docs/PLAN.md`.

## To do

### Personalization verification
Rules per listing that check the buyer's personalization and flag orders that don't match, for example exactly 2
colours when the listing asks for 2, or at least 1 switch type on clicker fidgets. Design agreed with TopHATTwaffle
(2026-10-26):

- **Rule types** (buyers write free text such as "1 mint 1 pink" or "two linear, two tactile", so regular expressions
  alone would be brittle):
  - *Colour count:* the text names between a minimum and a maximum number of colours, counted with
    `OrderColorEngine.ColorsIn` (a multi-colour filament counts once), so it agrees with the colour boxes.
  - *Must mention one of:* a word list with synonyms (for example linear / tactile / clicky).
  - *Not empty:* the personalization was filled in.
  - *Regular expression:* for anything the others can't express.
- Counts scale with the item's quantity (a rule of 2 colours on a line of 2 units expects 4); may be adjusted later.
- "Random" (as `OrderColorEngine.IsRandom` recognises it) always passes.
- Rules are named and shared like templates: one rule links to many listings, and a listing can have several.
- **When:** checked locally (no outside service) when an order arrives and again whenever its personalization is
  changed through `OrderOverrides`; a rule or link change rechecks open orders.
- **Shows** like the address alert: an alert on the order card naming the failed rule, a **Looks fine** button that
  dismisses it for that text, the same HA payload keys, card blurb and card alert, and an on/off toggle on its own
  settings page (where rules are also managed).

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
