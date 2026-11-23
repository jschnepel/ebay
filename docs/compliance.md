# Compliance: auction law, marketplace rules and industry norms

This page lists the laws, regulations and industry norms that apply to an eBay-style online auction platform. For each one it says what this demo does about it, and what we would change if this were a real, production marketplace.

> **Not legal advice.** This is an engineering summary of desk research done in November 2026. Primary sources are linked inline throughout this page. Where the research could not verify a point, this page says so. Any real launch needs review by counsel.

## Summary

- **Money, real goods and size trigger almost every heavy regime.** That includes INFORM, marketplace-facilitator sales tax, money-transmitter rules, sanctions, AML, auctioneer licensing, the EU withdrawal right, DSA trader traceability, GPSR, CCPA and accessibility law. This demo has no payments, no real goods, no fees and no email, so it sits outside nearly all of them.
- **Auction law is the exception, so the demo builds it anyway.** The sale-of-goods defaults (UCC §2-328, UK Sale of Goods Act s.57) map onto engine invariants: with reserve unless explicitly absolute, a reserve can go unmet, no seller bidding, and a defined moment of completion. The demo implements these as a real platform must, because they are the domain-competence signal.
- **The real legal risk is a mismatch between disclosed rules and running code.** Courts and regulators look at whether the displayed price, history and reserve status are real, and whether bots pose as people. The demo labels simulated bidders and keeps an append-only bid history.
- **Two features need careful framing.** Email-only sign-in falls below NIST guidance, and "one active session" runs against current OWASP advice. Both are deliberate demo trade-offs, implemented in their most defensible form.

## How to read the tables

Each area has a short statement of the rule, then a table with three columns of meaning:

- **Real platform:** **Required** (legally required, or needed to avoid clear legal exposure, once the trigger is met), **Good practice** (expected by standards or industry, not mandated), or **N/A** (no legal hook without money, goods or the trigger).
- **This demo:** what the system does today, with requirement IDs from [requirements.md](requirements.md).
- **Production change:** the concrete change for a real marketplace.

## 1. Auction law

