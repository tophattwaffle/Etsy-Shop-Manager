# Backlog

What is left to do across the app and its Home Assistant repositories
([Etsy-Shop-Manager-HA](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA) and
[Etsy-Shop-Manager-HA-Card](https://github.com/TopHATTwaffle/Etsy-Shop-Manager-HA-Card)). This file is the one list:
every agent updates it in the same PR when it adds, finishes or drops work (see `AGENTS.md`). Finished work leaves this
list and is described in `docs/PLAN.md`.

## To do

### Shipping country profiles
Reusable profiles of destination countries that can be applied to a shop's Etsy shipping profile, so controlling where a
shop ships doesn't mean clicking 200+ checkboxes per shop on Etsy.com. Design agreed with TopHATTwaffle (2026-10-30):

- **How Etsy models it:** destination countries live on a shop's `ShippingProfile` (`shipping_profile_destinations`),
  and every active, draft and inactive listing in each shop uses that shop's primary `USPS` calculated shipping profile.
  Updating a shop's shipping profile destinations immediately updates where all listings using that profile ship.
- **Country profiles (`ShippingCountryProfile`):** named allowlists of ISO alpha-2 country codes (`NOCASE` unique name,
  JSON list of codes), edited through **region groups** (North America, European Union & EEA / GPSR, Non-EU Europe,
  Central America & Caribbean, South America, Middle East & North Africa, Asia, Oceania, Sub-Saharan Africa) with
  tri-state group toggles, individual country checkboxes, search, quick actions (`Select all`, `Clear all`, `US only`,
  `Select all except EU (GPSR)`) and **Copy from shop** to seed from a shop's current imported `ShippingProfile`
  destinations. Covers all 226+ Etsy country codes (including Etsy's `AN`, `IC` and `KV`), with any unknown code Etsy
  returns shown under **Other**.
- **UI (`/shipping-profiles`):** a dedicated **Shipping profiles** page in the sidebar next to **Processing profiles**,
  with a country profiles panel (list, create/edit with `<SaveButtons>` and `SavedValues<T>`, delete) and a shop
  shipping profiles & apply panel showing each shop's Etsy shipping profiles, current destination counts and listing
  counts.
- **Preview → queued job (`SyncShippingDestinationsJob`, `etsy.shipping-profile.sync-destinations`):**
  - Comparing a country profile against a shop's Etsy shipping profile shows `ToAdd`, `ToRemove` and `UnchangedCount`
    in a sortable diff table before anything is sent.
  - **Guardrails:** blocks applying an empty country list (Etsy refuses deleting the final destination on a shipping
    profile), warns if the shipping profile's origin country (`OriginCountryIso`, e.g. `US`) is being removed, and
    warns when adding countries to a `calculated` shipping profile.
  - **Execution:** re-reads the shop's shipping profiles from Etsy for fresh `shipping_profile_destination_id`s, runs
    additions (`POST /v3/application/shops/{shop_id}/shipping-profiles/{shipping_profile_id}/destinations`, `NoRetry`)
    before removals (`DELETE .../destinations/{destination_id}`), refreshes the shop's cached lookups
    (`ImportLookupsAsync`), records every call in `EtsyWriteLogs` (`shops_w` scope, already granted), writes an audit
    entry and reports the outcome on the job.

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
- **Adding a destination country to a calculated shipping profile:** on a `calculated` shipping profile (`USPS`),
  Etsy's `GET` returns `primary_cost: 0`, `secondary_cost: 0` and `null` carrier/delivery days on each destination,
  whereas `createShopShippingProfileDestination` (`POST .../destinations`) documents cost and carrier or delivery days
  as required — whether Etsy accepts adding a destination to a calculated profile and treats it as calculated (not free
  fixed shipping) needs a one-country test push.

## Known limits (not planned)
- Styles and featured rank can't be changed on an existing listing: Etsy's API has no write for them.
- Typed category attributes without a value list (Height, Width, Depth) can be removed but not set.
- New listings and clones are created as Etsy drafts only; the app never publishes them.

## Dropped
- **Shipping labels and tracking** (buying labels, adding tracking): out of scope for good.
