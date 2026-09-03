# Open Policy and Design Decisions

Decisions the organization must make. Each records the ambiguity, why EMC cannot resolve it,
the options, a recommended default, and what is blocked until it is answered.

**These are not product preferences.** Each one changes whether EMC states a *correct* regulatory
position. Guessing would produce exactly the failure this project exists to avoid: presenting a
design assumption as though AR 195-5 mandated it.

Where a decision has a low-risk default, EMC implements the default **as configuration**, marks
it in the UI as a local policy setting, and records it here as provisional.

---

## DEC-01 — Do CI units conduct a separate quarterly inventory?

**Revised.** The earlier version of this entry treated frequency and method as one question and
recommended reading 3-2b's exemption broadly. The three questions are separate, and only the
third is genuinely open.

**The text.**

- **3-1b(2)** — during the regular **monthly** CI inspection, a **100 percent joint inventory** is
  conducted. **[REG]**
- **3-2a(1)** — inventories "will be conducted ... once in each calendar quarter." **[REG]**
- **3-2b** — "**Quarterly inventories.** (CI units are exempt from the requirements of **this
  paragraph** ...)". Its subparagraphs prescribe the *methods*: 3-2b(1) disinterested-officer
  inventories and 3-2b(2) reverse inventories.

**1. Frequency.** Settled by arithmetic, not interpretation: a monthly 100 percent joint inventory
**occurs in every calendar quarter**. Whatever 3-2a(1) requires as to *occurrence*, the CI regime
under 3-1b(2) meets it.

**2. Method.** 3-2b's exemption unambiguously relieves CI units of the 3-2b(1) and 3-2b(2)
*methods*. The CI method is the 3-1b(2) joint inventory, with the 3-2g(1)-style certification
statement in the ledger as 3-1b(2) prescribes.

**3. Whether the monthly joint inventory *is* the quarterly inventory, or a separate quarterly
event is still required.** This is the open question. Reading A: 3-2b's exemption covers the
methods only, and a distinct "quarterly inventory" event remains owed under 3-2a(1). Reading B:
the monthly joint inventory satisfies 3-2a(1)'s occurrence requirement and nothing further is
owed. The text does not settle it, and **EMC will not invent an interpretation the text does not
support.**

**What EMC does.** It **does not automatically schedule an additional "quarterly" inventory
session for a CI evidence room.** The monthly 100 percent joint inventory is the scheduled event
(INV-011). If the organization holds a policy interpretation requiring a distinct quarterly
event, that is recorded as a **[LOCAL]** setting and EMC schedules it; absent one, scheduling a
redundant session would be EMC asserting Reading A on its own authority.

**Recommendation.** Operate on Reading B for scheduling purposes, and pursue **C** — a formal
interpretation from the proponent via DCS G-2 (**1-6c**) — because this is precisely what that
channel exists for. Record the answer here.

**Blocks:** INV-011 (whether a separate quarterly session is configured).

---

## DEC-02 — What is a "working day"?

**Ambiguity (AMB-02).** AR 195-5 does not define the term, and two provisions turn on it:

- **2-4a** — evidence released to the custodian **no later than the first working day** after
  acquisition (two working days when served from a separate location).
- **3-3a** — **"up to 5 working days"** to resolve apparently missing evidence **before an
  official inquiry is initiated**.

**Why EMC cannot decide.** 3-3a is the only hard numeric deadline in this part of the regulation
and it has real consequences: getting it wrong either initiates an inquiry prematurely or misses
the point at which one becomes mandatory. The answer depends on the unit's actual duty schedule —
federal holidays, local training holidays, deployed schedules, and shift patterns all move it.
Hard-coding a Monday–Friday calendar inside business logic would silently produce wrong dates.

**Options**

| | Definition |
|---|---|
| A | Monday–Friday excluding federal holidays |
| B | A, plus locally-designated training holidays and unit down-days |
| C | An explicitly maintained `DutyCalendar` per evidence room |

**Recommendation: C**, seeded with **B**. `DutyCalendar` is already in the model. EMC displays the
computed deadline **with the calendar that produced it**, so a custodian can see and challenge the
basis rather than trusting an opaque date.

**EMC must never present a computed deadline without showing the calendar used.**

**Blocks:** LOSS-002, VCH-016, and the working-day component of IAM-006.

---

## DEC-03 — One evidence room per instance, or several?