**The rule.** UCC §2-328 and its UK twin, Sale of Goods Act 1979 s.57, set default rules ([Cornell LII](https://www.law.cornell.edu/ucc/2/2-328); [legislation.gov.uk](https://www.legislation.gov.uk/ukpga/1979/54/section/57)):

- **Each lot is a separate sale** (§2-328(1)).
- **An auction is with reserve unless goods are put up without reserve "in explicit terms"** (§2-328(3)). With reserve, goods may be withdrawn until completion. Without reserve, a lot cannot be withdrawn once a bid is made.
- **Completion is "by the fall of the hammer or in other customary manner"** (§2-328(2)). The research found no US appellate decision applying this to an online countdown. Commentators treat a published, server-decided close as the defensible answer ([Brandly, Sept 2026](https://mikebrandlyauctioneer.wordpress.com/2026/09/16/the-clock-is-the-cadence/); [Insta-Auction](https://insta-auction.com/online-auction-laws/), uncited).
- **A bidder may retract until completion, and retraction does not revive the earlier bid** (§2-328(3); s.57(2)). Commentators argue posted terms may vary this default under UCC §1-302 ([Brandly, 2025](https://mikebrandlyauctioneer.wordpress.com/2025/04/27/the-ucc-%C2%A7-2-328-the-ucc-%C2%A7-1-302/)). The research found no UK authority on whether terms can override s.57(2).
- **Undisclosed seller bidding lets the buyer avoid the sale** or take the goods at the last good-faith bid (§2-328(4)). The UK treats such a sale as fraudulent (s.57(4)–(5)).
- **"Absolute auction" can be a statutory promise.** Florida defines it as no minimum and no limit on sale other than to the highest bidder ([Fla. Stat. §468.382](https://www.flsenate.gov/Laws/Statutes/2024/468.382)).

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| Each lot a separate sale | Good practice | Each auction has its own lifecycle and state machine (LIF-01) | None |
| With reserve by default | Required | Floor type defaults to `RESERVE`; `ABSOLUTE` only by explicit seller choice; frozen at publish (BID-01) | Show the floor type in plain words on the listing and on a terms page |
| No withdrawal of an absolute lot after a bid | Required if "no reserve" is offered | Owner may cancel only before the first accepted bid, for every floor type. This is stricter than the with-reserve default (DOM-03). Admin may cancel a Scheduled, Active or Suspended auction with a recorded, audited reason (ADM-01) | Limit admin cancellation of a bid-on `ABSOLUTE` auction to published grounds (for example fraud or a prohibited item), and state those grounds in the terms |
| Reserve may go unmet | Required if reserves are offered | Hidden reserve; public "reserve met / not met" badge, never the amount; ends "Closed, reserve not met" with no sale | Same. If reserve edits are ever allowed after publish, gate them on bid state and time left, and audit them (eBay's rule: no bids or reserve unmet, and at least 12 h left) |
| Defined completion | Required (disclosed and consistent) | Hard close at a fixed end time, judged by database time under the auction row lock (DOM-02). Client clocks never decide | Publish the closing rule on a terms page |
| Last-instant ties | Good practice | Deterministic: the earliest server-accepted bid wins | Same; document it on the terms page |
| Retraction | Required to disclose and apply consistently | No bid retraction (Won't) | State "bids cannot be retracted" in the terms, or adopt a narrow policy (for example typo bids only) with price recompute and an immutable log. Counsel to confirm for UK users |
| No seller bidding | Required | Self-bidding blocked (DOM-03, §7 of the spec); append-only history with a per-auction sequence (COR-05) | Extend the block to linked accounts; support recomputing an outcome with flagged bids excluded |
| Terms shown before bidding | Required in effect | Terms frozen at publish (DOM-03) | Terms page covering floor type, proxy rule, increments, closing and retraction; store the terms version with each bid |

**Bidding modes.** Industry glossaries separate the floor from the way bids are entered ([NAR](https://www.nar.realtor/auction/types-of-auctions); [MSAA/NAA glossary](https://msaa.org/?p=1480)). The demo follows that split. The seller picks a floor type (`RESERVE`, `MINIMUM_BID` or `ABSOLUTE`) and a bid entry (`PROXY` or `FIXED_INCREMENT`), and both freeze at publish. With `PROXY`, the bidder sets a private maximum and the engine bids for them up to it. With `FIXED_INCREMENT`, each bid is the current price plus the increment. The seller sets the increment (default $1.00, minimum $0.01).

## 2. Consumer protection, fake bidding and shill bidding

**The rule.** No US statute requires a particular increment, soft close or sniping rule. What gets enforced is a gap between what a platform shows and what is true:

- **Every displayed number must be real.** In *Block v. eBay* (9th Cir. 2014), plaintiffs sued over eBay's automatic bidding and lost only because the user agreement made no promise ([Goldman](https://blog.ericgoldman.org/archives/2014/04/ninth-circuit-rejects-plaintiffs-bad-misreadings-of-ebays-user-agreement-block-v-ebay.htm)).
- **Bots posing as rivals are deceptive.** Washington's Attorney General barred a penny-auction operator that used "bidbots" ([WA AG](https://www.atg.wa.gov/news/news-releases/attack-bidbots)). DOJ treated EBlock's "deceitful bidding practices" as wire fraud ([DOJ](https://www.justice.gov/atr/case/us-v-eblock-corporation)).
- **Shill bidding is prosecuted as mail and wire fraud.** Mastro Auctions' former owner got 20 months in federal prison ([DOJ N.D. Ill.](https://www.justice.gov/usao-ndil/pr/former-owner-mastro-auctions-sentenced-20-months-federal-prison-shill-bidding-scam)). Early eBay shills were caught by IP analysis ([ATG 2002](https://www.antiquestradegazette.com:443/news/2002/us-court-to-hear-13m-web-bidding-fraud-case/)). eBay bans bidding by anyone connected to the seller ([eBay shill policy](https://www.ebay.com/help/policies/selling-policies/selling-practices-policy/shill-bidding-policy?id=4353)). Detection features are documented in research ([UCI dataset](https://archive.ics.uci.edu/dataset/562/shill+bidding+dataset); [Kagan et al. 2022](https://arxiv.org/abs/2204.02057)).
- **UK bid rings are a crime** for dealers who pay others not to bid ([Auctions (Bidding Agreements) Act 1927](https://www.legislation.gov.uk/ukpga/Geo5/17-18/12/section/1)).
- **Fake reviews and hidden fees are banned.** The FTC rule (16 CFR Part 465) carries up to $53,088 per violation ([Morgan Lewis](https://www.morganlewis.com/ru/pubs/2024/08/ftc-issues-final-rule-on-consumer-reviews-and-testimonials); [Federal Register](https://www.federalregister.gov/documents/2025/01/17/2025-01361/adjustments-to-civil-penalty-amounts)). Since 6 April 2025 the UK DMCC Act makes fake reviews and drip pricing automatically unfair ([Osborne Clarke](https://www.osborneclarke.com/insights/cma-publishes-final-unfair-commercial-practices-guidance-under-uk-dmcca-what-your-business)).
- **Pay-per-bid fees risk lottery law.** *Mendelsohn v. BidCactus* let illegal-lottery claims proceed ([Bloomberg Law](https://news.bloomberglaw.com/tech-and-telecom-law/penny-auction-website-may-be-liable-for-deceptive-trade-practices-illegal-lottery)).

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| No fake bids or bots | Required | An auction that any simulated bidder has bid on says so on its page, for every visitor; in the admin-only list of bids their labels carry "(simulated)" (requirements §5.2 Visibility). The simulator uses the ordinary public login with no privileged path (SIM-05) | Remove the simulator from production, or keep it to staging |
| Displayed price, history and reserve status are real | Required | The engine and PostgreSQL are authoritative; the UI re-fetches committed state (RT-01, RT-02) | None |
| Shill detection | Good practice; evidence retention effectively required | Self-bid block; append-only history; every command is idempotent (COR-03). No linked-account detection or scoring | Record IP hash, device and session per bid; flag linked accounts; nightly 0–10 risk score queued for review; warn → restrict → suspend ladder |
| UK bid rings | Good practice | Nothing specific | Terms ban; co-bidding anomaly flag |
| Fake reviews | Required if reviews exist | No reviews or feedback | If added: only after a completed transaction; never suppress negatives; label any seed reviews |
| All-in price | Required (UK) | No fees or buyer's premium; the bid is the price | If fees are added: show bid + fees = total on the bid confirmation |
| Penny-auction mechanics | Avoid | Bidding is free; no bid credits | Keep it that way |

## 3. US marketplace regimes

**The rule.** Each regime below has a trigger tied to money, goods or volume:

- **INFORM Consumers Act:** applies once a third-party seller makes 200+ sales of new or unused products and $5,000+ in 12 months, counting only sales the marketplace processes. Seller identity must be shown at $20,000. The first enforcement cost Temu $2M ([FTC](https://www.ftc.gov/business-guidance/resources/informing-businesses-about-inform-consumers-act); [DOJ](https://www.justice.gov/opa/pr/temu-agrees-2m-civil-penalty-and-injunction-alleged-violations-inform-consumers-act)).
- **Marketplace-facilitator sales tax:** applies to platforms that process payment, typically above $100,000 or 200 transactions per state ($500,000 in Texas). Tennessee ruled that an auction platform with no access to funds was not a facilitator ([SST](https://streamlinedsalestax.org/for-businesses/marketplace-facilitator); [Bloomberg Tax](https://news.bloomberglaw.com/daily-tax-report/tennessee-dor-rules-digital-auction-platform-not-a-marketplace-facilitator)).
- **Money transmission (BSA):** depends on how funds flow ([FinCEN FIN-2014-R006](https://www.fincen.gov/sites/default/files/administrative_ruling/FIN-2014-R006.pdf)). The proposed antiquities-dealer rule was withdrawn on 16 April 2025 ([Federal Register](https://www.federalregister.gov/documents/2025/09/22/2025-18332/semiannual-agenda)).
- **OFAC sanctions:** no threshold and strict liability. Amazon paid $134,523 after its screening missed spelling variants of sanctioned regions ([OFAC](https://ofac.treasury.gov/recent-actions/20200708)).
- **Prohibited items:** selling recalled products is illegal ([CPSC](https://www.cpsc.gov/Business--Manufacturing/Business-Education/ResaleThrift-Stores-Information-Center/Stop-Online-Sale-of-Recalled-Products)). Firearms must go to an FFL ([ATF](https://www.atf.gov/resource-center/questions-and-answers?page=23)). African elephant ivory trade is largely banned ([FWS](https://fws.gov/policy/library/2016/2016-13173.html)). Vapes cannot be mailed to consumers ([ATF](https://www.atf.gov/alcohol-tobacco/vapes-and-e-cigarettes)). eBay limits alcohol to licensed wine sellers ([eBay](https://www.ebay.com/help/Policy/-/Alcohol_policy?id=4274)).
- **Auctioneer licensing:** state by state. Texas exempts internet auctions ([Tex. Occ. Code §1802.002](https://texas.public.law/statutes/tex._occ._code_section_1802.002)). Pennsylvania reaches online auctions wherever the server is ([PA Act](https://www.pa.gov/content/dam/copapwp-pagov/en/dos/department-and-offices/bpoa/auctioneer/Auctioneer-Law-Act.pdf)). Tennessee exempts "timed listings" but licenses "extended-time" internet auctions, upheld by the Sixth Circuit in 2025 ([Casemine](https://www.casemine.com/commentary/us/mclemore-v.-gumucio:-sixth-circuit-reaffirms-the-conduct–speech-divide—auctioneer-licensing-schemes-trigger-only-rational-basis-review/view)). California's AG noted that bid recognition is "customarily performed by proprietary software" ([Cal. AG Op. 04-1001](https://oag.ca.gov/system/files/opinions/pdfs/04-1001.pdf)).
- **CAN-SPAM:** outbid and won emails are transactional; marketing email needs an opt-out honoured within 10 business days ([FTC](https://www.ftc.gov/business-guidance/resources/can-spam-act-compliance-guide-business)).

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| INFORM | Required at the threshold | N/A: no sales are processed | Seller-volume counter; 10-day collect and verify; annual re-certification; suspend on failure; disclosure at $20k; report link and phone on listings |
| Sales tax | Required if processing payment, per state | N/A: no payments | Per-state nexus tracker; tax engine on hammer price plus taxable fees; remittance |
| Money transmission | Required: never hold buyer funds | N/A: no funds (A4, SIM-12) | Route all funds through a licensed payment processor |
| OFAC | Required | N/A: no funds, no shipments | Fuzzy screening against sanctions lists at sign-up and on list updates; geo-block sanctioned regions by IP, billing and shipping address |
| Prohibited items | Required | Admin can suspend or cancel an auction with a reason (ADM-01); uploaded images are type- and size-checked, served with `nosniff`, and deleted by the nightly reset. No category blocklist or user report button | Listing-policy engine: category bans, keyword filters, recall matching, takedown queue |
| Auctioneer licensing | Required to fit venue exemptions | N/A: a labelled demo, not holding itself out as an auctioneer. Hard close, so the Tennessee "extended-time" question does not arise | Venue language in terms; seller-set terms; never take title or possession; capture seller, item and buyer state |
| CAN-SPAM | Required once email is sent | No email is sent; notifications are in-app (NOT-01) | Separate transactional and marketing streams; suppression list |

## 4. EU and UK

**The rule.** These apply to a real platform serving EU or UK consumers:

- **14-day withdrawal right.** Online auction platforms "should not be considered as a public auction" (CRD recital 24), so trader sellers owe EU consumers the withdrawal right ([CRD recitals](https://www.legislation.gov.uk/eudr/2011/83/introduction)). The UK copies the in-person definition ([CCR reg 5](https://www.legislation.gov.uk/uksi/2013/3134/regulation/5)).
- **Seller status and ranking.** CRD Art 6a requires marketplaces to show whether a seller is a trader, warn that consumer rights don't apply to private sellers, and explain ranking ([Directive 2019/2161 Art 4](https://www.legislation.gov.uk/eudr/2019/2161/article/4)).
- **DSA.** Art 16 notice-and-action applies to every hosting service, with no verified micro exemption. Marketplace duties (Arts 30–32) and the Art 25 dark-pattern ban exempt micro and small enterprises ([Art 29](https://dsa-ipj.pages.dev/article/29); [Art 19](https://dsa-ipj.pages.dev/article/19)).
- **GPSR.** Marketplaces must act on authority orders within 2 working days and on safety notices within 3 ([Mason Hayes & Curran](https://www.mhc.ie/latest/insights/product-safety-for-providers-of-online-marketplaces)).
- **AML.** EU AMLR covers cultural-goods intermediaries at €10,000+ from 10 July 2027 ([DLA Piper](https://dlapiper.com/insights/publications/2024/12/the-new-anti-money-laundering-rules-what-you-need-to-know)). UK MLR covers art-market participants at £10,000+ ([MLR reg 14](https://www.legislation.gov.uk/uksi/2017/692/regulation/14)).
- **UK Online Safety Act.** Illegal-content duties have applied since 17 March 2025 to user-to-user services with UK links ([Kennedys](https://kennedyslaw.com/en/thought-leadership/article/2025/uk-online-safety-act-how-to-comply-as-illegal-harms-duties-take-effect)).

The demo concludes no contracts, does not target the EU or UK, and its operator is a micro enterprise, so these do not apply to it.

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| 14-day withdrawal (trader sellers) | Required for EU/UK consumers | N/A: no contracts | Business/private seller flag; returns-policy field; cancellation flow |
| Art 6a disclosures | Required (EU) | N/A | Seller-type badge on listings; "how results are ranked" page |
| DSA Art 16–17 notice-and-action | Required | N/A (likely outside territorial scope); admin moderation with recorded reasons exists (ADM-01, ADM-03) | "Report listing/user" with reason codes; ticket; statement of reasons; moderation log |
| DSA Arts 30–31, transparency reports | Required unless micro/small | N/A; the demo collects no ID or payment data | Trader KYC with 6-month retention after the relationship ends; product-safety fields |
| GPSR | Required; no SME exemption found | N/A: no real products | Safety Gate registration; 2/3-working-day queues; recall notices to past buyers |
| AMLR / UK MLR | Required if intermediating at the threshold | N/A: no transaction value | Art category flag; linked-transaction totals; KYC gate; suspicious-activity reporting |
| UK Online Safety Act | Required with UK links | Probably out of scope (unverified) | Illegal-content risk assessment; reporting and takedown; records |

## 5. Privacy and location

**The rule.**

- **Coverage.** CCPA covers only for-profit businesses above $26.625M revenue or 100,000 consumers ([§1798.140(d)](https://codes.findlaw.com/ca/civil-code/civ-sect-1798-140/); [CPPA](https://www.cppa.ca.gov/regulations/cpi_adjustment.html)). GDPR reaches a US operator only if it offers services to, or monitors, people in the EU; being reachable is not enough ([Art 3](https://gdpr-info.eu/art-3-gdpr/); [Recital 23](https://gdpr-info.eu/recitals/no-23/)).
- **Only precise location is sensitive.** US state laws treat location within 1,850 ft (California, Colorado) or 1,750 ft (Virginia, Texas, Connecticut) as sensitive ([§1798.140(w)](https://codes.findlaw.com/ca/civil-code/civ-sect-1798-140/); [Texas AG](https://www.texasattorneygeneral.gov/consumer-protection/file-consumer-complaint/consumer-privacy-rights/texas-data-privacy-and-security-act); [Mayer Brown](https://mayerbrown.com/en/insights/publications/2025/09/2025-mid-year-review-us-state-comprehensive-data-privacy-law-updates-part-1)). The research infers that city-level IP lookup is far coarser than these radii; no regulator guidance says so directly.
- **Browser geolocation can be precise.** It needs express permission and HTTPS, and the spec says to disclose the purpose and dispose of the data after the task ([W3C Geolocation](https://www.w3.org/TR/geolocation/)).
- **In the EU an IP address is personal data,** but logging it for security is a recognised legitimate interest ([Inside Privacy on *Breyer*](https://www.insideprivacy.com/international/cjeu-confirms-dynamic-ip-addresses-to-be-personal-data/)). The EDPB reads ePrivacy Art 5(3) as covering JavaScript that sends device data to a server ([EDPB 2/2023](https://edpb.europa.eu/system/files/2024-10/edpb_guidelines_202302_technical_scope_art_53_eprivacydirective_v2_en_0.pdf)).
- **Geofencing near health facilities is banned in Washington** ([RCW 19.373.080](https://app.leg.wa.gov/RCW/default.aspx?cite=19.373.080)).

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| Data minimisation | Required | Sign-up asks only for display name and email (AUTH-01). Emails are never shown to other users; admins see them in the user list, a user's page and the event log (§7 of the spec). The list of bids is admin-only and uses anonymous bidder labels; only the seller and winner see each other's display names | Same |
| Retention and deletion | Required | Nightly `reset-demo` deletes self-registered accounts and their data (HOST-01) | Retention schedule per data category; self-service account deletion |
| Privacy notice | Required | The sign-up form and README state the email-only trade-off. A one-page privacy notice is recommended by the research but not yet specified | Full notice (GDPR Art 13 / CCPA notice at collection) |
| Location tracking | Required (consent and notice) if precise | **Coarse only (LOC-01):** offline IP lookup to country/region at sign-in; salted IP hash, never the raw IP; purged after 30 days; shown only in the signed-out notice and the admin user list; no browser geolocation prompt; stated on the sign-up form | Keep coarse and security-only; documented legitimate-interest balancing for EU users; opt-in consent if precise location is ever used; no geofencing |

**Location in a real system: finer, but for operations, not people.** The demo keeps location to country/region because its only use is sign-in security. A real marketplace would narrow it further to answer an operational question: **which locations are seeing problems** (slow pages, failed bids, dropped live connections, sign-in failures, fraud clusters). The design keeps that useful without crossing into "precise geolocation":

- **Granularity:** city or metro area and network (ASN/ISP) from server-side IP lookup. That's enough to tell "users on one ISP in one metro are timing out" from "the whole region is slow". It stays far coarser than the 1,850 ft / 1,750 ft statutory radii, so it isn't sensitive data under the state laws above. That is an inference, so confirm it with counsel. Still no browser geolocation, and no street-level or coordinate data.
- **Attach it to telemetry, not to profiles.** Location goes onto request metrics and traces as low-cardinality labels: country, region, metro, ASN, plus client type. It powers per-location error rates and latency (p50/p95/p99), SignalR disconnect and reconnect rates, bid rejection reasons and sign-in failures. It extends the demo's latency KPIs (UI-04) with a location dimension. Field telemetry from real browsers (real-user monitoring) adds page-load and live-update lag per location.
- **Aggregate before anyone looks.** Dashboards show only groups above a minimum size (for example 20 or more distinct users per cell per window), so no single user can be singled out. Raw per-request location stays in short-lived logs (for example 30 days). Aggregates can be kept longer because they no longer identify anyone.
- **What it's used for:** spot regional outages and ISP or CDN routing problems; decide where to add edge capacity or a closer region; check that a deploy didn't degrade one market; and feed fraud signals (bursts of new accounts or bids from one network). Each use is written into the privacy notice, with a documented legitimate-interest assessment for EU users.
- **What it's never used for:** per-user location history, pricing or ranking by location, or anything shown to other users.

## 6. Account security and sessions

**The rule.**

- **Email is not an authenticator.** NIST SP 800-63B-4 says email "SHALL NOT be used for out-of-band authentication" ([NIST](https://pages.nist.gov/800-63-4/sp800-63b.html)). NIST is binding only on federal agencies; for this project it is a benchmark.
- **Blocking concurrent sessions is out of favour.** OWASP ASVS v4.0.3 says blocking them "is no longer appropriate" because the last sign-in, often the attacker's, wins. It recommends letting users view and end sessions ([OWASP ASVS V3](https://github.com/OWASP/ASVS/blob/v4.0.3/4.0/en/0x12-V3-Session-management.md)).
- **If single-session is chosen, do it openly.** OWASP's cheat sheet says to end the old session or ask the user, alert on concurrent logons, and show session details ([OWASP Session Management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html)). Session IDs should be opaque and high-entropy, renewed at login, invalidated server-side at logout, and kept out of localStorage.

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| Strong sign-in | Good practice; "reasonable security" in effect | Under `DEMO_MODE`, ordinary users sign in with email only. This is a deliberate demo trade-off, below NIST guidance, stated on the sign-up form and README. Email-only sign-in is off outside `DEMO_MODE` (AUTH-01) | Passkeys (WebAuthn) as the primary method. Any magic links: single-use, stored hashed, 10–15 minute expiry, rate-limited, generic "if that address exists" response |
| Admin accounts | Good practice | Always need a password; seeded only, never self-registered (AUTH-01) | Add MFA or passkeys for admins |
| One active session | Good practice if chosen | A new sign-in revokes older sessions server-side; their next request gets 401; an open page is told in-app it was signed out (AUTH-02). The engine holds proxy maximums, so a revoked session loses no bids | Add a new-sign-in alert through an independent channel (email or push) with browser and country; an active-sessions page. Reconsider allowing concurrent sessions with view-and-revoke, per ASVS |
| Session handling | Good practice | Cookie-based Identity sessions; `SameSite=Strict`, JSON-only unsafe requests and an `Origin` check instead of antiforgery tokens (SEC-01, ADR-0003) | Confirm `__Host-` prefix, `Secure`, `HttpOnly`, `SameSite`; idle and absolute timeouts with a warning before expiry |

## 7. Accessibility

**The rule.**

- **The law does not reach the demo.** ADA Title III has no web regulation. The DOJ Title II rule binds only state and local governments, with deadlines in April 2027/2028 ([Plante Moran](https://plantemoran.com/explore-our-thinking/insight/2026/04/doj-extends-ada-title-ii-web-accessibility-compliance-deadlines)). The European Accessibility Act exempts micro-enterprises providing services ([Accessible.org](https://accessible.org/eaa-ecommerce-services-requirements/)).
- **WCAG 2.2 AA is the usual accessibility baseline, and it has auction-specific content.** SC 2.2.1 exempts real-time auction deadlines from the adjustable-time rule ([W3C Understanding 2.2.1](https://www.w3.org/WAI/WCAG22/Understanding/timing-adjustable.html)). Session timeouts are not exempt. Status messages (SC 4.1.3), pausing live updates (2.2.2), confirming financial commitments (3.3.4), 24×24 px targets (2.5.8) and accessible authentication (3.3.8) all apply.

| Rule | Real platform | This demo | Production change |
|---|---|---|---|
| Accessibility law | Required in effect (US litigation risk; EAA above micro size) | N/A | Treat WCAG 2.2 AA as the bar |
| WCAG 2.2 AA | Good practice for the demo | Target: WCAG 2.2 AA basics; zero serious or critical axe violations; usable at 360 px (UI-01) | Manual and screen-reader audit. Check the auction items automated scans miss: a silent `role="timer"` countdown with polite milestone and "outbid" announcements; the absolute end time as text; a pause for live updates; a bid confirmation step; a warning before session expiry; never expiring a session mid-bid |

## 8. Industry norms

eBay's mechanics are platform terms, not law. The research found:

- **Proxy bidding:** eBay bids for you "only up to your limit" ([eBay Help](https://www.ebay.com/help/buying/auctions-bidding/automatic-bidding?id=4014)). The "second-highest maximum plus one increment" price and the earliest-bid tie rule come from community and academic sources, not an official eBay page ([eBay Community](https://community.ebay.com/t5/Selling/Same-amount-bid-by-2-bidders/m-p/33055698); [EconPort](https://econport.gsu.edu/content/handbook/auctions/usefulconcepts/hardsoftclose.html)).
- **Increments:** eBay's US table runs from $0.05 below $1 to $100 at $5,000 and up ([eBay Help](https://www.ebay.com/help/buying/auctions-bidding/automatic-bidding?id=4014)).
- **Reserve:** hidden amount, public met/not-met status, changes limited by bid state and a 12-hour rule ([eBay Help](https://www.ebay.com/help/Selling/-/Setting_a_reserve_price?id=4143)).
- **Retraction and seller cancellation:** limited to listed reasons, tighter in the last 12 hours ([retraction](https://www.ebay.com/help/buying/bidding/bidding-works?id=4013); [cancellation](https://www.ebay.com/help/selling/listings/selling-auctions/canceling-bid?id=4140)).
- **Closing:** eBay's Extended Bidding resets the clock to 60 seconds after a final-minute bid. It applied to all Australian auctions from 17 August 2026 and was still a test in the US ([eBay Help](https://www.ebay.com/help/buying/bidding/bidding?id=4003); [Value Added Resource](https://www.valueaddedresource.net/ebay-expands-extended-bidding-auctions/)). Catawiki adds 90 seconds; Bring a Trailer resets to 2 minutes ([Catawiki](https://www.catawiki.com/en/help/bidding-basics/why-are-bidding-times-for-some-lots-occasionally-made-longer); [Fox Business](https://www.foxbusiness.com/markets/how-one-guys-car-blog-became-1-billion-marketplace)). Revenue evidence is mixed ([Ockenfels & Roth 2006](https://www.cs.princeton.edu/courses/archive/spr08/cos444/papers/ockenfels_roth06.pdf)).

| Norm | Real platform | This demo | Production change |
|---|---|---|---|
| Proxy bidding | Good practice (accuracy of disclosure is required) | Seller can pick `PROXY`; the engine bids up to a private maximum (BID-01). Our own rule is stated in requirements §5.2 (price = min(highest max, second-highest max + increment); earlier equal maximum wins; price rises to the reserve once the leader's maximum reaches it), and the oracle recomputes every price from the bid history (SIM-03). We don't claim eBay parity | Publish the same rule on a user-facing terms page; add property-based tests over random bid sequences |
| Increment table | Good practice | One seller-set increment per auction, default $1.00, minimum $0.01, frozen at publish | Optionally a versioned price-band table, snapshotted on each listing |
| Reserve | Good practice | Hidden reserve with met/not-met badge; frozen at publish | Same |
| Retraction | Good practice | None (Won't) | See §1 |
| Soft close | Good practice (disclosure required) | Hard close (Won't soft close) | Per-listing ending policy (window, extension, reset or add, total cap), fixed at first bid, pushed live. **Licensing review first** (Tennessee) |

## If this were a real marketplace

A prioritized plan. Each item names the trigger that makes it necessary and the system change.

### Before launch (any real money or goods)

| # | Change | Trigger | System change |
|---|---|---|---|
| 1 | **Counsel review** | Any launch | Review terms, venue position, retraction rule, and every "Required" row on this page |
| 2 | **Payment processor, never hold funds** | First real payment | Integrate a licensed processor; store only payment references; no balances or escrow in our database |
| 3 | **Sanctions screening** | First real user; no threshold | Screening service at sign-up and on list updates, with fuzzy matching; geo-block by IP, billing and shipping address |
| 4 | **Passkeys instead of email-only sign-in** | Any real account | WebAuthn sign-in; `DEMO_MODE` off; MFA or passkeys for admins |
| 5 | **Session alerts** | Any real account | New-sign-in alert through an independent channel; active-sessions page; decide again between single-session and view-and-revoke |
| 6 | **Published terms, versioned per bid** | First real bid | Terms page (floor types, proxy formula, increments, closing, retraction, cancellation grounds); `terms_version` stored on each bid |
| 7 | **Prohibited-item and recall screening** | First real listing | Listing-policy engine: category bans, keyword filters, CPSC recall matching, takedown queue |
| 8 | **Notice-and-action** | First real listing | "Report listing/user" with reason codes; tickets; statement of reasons to reporter and seller; extend the admin audit log (ADM-03) into a moderation log |
| 9 | **Privacy notice and retention** | First real user | Notice at sign-up; retention schedule; account deletion; coarse IP security log, hashed, purged after 30–90 days |
| 10 | **Shill detection** | First real bid | Per-bid IP hash, device and session; linked-account block; nightly risk score; enforcement ladder |
| 11 | **Simulator out of production** | Launch | Simulator and its reserved accounts only in staging |
| 12 | **Transactional email** | First email sent | Separate transactional and marketing streams; suppression list; accurate headers |
| 13 | **Accessibility audit** | Launch | Manual WCAG 2.2 AA audit, including the auction items in §7 |

### At a threshold

| Change | Trigger | System change |
|---|---|---|
| **INFORM seller verification** | A seller reaches 200 sales of new/unused items and $5,000 in 12 months; disclosure at $20,000 | Seller-volume counter; KYC workflow with 10-day timers; annual re-certification; suspension state; listing disclosure panel; report link and phone |
| **Sales-tax engine** | A state's nexus threshold (often $100k or 200 transactions; $500k in Texas) | Per-state nexus tracker; tax engine at checkout; remittance and records |
| **CCPA program** | $26.625M revenue or 100,000 consumers | Notice at collection; access and deletion request handling |
| **Art and cultural-goods AML** | Intermediating sales of €10,000 / £10,000 or more | Category flag; linked-transaction totals; KYC gate; reporting process |

### When changing the auction format

| Change | Trigger | System change |
|---|---|---|
| **Licensing review** | Adopting soft close (Tennessee licenses "extended-time" internet auctions) | Per-state feature flag on the ending policy; jurisdiction capture |
| **Atomic Buy It Now** | Adding Buy It Now (*Armiros v. Rohr*: clicking it forms a contract; [Osborn Maledon](https://www.omlaw.com/azapp-blog/postings/2018/arizona-court-of-appeals-division-one-holds-that-a-contract-is-formed-when-a-buyer-clicks-the-buy-it-now-button-on-ebay/)) | BIN purchase ends the auction under the same per-auction lock as bids |
| **All-in price** | Adding fees or a buyer's premium | Total shown on bid confirmation; fee rate snapshotted on the listing |
| **Review integrity** | Adding feedback | Transaction-gated reviews; no suppression; labelled seed data |
| **Location consent** | Using browser geolocation | Opt-in with stated purpose; dispose after use |
| **Operational location analytics** | Enough traffic that per-location problems matter (any production launch) | City/metro and ASN from server-side IP lookup added as telemetry labels; per-location error, latency, reconnect and bid-rejection dashboards; minimum group size before display; raw logs kept 30 days, aggregates longer; privacy-notice entry and legitimate-interest assessment (see §5) |

### When expanding to the EU or UK

| Change | Trigger | System change |
|---|---|---|
| **Consumer contract rights** | Trader sellers selling to EU/UK consumers | Seller-type flag and badge; returns field; 14-day cancellation flow; ranking explanation |
| **GDPR basics** | Offering services to people in the EU | Art 13 notice; documented legitimate interest for security logs; records of processing |
| **Product safety (GPSR)** | Consumer products sold into the EU | Safety Gate registration; product-safety fields; 2/3-working-day queues; recall notices |
| **DSA marketplace duties** | No longer a micro or small enterprise | Trader traceability (Art 30); compliance-by-design listing form |
| **UK Online Safety Act** | Significant UK users or UK targeting | Illegal-content risk assessment; reporting and takedown records |
