# User stories: Buyer, Seller and Admin views

Each story maps to requirement IDs in [requirements.md](requirements.md), which governs.

**Roles** (requirements §5.2): Buyer and Seller are capabilities of one ordinary account. Admin is a separate, seeded-only role.

## 1. Buyer

| ID | Story | Requirements | Notes |
|---|---|---|---|
| US-B01 | As a buyer, I want to browse active auctions so that I can find something to bid on. | UI-01, UI-03, API-03 | Paginated; deterministic order |
| US-B02 | As a buyer, I want to search by title keyword, filter by status and sort by ending soonest so that I can find relevant auctions quickly. | UI-03 | "Ending soon" means within 1 hour, configurable |
| US-B03 | As a buyer, I want an auction's detail page to show the item, current price, minimum next bid, time remaining and number of bids so that I can decide what to bid. | UI-01, COR-06 (Should) | Up to 4 images (UI-05). The countdown uses server time, not the browser clock |
| US-B04 | As a buyer, I want to place a bid and immediately see whether it was accepted, and if not, exactly why, so that I'm never unsure where I stand. | UI-01, DOM-01..03, COR-03, API-03 | Rejection reasons: below minimum, auction not active, own auction, over cap, suspended |
| US-B05 | As a buyer, I want the price and history to update live while I'm watching so that I don't have to reload. | RT-01, RT-02, INT-02 | |
| US-B06 | As a buyer, I want to know my bid's outcome even if my connection drops mid-bid so that I don't double-bid or assume wrongly. | API-02, UI-02 (both Should), COR-03 | The idempotency key makes a retry safe even without the Should items |
| US-B07 | As a buyer, I want a "My bids" list showing whether I'm leading, outbid, won or lost so that I can track everything I'm in. | UI-01 | |
| US-B08 | As a buyer, I want to be notified when I'm outbid, and when an auction I bid on ends (won/lost), so that I can react. | NOT-01, UI-01 | Notification bell in the shared header |
| US-B09 | As a buyer, I want to see every closed auction's outcome and final price, but who won only for auctions I sold or won, and to be notified only about auctions I took part in. | NOT-01, §5.2 Closing/Visibility, §7 | Public history uses anonymous bidder labels |
| US-B10 | As a buyer, I want to be told when an auction I bid on was suspended or cancelled, and why, so that I'm not left guessing. | NOT-01, ADM-01 | The admin's reason is shown to the seller and bidders |
| US-B11 | As a buyer, I want to sign in and out, and have the app work on my phone, so that I can bid anywhere. | SEC-01, UI-01 (360 px) | |
| US-B12 | As a new visitor, I want to create an account with just my name and email, and sign in later with that email, so that trying the demo takes seconds. | AUTH-01 | |
| US-B13 | As a user, I want signing in somewhere new to sign me out everywhere else, and to be told why, so that my account is only ever in use in one place. | AUTH-02 | |
| US-S10 | As a seller, I want to choose a floor type (hidden reserve, published minimum, or no reserve) and how bids are entered (buyer max bid or fixed increments), so that the auction runs the way I want. | BID-01, §5.2 Bidding/Reserve | |
| US-B14 | As a buyer on a max-bid auction, I want to set the most I'll pay and have the system bid for me only as much as needed, without anyone seeing my maximum. | BID-01, §7 | |
| US-B15 | As a buyer, I want to see whether the reserve is met (never its amount), and to be told if an auction I bid on closed with the reserve not met. | BID-01, NOT-01 | |
| US-A10 | As an admin, I want to see the country/region each user last signed in from, so that I can spot suspicious sign-ins. | LOC-01 (Should) | Country and region only |

## 2. Seller

| ID | Story | Requirements | Notes |
|---|---|---|---|
| US-S01 | As a seller, I want to create a draft with a title, description, opening price, bid increment, start and end time so that I can prepare a listing. | UI-01, DOM-01, §5.2 Drafts | Saved on the server, visible only to me. The seller sets the increment (§5.2 Increment) |
| US-S02 | As a seller, I want to edit or delete my draft before publishing, and find it again after a reload, so that mistakes don't go live and I don't lose work. | UI-01, §5.2 Drafts, §7 | Server-side; available from any device; hard delete |
| US-S11 | As a seller, I want to upload up to 4 photos of my item and pick a cover, so that buyers can see what they're bidding on. | UI-05 | |
| US-S03 | As a seller, I want to publish a draft so that it opens at its start time, or immediately, and its terms are frozen. | DOM-03, LIF-01 | |
| US-S04 | As a seller, I want a list of my auctions with status, current price and bid count so that I can manage them. | UI-01 | |
| US-S05 | As a seller, I want to watch bids arrive live on my auction so that I see demand as it happens. | RT-01, RT-02 | |
| US-S06 | As a seller, I want to cancel my auction before anyone bids, and be told clearly why I can't afterwards. | DOM-03, §7 | |
| US-S07 | As a seller, I want to be notified when my auction ends, with the winner's display name and final price (or that it had no bids), so that I know the result. | NOT-01 | |
| US-S08 | As a seller, I want to be told when an admin suspends or cancels my auction, and why. | NOT-01, ADM-01 | |
| US-S09 | As a user, I want to switch between buying and selling with one account, without signing in again. | UI-01, §5.2 Roles | One app with Buy and Sell areas |

## 3. Admin

| ID | Story | Requirements | Notes |
|---|---|---|---|
| US-A01 | As an admin, I want a live dashboard of active auctions, recent bids and online users so that I can see what the platform is doing. | UI-04, RT-01 | |
| US-A02 | As an admin, I want system health (API, engine, PostgreSQL, Redis, outbox backlog) so that I can spot a degraded dependency. | UI-04, REL-02, HOST-01 | |
| US-A03 | As an admin, I want to find any published auction and suspend, resume or cancel it with a reason so that I can moderate. | ADM-01, UI-04, §5.2 Suspension | Admin auction list |
| US-A04 | As an admin, I want to find a user and disable or re-enable them so that I can stop abuse. | ADM-01, UI-04 | Admin user list |
| US-A05 | As an admin, I want to see the audit log of admin actions so that moderation is accountable. | ADM-01, ADM-03 (Should) | Read-only view |
| US-A06 | As an admin, I want to start and stop a simulator profile so that I can show the platform under load. | ADM-02 | First cut candidate |
| US-A07 | As an admin, I want to see who bid (display name and account ID) so that I can investigate. | §7 | Admins see display name and account ID, never email |
| US-A08 | As an admin, I want live latency KPIs (API, bid, engine call, publish lag at p50/p95) so that I can see how fast the system responds under load. | UI-04 | |
| US-A09 | As an admin, I only moderate: I can't bid or sell, so moderation stays separate from trading. | §5.2 Roles, §7, SEC-04 | |
