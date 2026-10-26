# Backlog

What is left to do across the app and its Home Assistant repositories
([Etsy-Shop-Manager-HA](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA) and
[Etsy-Shop-Manager-HA-Card](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA-Card)). This file is the one list:
every agent updates it in the same PR when it adds, finishes or drops work (see `AGENTS.md`). Finished work leaves this
list and is described in `docs/PLAN.md`.

## To do

### Address verification
Flag an order as soon as it arrives when its shipping address is one USPS wouldn't accept for a label, so the buyer can
be messaged before the order is made. Shipping labels and tracking stay out of scope. Design agreed with TopHATTwaffle
(2026-10-26):

- **Service:** the USPS Addresses API v3 (free; OAuth client credentials, tokens last 8 hours, about 60 calls an hour
  by default; needs a USPS Business Portal account, a developer app and the free Addresses API license signed through
  the portal). TopHATTwaffle sets the account up. Etsy's API has no address check of its own. Calls go through the job
  queue, a 429 throws `RetryLaterException`, and a cached token is reused until it is close to expiry (token calls
  count against the limit).
- **Settings page of its own** (Admin): an on/off toggle (off = no checks and no alerts shown), the USPS consumer key
  and secret (encrypted with Data Protection like the Etsy tokens), step-by-step instructions for creating the USPS
  account, app and license, and a **Test** button that checks a known address and shows whether the credentials,
  license and connection work. Staging never calls USPS.
- **When:** a job checks each new order (`OrderReceived`) and checks again whenever the address it ships to changes
  (Etsy's address on a poll, or a local address override). Turning the feature on checks open, unshipped orders once.
  Only addresses in the US are checked, including Puerto Rico, US territories and military (APO/FPO/DPO) addresses;
  other countries are skipped and show nothing.
- **What is flagged:** an alert for *address not found* (no match or DPV "N"), *apartment or unit missing* (DPV "D") and
  *unit not found* (DPV "S"). When USPS changes something that matters (street, city or 5-digit ZIP) the order gets
  a softer "USPS suggests …" note. Tidying alone (ZIP+4, abbreviations, capitals) shows nothing.
- **Storing results:** per order, the outcome, a hash of the address that was checked (a changed address means a
  recheck), when it was checked, USPS's suggested address and its raw reply, and whether it was dismissed. Local
  address overrides are free text lines, so checking them needs parsing them back into street, unit, city, state and
  ZIP (or giving the override dialog separate fields for US addresses); to settle when building.
- **App:** an alert on the order card (and the dashboard's open orders) with two buttons: **Use USPS's version** (saves
  USPS's address as the order's address override, which rechecks it) and **Address is fine** (dismisses the alert for
  that address; a later change of address checks again).
- **Home Assistant:** new keys in the HA payload (added, nothing changed or removed): each order's check result and
  message, and a list of open orders with an address alert. The card shows a blurb naming the orders with an address
  problem, and an alert on each such order card styled like the app's; the alert's CSS comes from the app in the
  payload, as the order colours do, so the two look the same. Needs a release of the integration (if the new
  attributes don't already pass through) and of the card.

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