**Ambiguity (AMB-03).** **2-4c** scopes the document-number series to a calendar year; **2-7g**
shows the series belongs to an **evidence room** (a receiving room assigns "the next document
number of the receiving evidence room"). AR 195-5 does not address one application serving
several rooms, because it does not contemplate the application.

**Why EMC cannot decide.** It determines uniqueness scoping, default access control, inventory
population, appointment scoping and inspection scheduling. Retrofitting an evidence-room key into
an accountability schema after it holds real data is expensive and risky.

**Options**

| | Deployment |
|---|---|
| A | One instance per evidence room |
| B | One instance, several rooms, strict per-room scoping and deny-by-default cross-room access |
| C | One instance, several rooms, cross-room visibility for designated supervisors |

**Recommendation: B.** `EvidenceRoom` is in the model from day one and every accountability
aggregate carries the key, so **A** remains available at no cost while **B** and **C** stay
possible. Cross-room read access is deny-by-default and must be granted explicitly.

**Blocks:** the scope half of VCH-005 (the constraint is implemented; the deployment posture is
not decided).

---

## DEC-04 — What counts as one evidence item?

**Ambiguity (AMB-04).**

- **2-1b** — when items are grouped together (for example, a box containing tools) and **listed as
  one item** on the DA Form 4137, **only one DA Form 4002 is used**.
- **2-2f** — each item is sealed in its own container, and items under **separate numbers** are not
  sealed together.

Read together: **an item is the numbered line on the DA Form 4137**, and its quantity may exceed
one physical object.

**Why confirmation matters.** Inventory counts, the "expected population" of a session, and the
totals presented to a commander at a monthly 100 percent joint inventory all depend on it. A count
of 184 must mean the same thing to the custodian, the commander and the inspector.

**Recommendation:** confirm the reading above. EMC models `EvidenceItem` as the numbered line with
a quantity/approximation field (**2-3d** requires approximations for large numbers and exact
denominations for currency), and inventory counts **numbered lines**, stating so on the screen.

**Blocks:** confirmation of ITEM-010.

---

## DEC-05 — What happens when the alternate custodian's temporary-absence window expires?

**Status: DECIDED IN V1 — hard denial.** This entry is kept because the earlier recommendation was
wrong and the reasoning matters; a future reviewer should not re-open it without reading why.

**Ambiguity (AMB-05).** The regulation uses two different units:

- **1-4i** — a temporary absence is **more than 1 working day and not more than 30 consecutive
  days**.
- **1-7c(2)** and **3-2d** — thresholds expressed in **30 calendar days**, with 3-2d requiring
  that if it is known the absence will exceed 30 days, **the alternate is appointed primary on
  orders and a joint inventory is conducted**.

That ambiguity is about *counting* (working days vs. calendar days, see AMB-05), not about
*whether the window closes*. It does not create discretion to act past it.

**Decision.** Once the primary's absence exceeds 30 days, an alternate whose only authority is
`AlternateEvidenceCustodian` + an open duty assumption is **denied** every accountability
permission (**IAM-020**). The denial names AR 195-5 3-2d and states the remedy: appoint the
alternate primary on orders and conduct the joint inventory. Authority then flows from the primary
appointment, not from a lapsed temporary-absence provision.

**There is no override — not even an audited one.** An earlier draft of this document recommended
"warn from day 25, block at day 31, with a commander override that is itself audited," and the
code implemented warn-and-continue. Both were wrong, for the same reason:

- AR 195-5 grants no such override. 1-4i bounds the absence at 30 consecutive days and 3-2d states
  what to do when it will be exceeded. Neither paragraph contains a provision to extend it.
- A commander already holds the authority the regulation gives — appointing the person primary on
  orders with a joint inventory. An in-application override would not add authority; it would
  substitute a software affordance for the act the regulation actually requires, and it would
  create a record showing evidence accepted under an authority nobody in fact held.
- "Orders are late" is an administrative problem in the unit, not a grant of custodial authority.
  Software must not resolve it by manufacturing one. **[REG]**

**What the software may do instead (all [LOCAL]/[DESIGN], none regulatory):**

- Surface an **advisory** during the last 5 days of the window so the transition can be started
  before authority lapses. Five days is a local convenience figure chosen to give a unit time to
  cut orders; AR 195-5 states no such threshold.
- Model the transition itself — `PrimaryCustodianTransition` records the incoming and outgoing
  appointments, the joint inventory and its discrepancy resolution, and the 3-2g(3) ledger
  statement — so the regulatory path is at least as easy to follow as the blocked one was.

**Also settled by the same reasoning:** the 30-day clock runs from the **primary's absence start**
(1-4i bounds the *absence*), not from the date the alternate assumed duties. An earlier
implementation measured from the assumption date, which silently extended the window whenever the
alternate stepped in late.

**Still open (narrow):** whether the count is working days or calendar days (AMB-05). V1 uses
**consecutive calendar days**, the stricter reading, because it can never authorize action the
other reading would forbid. A unit whose local interpretation differs should raise it through the
proponent rather than change the code.

**Requirements:** IAM-006 (assumption), IAM-019, IAM-020 (expiry denial), IAM-021 (no assumption
created past the window), IAM-022 (transition joint inventory).

**Note:** 1-7c(2) states that for an absence of 30 calendar days or less **there is no requirement
for a 100 percent inventory** — so crossing 30 days changes the inventory obligation too, not just
the authority. `CustodianDutyAssumption.RequiresHundredPercentInventoryOnResumption` records that.

---

## DEC-10 — Year-first document numbers

**Observation.** Some evidence rooms write the document number as `26-01` — two-digit year,
hyphen, two-digit sequence. AR 195-5 **2-4c** prescribes `001-18`: a three-digit sequence
beginning at 001, a hyphen, then the two-digit calendar year. The regulation describes exactly one
layout and does not contemplate another.

**What EMC does.** The layout a room writes is an effective-dated, per-room
`EvidenceRoomNumberingPolicy` **[LOCAL]** (VCH-023). The regulation's layout is the default and
applies when a room has recorded nothing. A room may record a year-first layout, but it must
either cite the local SOP, policy, waiver or directive that authorizes it (`LocalAuthorized`) or
record it as a legacy practice with no authority yet cited (`LegacyObserved`) — in which case
every number recorded under it carries a warning saying so. The identity of a number is
`(room, calendar year, sequence)` whatever the layout, so switching layouts renumbers nothing and
cannot reissue a number (VCH-011). The number as written is preserved verbatim.

**What EMC will not do.** Describe the year-first layout as something AR 195-5 permits or
requires, anywhere — in code, on screen, or in this documentation. If a room believes it holds
authority for its layout, that authority is recorded on the policy; if it does not, the flag
stays until it does or the room adopts the regulation's layout.

**Decision needed locally:** which of the two bases applies to this room, and the reference.

---

## DEC-06 — What classification is the system accredited for?

**The most consequential decision here.**

**Ambiguity.** AR 195-5 delegates classified evidence handling to **AR 380-5** (2-6h, 2-7k, 2-9r,
4-1a) and CI storage to **AR 381-20** (4-2a(2)). EMC invents no classified requirements — correct.

But there is an architectural consequence the regulation does not address: **an aggregation of CI
evidence descriptions, case control numbers, subject names and locations may itself be
classified**, independent of whether any individual item is. If it is, the accreditation, hosting
enclave, backup handling and disposal of the database all change.

**Why EMC cannot decide.** This is an accreditation and security-classification-guide question for
the organization's security manager and the accrediting authority. No application can decide the
classification of its own aggregate content.

**Options**

| | Posture |
|---|---|
| A | Accredit at UNCLASSIFIED; **prohibit** classified content in free-text fields; UI banner and per-field markings enforce the prohibition procedurally |
| B | Accredit at the highest level any entered content may reach; host in the corresponding enclave |
| C | Two deployments — an unclassified companion and a separate accredited instance |

**Recommendation:** begin with **A** as the design control, and obtain a written determination
before the system holds real data. EMC already carries a `ClassificationMarking` on free-text
fields and a configuration-driven banner, so **B** requires a configuration change and an enclave
move, not a redesign.

**This decision must be made before EMC holds real evidence data.**

**Blocks:** SEC-005.

---

## DEC-07 — Are EMC's own records Army records under ARIMS/RRS-A?

**Ambiguity (AMB-07).** **1-5** places records management for "all record numbers, associated
forms, and reports required by this regulation" under the **Records Retention Schedule — Army
(RRS-A)** via ARIMS.

AR 195-5 sets retention for the **paper**: inactive DA Forms 4137 disposed **3 years** after
becoming inactive (**2-4h**); ledgers disposable **3 years** after the last item is disposed
(**2-5a**).

It says nothing about a companion database, because it does not contemplate one.

**Why EMC cannot decide.** Only the organization's records manager can determine whether EMC's
database is itself an Army record, which RRS-A record number applies, and therefore how long EMC
data must be kept and when it must be destroyed. This is not a technical question.

**Note the tension:** if EMC data is *not* a record, it should arguably follow the paper's
retention. If it *is* a record, EMC becomes subject to its own retention and destruction
obligations — including the ability to *destroy* data on schedule, which sits awkwardly beside an
append-only design. Both the retention rule and the destruction mechanism need to be designed
deliberately, not discovered later.

**Recommendation:** obtain a determination from the records manager. Until then EMC retains all
data and destroys nothing, which is the safe default but is **not** a decision — it is the absence
of one.

**Blocks:** RET-008.


**What is now built, and what is not (this slice).** The evidence room's PAPER DA Form 4137 record
computes and displays its 2-4h destruction eligibility — three years from the date the record
became inactive — and the custodian records confirmed destruction. That is the paper. **EMC's own
digital data and stored scans are retained; nothing digital is destroyed or scheduled for
destruction on that rule**, and the UI says so beside every eligibility statement. The 2-4h
eligibility of the evidence-room copy also says nothing about the copy in the investigative case
file, which is a different record on a different schedule. This decision stays open until the
records manager's determination exists.

---

## DEC-08 — May EMC hold restricted-reporting sexual assault data?

**Ambiguity (AMB-06).** **2-16** governs restricted reporting, and **2-5b(1)(c)** requires the
ledger description for such cases to read simply **"Restricted Sexual Assault."** The regulation
deliberately limits what is recorded and who may see it.

Unlikely in a CI evidence room, but "unlikely" is not a policy.

**Options**

| | Posture |
|---|---|
| A | Prohibit entirely; EMC records no restricted-reporting evidence |
| B | Permit with a restricted-description mode mirroring 2-5b(1)(c) and a separate access-control group |

**Recommendation: A** for V1, enforced by policy and stated in the user guidance, with **B**
designed if the organization identifies a genuine need. Building **B** speculatively would create
a sensitive access-control surface with no confirmed requirement.

---

## DEC-09 — Two workflows for temporary release, or one?

**Design question, not a regulatory ambiguity.** **2-7a** treats temporary release uniformly, but
practice differs: an agent taking an item to a local Art. 32 hearing and a shipment to USACIL by
registered mail (**2-7e**) have different evidence, different follow-up cadence and different
return mechanics.

**Options:** one workflow with conditional fields, or two entry points sharing one
`TemporaryRelease` entity.

**Recommendation:** one entity, two entry points. The regulation defines the suspense **folders**
(**2-4f(3)**: USACIL, ADJUDICATION, PENDING DISPOSITION APPROVAL) and EMC keeps those exact
categories; the entry points are a usability choice above them.

**Blocks:** nothing. Flagged so it is decided during suspense implementation rather than by
default.

---

## Summary

| ID | Decision | Blocks | Recommended default |
|---|---|---|---|
| DEC-01 | CI quarterly inventories | INV-011 | Monthly regime; seek interpretation via 1-6c |
| DEC-02 | "Working day" definition | LOSS-002, VCH-016, IAM-006 | Maintained `DutyCalendar`; always show the calendar used |
| DEC-03 | One evidence room or several | VCH-005 scope | Multi-room, strict scoping, deny-by-default |
| DEC-04 | What is one item | ITEM-010 | The numbered line; quantity may exceed one |
| DEC-05 | Alternate window expiry | IAM-006, IAM-020 | **Decided:** hard denial past 30 days, no override; 5-day advisory is [LOCAL] |
| DEC-06 | Accredited classification | SEC-005 | UNCLASSIFIED + prohibition; **decide before real data** |
| DEC-07 | ARIMS/RRS-A status of EMC data | RET-008 | Retain all pending determination |
| DEC-08 | Restricted-reporting data | — | Prohibit in V1 |
| DEC-09 | Temporary-release entry points | — | One entity, two entry points |
| DEC-10 | Year-first document numbers (`26-01`) | VCH-023 | Recorded as a LOCAL layout; cite the SOP/waiver or leave flagged as awaiting validation. AR 195-5 does not authorize it. |
