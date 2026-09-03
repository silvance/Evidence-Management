# AR 195-5 Regulatory Requirements Relevant to the Evidence Management Companion (EMC)

**Source of truth:** AR 195-5, *Evidence Procedures*, 25 August 2019 (administrative revision
13 September 2023). Proponent: Director, U.S. Army Criminal Investigation Division.

**Scope of this document.** This is a working extract of AR 195-5 provisions that bear on the
design of an internal Army Counterintelligence (CI) evidence-management **companion**
application. It is not a substitute for the regulation. Where a statement here is an
interpretation, a design inference, or a recommended control rather than regulatory text, it is
labelled explicitly. Everything not so labelled is a direct paraphrase of the cited paragraph.

**Reading conventions used throughout the repository:**

| Label | Meaning |
|---|---|
| **[REG]** | Directly required by the cited AR 195-5 paragraph. |
| **[REG-REF]** | AR 195-5 defers the requirement to another authority (AR 380-5, AR 381-20, AR 15-6, AR 25-400-2, ARIMS/RRS-A). EMC must not invent the content of that other authority. |
| **[DESIGN]** | A design decision made by this project. AR 195-5 neither requires nor forbids it. |
| **[CONTROL]** | A recommended integrity or security control. Not regulatory. |

> **Never present a [DESIGN] or [CONTROL] item to a user, an inspector, or an auditor as
> something AR 195-5 mandates.** Several of the most useful features of this application
> (complete location history, aging dashboards, append-only event storage) fall into those
> categories. See §12 for the specific cases where this distinction matters most.

---

## 1. Applicability to Army CI

| Ref | Provision |
|---|---|
| **1-1** | The standards of AR 195-5 apply to Army CI agents collecting and processing evidence by authority of AR 381-20. The regulation governs internal management, control, and disposition of evidence collected during CI investigations. USACIL's internal processes are exempt from AR 195-5. |
| **1-1** | All policies and procedures apply to consolidated and long-term evidence rooms/facilities. |
| **1-4a** | DCS, G-2 ensures Army CI organizations collecting and processing evidence under AR 381-20 do so per AR 195-5. |
| **1-6c** | CI waiver/exception requests route through DCS, G-2 to OPMG. |

**Consequence for EMC:** the CI variant of AR 195-5 is the target. Several provisions that
dominate USACIDC/MP evidence software (quarterly disinterested-officer inventories, reverse
inventories) are **exempted or replaced** for CI units. See §7.

---

## 2. The controlling constraint on V1: automated systems (para 2-5c)

> **2-5c.** "Stand-alone automated evidence ledger/accountability systems must be approved by
> the Commander, USACIDC (CIOP-COP-PO), for USACIDC units; the Office of the Provost Marshal
> General (DAPM-MPD-LE) for DALEO activities; or **Army G-2X for CI organizations** prior to
> use. **There is no need for approval of automated systems used in conjunction with or to
> enhance the requirements of this regulation.**"

This single paragraph defines what V1 may and may not be.

| Ref | Provision | Effect on EMC |
|---|---|---|
| **2-5a** | The evidence ledger **must be a bound book** — *or an approved automatic equivalent, see para 2-5c*. | **[REG]** Until Army G-2X approval is obtained, the bound-book ledger remains the authoritative accountability record. EMC is not it. |
| **2-5c** | Stand-alone automated evidence ledger/accountability systems for CI organizations require Army G-2X approval prior to use. | **[REG]** V1 must not be, or hold itself out as, the stand-alone ledger. |
| **2-5c** | No approval is required for automated systems used *in conjunction with or to enhance* the regulation's requirements. | **[REG]** This is the authority under which V1 operates. |

**V1 posture (see `EMC-001` .. `EMC-006` in the traceability matrix):**

- EMC **assists** the AR 195-5 process. The bound ledger and the original DA Form 4137 remain
  the authoritative records.
- EMC **must not** assign the authoritative evidence document number (see §5).
- Every screen that displays accountability data must be unambiguous about which record is
  authoritative.
- The architecture must be able to *become* an approved automated equivalent later without a
  rewrite, but V1 must not assume approval. **[DESIGN]**

---

## 3. Custodians, appointments, and who may do what

| Ref | Provision | EMC effect |
|---|---|---|
| **1-4g(1)** | Commanders of units with a CI investigative mission will appoint, **in writing, one primary and one alternate** evidence custodian. | Exactly one active primary and one active alternate appointment per evidence room at any instant. |
| **1-4g(2)** | Commander supervises the evidence custodian. | |
| **1-4g(3)** | Commander ensures proper handling/processing and **inspects the evidence room or security container monthly**. Inspection conducted by the CI unit commander, or the acting commander when the commander is unavailable. | Inspection actor is constrained. |
| **1-4h(1)-(5)** | Primary custodian: account for, preserve, safeguard and (when authorized) dispose of all evidence in a timely manner; maintain all evidence records and files; protect from loss, deterioration, contamination, needless damage; seek guidance on unusual circumstances and **document it in an MFR or email attached to the original evidence document and/or case file**; ensure the Final Disposal Action and Final Disposal Authority areas of DA Form 4137 are completed **before** the approval authority signs. | |
| **1-4i** | The alternate assumes the primary's duties **during the primary's temporary absence**. **A temporary absence is more than 1 working day and not more than 30 consecutive days.** In an emergency another alternate may be appointed in writing; the new orders supersede the previous alternate's. | **Appointment and assumption of duties are two different periods.** An alternate may hold the appointment for months without the primary ever being absent. The 30-day limit runs from the date duties were **assumed**, not from the appointment date. See §3.1. |
| **1-7a(1)(c)** | **Military** CI evidence custodian (primary and alternate) **must be a credentialed CI agent**. CI agents in a probationary program **will not** be appointed. | Applies to the **military** category only. |
| **1-7a(2)(c)** | **Civilians** may be appointed primary or alternate **"depending on the needs and requirements of the unit and at the discretion of the commander"** (CI units). | Applies to the **civilian** category. **Note what this paragraph does not say:** unlike the USACIDC (1-7a(2)(a)) and Military Police (1-7a(2)(b)) civilian paragraphs, it states **no job-series list and no background-investigation requirement**. EMC must not import those into the CI case. |
| **1-7b** | A copy of the appointment documents is kept in the evidence room files per AR 25-400-2, maintained as long as the position is held. AR 195-5 is cited as the appointment authority. | **[REG-REF]** AR 25-400-2 governs filing. |
| **1-7c(1)** | On assuming temporary duties, the alternate enters and signs a prescribed statement **in the evidence ledger** immediately below the last entry: *"I (Name), on (Date), assume all duties of the primary evidence custodian during the temporary absence of the regularly appointed custodian. I accept responsibility and accountability for all evidence in the evidence room."* | Ledger event EMC can prompt for and record as *having been made on paper*. |
| **1-7c(2)** | On return, the primary verifies all entries and evidence are correct, then enters and signs a prescribed resumption statement in the ledger. **If the absence is 30 calendar days or less there is no requirement to conduct a 100 percent inventory.** | |
| **1-7c(3)** | If a primary or alternate finds that an **incorrect entry** has been made, they will **immediately inform the responsible CI supervisor** and prepare an **MFR outlining the error and the corrective action taken**. The original MFR is filed with the proper DA Form 4137 (or in a file folder if the error was not on a DA Form 4137); a copy goes in the law enforcement case file. | **This is the regulatory anchor for EMC's correction model.** See §9. |
| **4-1a** | Primary and alternate custodians are **required to have the necessary level of security clearance** for classified evidence stored. | |

### 3.1 Appointment is not assumption of duties

AR 195-5 describes two distinct things, and conflating them produces a wrong authorization model.

| | Governing paragraphs | Duration |
|---|---|---|
| **Appointment** as primary or alternate custodian | 1-4g(1), 1-7b | Long-lived — held until the position changes; appointment documents are retained "as long as the primary and alternate custodians retain the position" |
| **Assumption of the primary's duties** by the alternate | 1-4i, 1-7c(1), 1-7c(2) | Short — the primary's temporary absence, "more than 1 working day and not more than 30 consecutive days" |

Consequences for EMC:

1. **Holding the alternate appointment does not, by itself, authorize acting as the evidence
   custodian.** 1-4i grants that authority "during his or her temporary absence." An alternate
   appointed for a year has no custodial authority on a day the primary is present.
2. **The 30-consecutive-day limit runs from the date duties were assumed**, not from the
   appointment date. Measuring from the appointment produces a limit that expires while the
   alternate has never acted at all.
3. **1-7c(1) and 1-7c(2) require handwritten, signed ledger statements** at assumption and at
   resumption. EMC records that the paper entries were made; it does not produce them.
4. **1-7c(2)**: an absence of 30 calendar days or less carries **no** 100 percent inventory
   requirement. Beyond that, 3-2d requires the alternate to be appointed primary on orders and a
   joint inventory conducted.

---

## 4. Marking, sealing, and item identity

| Ref | Provision |
|---|---|
| **2-1a** | The first CI agent assuming custody marks the evidence for future identification: **time and date of acquisition and the initials** of the person assuming custody. If marking is not possible or practical, the evidence is placed in a sealed, marked container. |
| **2-1b** | A self-adhesive **DA Form 4002** (Evidence/Property Tag) is attached to each item of evidence or evidence container. **When items are grouped together (for example, a box containing tools) and listed as one item on the DA Form 4137, only one DA Form 4002 is used.** Attaching the tag alone does not satisfy the regulation — the item or sealed container must also be marked. |
| **2-1c** | Unnecessary damage or destruction of personal property that may be returned to the owner is prohibited; markings should be inconspicuous, or the item sealed in a marked container. |
| **2-2a** | Sealing: all openings, joined surfaces and edges sealed with paper packaging tape or evidence tape that shows tampering. DA Form 4002 affixed. The sealer writes **initials or signature across the seals in several different locations**, visible on both tape and container. On breach the container is **resealed when appropriate**, with the resealer's initials/signature **and time and date** across the new seals. Before sealing for reasons other than preventing cross-contamination or preserving fungible evidence, the evidence is **jointly inventoried** between the agent and the custodian. |
| **2-2f** | **Each item will be sealed in its own separate container.** Items listed under a separate number on the DA Form 4137 **will not** be sealed together with other items for convenience or storage. (Exception: long-term retention boxes, para 2-13.) |

**Consequence:** a DA Form 4137 *item number* is the unit of physical identity and sealing. A
grouped set listed as one item is one item. This is the regulatory basis for making
`EvidenceItem` — not the voucher — the primary unit of accountability in EMC.

---

## 5. DA Form 4137 preparation and the evidence document number

### 5.1 Preparation

| Ref | Provision |
|---|---|
| **2-3a** | **All physical evidence will be inventoried and accounted for on DA Form 4137.** A **computer-generated DA Form 4137 is authorized**; it must be prepared as a two-sided document with a vertical flip whenever reasonably possible. |
| **2-3b** | The agent who **first acquired** the evidence prepares the form. A signed copy is given as a receipt to the person releasing the evidence, or left at a search scene. The **Army CI case control number** is recorded on the DA Form 4137 **and** the DA Form 4002. When evidence is collected in response to a **request for assistance (RFA)**, **both** the seizing and requesting offices' numbers are recorded. |
| **2-3c** | When evidence is sealed in a container, the **Description of Articles** section should be annotated to reflect the sealing. |
| **2-3d** | The Description of Articles block describes the item **accurately, to individualize it to the exclusion of any other item**. Descriptions include **only descriptive information** — no supposition or suspicion (not "suspected to be marijuana"). Limited to **permanent characteristics**. Large numbers or weight given in **approximations**. Seized or safeguarded **funds: exact amount, by denomination**. **Serial numbers, if available, will be recorded.** The words **LAST ITEM** are placed in capital letters on the next line below the last listed item, centred, with lines or slashes drawn to the left and right margins. |
| **2-3e** | Custodians will **not normally breach or inventory** the contents of a sealed container. The custodian normally annotates the *Purpose of Change of Custody* with **SCRCNI** (sealed container received; contents not inventoried). **Any breach by the custodian will be annotated on the DA Form 4137**, the container opened by cutting without damaging seals if possible, and an **MFR describing the purpose of the breach affixed to the original DA Form 4137 as a permanent attachment**. |
| **2-3f** | **Any change in custody** after first acquisition is recorded in the **Change of Custody** section of the DA Form 4137. When custody of sealed evidence changes, the Purpose of Change of Custody column is noted **SCRCNI**. |
| **2-3g** | Custodians **review** the submitted DA Form 4137 and have the submitting agent **correct and initial all errors**. Evidence received from a non-Army agency: the first agent inventories and marks it, prepares a DA Form 4137, and **attaches** the other agency's receipts or chain-of-custody documents. | EMC models this as a voucher review: the custodian returns the form recording what was found, the **submitting** agent corrects it and attests that the **paper** form was corrected and initialed, and resubmits. EMC records the attestation; it supplies no initials (2-5c companion). This is not the 1-7c(3) path. VCH-017..VCH-021. |
| **2-3i** | **Continuation of Chain of Custody**: a **new DA Form 4137** is used. Case control number, receiving activity, location and person from whom received are entered as on the original. The entry *"Continuation of Chain of Custody, dated (last date shown on the preceding chain of custody page)"* is placed in the middle of the Description of Articles section. |
| **2-3j** | Partial extraction of an item for examination by a non-USACIL laboratory uses that laboratory's chain-of-custody document; the original DA Form 4137's Chain of Custody section is annotated describing what was extracted and from which item; the original document number, item number and USACIL exhibit number appear on the derived document; a copy is attached to the original. |
| **2-3l** | Items with suspected blood or known/suspected bodily fluids or parts: the Description of Articles section **will reflect `POSSIBLE BIOHAZARD` in all capital letters after each such item**. |
| **2-3n** | .0015 funds received as evidence: DD Form 281 (or an MFR with the accounting classification) maintained with the original DA Form 4137. **All .0015 funds received as evidence will be processed into the evidence room and assigned a document number before ever being temporarily released to a non-DA law enforcement agency.** |

### 5.2 Turn-in timing

| Ref | Provision |
|---|---|
| **2-4a** | Except in unusual circumstances, physical evidence is released to the evidence custodian **no later than the first working day after it is acquired**. Evidence acquired during non-duty hours is secured in a temporary storage container and controlled by the person securing it until released. Activities served by a custodian **in a separate location** release the evidence **normally within two working days**, physically, by registered mail, or by an accountable commercial shipping service. |
| **App B-4a(8)** | Internal control check: *"Was all evidence turned into the evidence custodian by the end of the next work day after it was received, or if not, was there a memorandum for record attached to the DA Form 4137 explaining the cause for the delay?"* |

### 5.3 The evidence document number — **the central V1 constraint**

| Ref | Provision |
|---|---|
| **2-4c** | **Upon receipt of the evidence and DA Form 4137, the evidence custodian will assign a document number.** The number consists of **two groups of digits separated by a hyphen**: the first group is the number of the document **beginning with 001 for the first DA Form 4137 received for the calendar year**; the second group represents the **current calendar year** (for example, `001-18`). **The number is assigned by order of precedence from the evidence ledger** (or approved automatic equivalent, see para 2-5c). The number is entered **on all copies of the DA Form 4137 and each DA Form 4002**. |
| **2-7g** | On **permanent transfer between evidence rooms**, the receiving custodian enters **the next document number of the receiving evidence room** on both copies. **The prior document number is lined through in such a way that it remains legible.** |

**Consequences for EMC (this is `EMC-002`, `VCH-004`, `VCH-005`):**

1. The document number is a property of the **voucher (DA Form 4137)**, not of the item.
2. It is **assigned by the custodian, on receipt, from the ledger** — a paper act in V1.
   Because 2-4c ties assignment to order of precedence **in the ledger**, and the ledger is the
   bound book until G-2X approves otherwise (2-5a/2-5c), **EMC V1 must not generate it.**
3. The series is scoped to **one evidence room, one calendar year**. Uniqueness must be
   enforced on `(EvidenceRoom, CalendarYear, Sequence)` — not globally. **[DESIGN implication of 2-4c]**
4. A voucher can hold **more than one document number over its life** (2-7g). The prior number
   remains legible — it is superseded, never erased. EMC models this as a history, not a column.

### 5.4 Filing, suspense, and voucher closure

| Ref | Provision |
|---|---|
| **2-4d** | The custodian keeps the **original** DA Form 4137 and distributes copies after the chain of custody is complete and the document number is assigned. A copy goes to the agent for the case file. On permanent forward to another office the **original** goes to the gaining unit. |
| **2-4e** | **The location of the evidence in the evidence room will be recorded in pencil on the location block of the DA Form 4137. Location changes in the evidence room will be kept current by erasing the previous entry and noting the new location.** |
| **2-4f(1)** | Active DA Form 4137 files: numerical sequence, **no more than 50 vouchers per folder/binder**, range shown on the outside, highest numbers on top. |
| **2-4f(2)** | On temporary release **the original DA Form 4137 accompanies the evidence**; a copy is retained in a **suspense folder** until return. |
| **2-4f(3)** | **At least three suspense folders** are kept: **USACIL**, **ADJUDICATION** (Art. 32 IOs, courts, trial counsel, civilian prosecutor, other legal proceedings), and **PENDING DISPOSITION APPROVAL** (original sent to trial counsel/civilian prosecutor for disposition approval). |
| **2-4h** | **After *all* items of evidence listed on a DA Form 4137 have been properly disposed**, the original and related documents move to the **inactive** file, labelled by **month and year of the disposition date**. Inactive DA Forms 4137 are **disposed of 3 years after the date they become inactive**. |
| **2-5a** | Evidence ledgers **may be disposed of 3 years after the date the last item of evidence listed within it is disposed**, or held indefinitely. |
| **2-13b** | For long-term retention the DA Form 4137 concerned **continues to be maintained in the active DA Form 4137 file**. |

**Consequence (`VCH-007`):** 2-4h states voucher closure in terms of **all its items** being
disposed. This is direct regulatory support for **deriving voucher status from item status**
rather than maintaining a voucher-level status by hand.

---

## 6. The evidence ledger and the treatment of erroneous entries

| Ref | Provision |
|---|---|
| **2-5a** | The ledger shows accountability **through cross-reference with the DA Form 4137**. It accounts for document numbers assigned to DA Forms 4137. Ledgers must be **bound books** (or approved automatic equivalent, 2-5c). |
| **2-5b** | Six columns spanning two facing pages: Document Number/Date Received; law enforcement report number or **Army CI Case Control Number**; Description of Evidence; Date of Final Disposition; Final Disposition; Remarks. **Blue or black ink.** |
| **2-5b(1)(c)** | Description column includes **the item number from the DA Form 4137**. The entry does not imply the custodian inventoried the items. |
| **2-5b(1)(d)** | **"When a DA Form 4137 contains several items that are not disposed of on the same date, the date of disposition for each item will be shown opposite the item's description."** When all items are disposed on the same date, one date followed by **All Items**. |
| **2-5b(1)(e)** | Final disposition noted opposite the item's description; **All Items** when uniform. |
| **2-5b(1)(f)** | Remarks may record cross-references to other DA Forms 4137 from the same investigation, names, .0015 fund notations, laboratory results, and **SCRCNI**. |
| **2-5b(2)** | Entries requiring signatures (temporary absence of custodian, change of custodian, recording inspections and inventories) are **handwritten** and extend across both pages, bounded by straight lines. |
| **2-5b(4)** | After the last entry for a calendar year: *"This ledger pertains to DA Forms 4137 from 001 through (number) for calendar year (year)."* |
| **2-5b(5)** | **No blank pages or lines between entries.** Spaces left between entries are lined through, annotated **VOID**, and initialled by the custodian. **"Erroneous entries will be voided with one line drawn through the entry (so it may still be read) and initialed by the custodian. No liquid correction type products, correction tape, stick-on labels, or erasures are authorized to correct erroneous entries."** |

**Consequence (`AUD-001`, `AUD-004`) — the regulatory basis for append-only history:**

Paragraph **2-5b(5)** and paragraph **1-7c(3)** together express the regulation's philosophy on
error correction:

1. The erroneous entry **remains readable**. It is struck through, not removed.
2. The correction is **attributable** — initialled by the custodian.
3. Physical means of making the original vanish (correction fluid, tape, labels, erasure) are
   **prohibited**.
4. A separate narrative record — **the MFR** — states the error and the corrective action, is
   filed with the DA Form 4137, and is copied to the case file (1-7c(3)).
5. The **supervisor is informed immediately** (1-7c(3)).

EMC's correction model is the software analogue of exactly this and cites these paragraphs.

> **Important honesty note.** 2-5b(5) governs the **evidence ledger**. AR 195-5 does not
> contain a general "all electronic records shall be append-only" rule, because it does not
> contemplate a general electronic record. EMC's append-only event store is therefore
> **[DESIGN] + [CONTROL] modelled on 2-5b(5) and 1-7c(3)** — it is not itself a paragraph of
> the regulation. Document it that way.

---

## 7. Inspections, inventories, and inquiries — the CI variant

### 7.1 Monthly inspection

| Ref | Provision |
|---|---|
| **1-4g(3)** | The CI unit commander (or acting commander when the commander is unavailable) inspects the evidence room or security container **monthly**. |
| **3-1a** | A **monthly inspection** is conducted. The **first inspection by a new commander/SAC assuming supervisory control includes an inventory of all evidence**. The **internal control evaluation checklist (Appendix B)** should be used. The inspector determines whether: (1) the room is orderly and clean; (2) structural and security requirements are met, **including verifying that spare keys and combinations are sealed on SF 700 and secured in the CI supervisor's safe**; (3) evidence is being received, processed, safeguarded and disposed of per the regulation; (4) **evidence on temporary release for laboratory examination or judicial proceeding has not been so released for an excessive period**. |
| **3-1b(2)** | **"For regular monthly inspections performed by CI Commanders/SACs, because the amount of on hand evidence is normally minimal, a 100 percent joint inventory will be performed by the Commander/SAC and Primary Evidence Custodian."** The prescribed statement is entered in the evidence ledger immediately below the last entry and **signed by the CI Commander/SAC and the Primary Evidence Custodian**: *"We, the undersigned, certify that on (Date), in accordance with AR 195-5, a joint inventory of the evidence room was conducted. All evidence was properly accounted for with (no exceptions) or (the following exceptions). (Signature of Officer) (Signature of Evidence Custodian) (Printed Name, Grade, Unit)."* |

**This is the defining CI workflow.** For CI, the monthly inspection **contains** a 100 percent
joint inventory. EMC models an `Inspection` that may own an `InventorySession`.

### 7.2 Inventories

| Ref | Provision | CI applicability |
|---|---|---|
| **3-2a(1)** | Inventories conducted **once in each calendar quarter**. | See ambiguity **AMB-01** below. |
| **3-2a(2)** | On **change of the primary evidence custodian**; on change of the CI supervisor assuming supervisory control. | Applies. |
| **3-2a(3)** | On **loss of evidence** stored in the evidence room or **breach of security** of the evidence room. | Applies. |
| **3-2a(4)** | With the assistance of the internal control evaluation checklist. | Applies. |
| **3-2b** | **"Quarterly inventories. (CI units are exempt from the requirements of this paragraph but must adhere to the standards of AR 380-5.)"** — covering **3-2b(1) disinterested officer inventories** and **3-2b(2) reverse inventories**. | **Exempt.** |
| **3-2c** | Reverse inventories on change of DES/PM/SAC-RAC/detachment commander, within 30 calendar days. | Sits inside the reverse-inventory scheme; see **AMB-01**. |
| **3-2d** | **On change of the primary evidence custodian**, the incoming and outgoing primary custodians conduct a **joint physical inventory of all evidence**. All evidence records are checked. **The outgoing custodian will resolve all discrepancies before transfer of accountability.** No joint inventory is needed when the alternate replaces the primary for **30 consecutive calendar days or less**; if it is known the absence will exceed 30 days, **the alternate is appointed primary on orders and a joint inventory is conducted**. Death, extension beyond 30 days, sudden illness or emergency transfer also trigger a joint inventory, conducted by the alternate and a person appointed by the commander. | Applies. |
| **3-2e** | **Inventories in case of lost evidence or breach of security** are conducted **by the person assigned to conduct the inquiry**, in the **presence of the primary or alternate evidence custodian**. | Applies. |
| **3-2f** | **Sealed containers of fungible or other sealed evidence will not be breached for any type of inventory** unless directed by the responsible supervisor. If breached, the evidence is resealed in a new container and the **supervisor directing the breach prepares an MFR** attached to the corresponding DA Form 4137. | Applies. |
| **3-2g(3)** | Change-of-custodian inventories are entered in the ledger and **signed by both incoming and outgoing primary custodians**, with the prescribed statement including *"Any discrepancies have been resolved to my satisfaction."* | Applies. |
| **3-2g(4)** | On satisfactory completion of the change-of-custody inventory, **each DA Form 4137 in the document files is annotated and signed** to show the change of custody; suspense copies are checked for registered mail receipt numbers and proper signatures. | Applies. |
| **3-2g(5)** | On death or inability of the primary custodian to sign, the **Released By** block is annotated **"N/A Custodian Unable to Sign"**; the alternate completes **Received By**; the Purpose block shows why. | Applies. |
| **3-2g(6)** | Results of an inventory conducted for loss or breach are recorded **in the evidence ledger and in the report of inquiry**. | Applies. |

**Inventory observation categories implied by the regulation.** AR 195-5 does not enumerate a
list of per-item observation outcomes. It does establish the concepts EMC needs:

- **Physically verified** — 3-2b(1)(b) "conduct a physical count of evidence to verify that
  evidence in the evidence room corresponds with that shown on DA Form 4137"; 3-1b(2) "all
  evidence was properly accounted for".
- **Properly on temporary release** — 3-2b(1)(d) requires suspense-file copies to be checked and
  properly annotated; 3-1a(4) requires checking that release has not been excessive. Evidence
  legitimately out of the room is *accounted for*, not missing.
- **Sealed container verified without breach** — 3-2f; and 3-2b(1) *"will not ask the evidence
  custodian to verify the weight of any drug or controlled substance evidence but rather, will
  ensure that the number of containers listed on DA Form 4137 ... is correct and that any seals
  on any containers are intact."*
- **Discrepancy / cannot be located** — 3-3a.
- **Unexpected / on hand but recorded as disposed** — App B-4e(4) *"Were there any items of
  evidence still in the evidence room that had been documented as having been disposed of?"*
- **Exceptions** — 3-1b(2) and 3-2g(1) require the certification to state *"with (no exceptions)
  or (the following exceptions)"*.

The specific five-state vocabulary used in EMC is **[DESIGN]** built on these paragraphs.

### 7.3 Inquiries — the one hard deadline in the regulation

| Ref | Provision |
|---|---|
| **3-3a** | **"If during an inspection or inventory of an evidence room an item(s) of evidence cannot be located, the evidence custodian and the ... CI supervisor, as appropriate, will have up to 5 working days to try to resolve the problem, before an official inquiry is initiated."** The apparent loss could be misplacement or a documentation gap. **"If the problem cannot be resolved by the end of the 5th working day, an inquiry will be initiated."** Any corrective actions **will be fully documented in an MFR attached to the appropriate DA Form 4137.** |
| **3-3b** | If evidence is **lost** or the room's **security is breached**, an **inventory** is conducted and an **inquiry or investigation is performed in accordance with AR 15-6**, initiated by the appropriate commander. **All losses or breaches of security and the start of inquiries will be reported** — for CI, to **DCS, G-2 (Army G-2X), 1000 Army Pentagon, Washington, DC 20310-1000**. |
| **3-3c** | If the inquiry **fails to account for or recover** the evidence, **relief for accountability must be granted**. **For CI units, relief is granted by the Army G-2X.** Relief from further accountability: **(1) permits the closure of the DA Form 4137**; **(2) has no bearing on administrative or judicial action** against those responsible. |
| **3-3d** | If evidence appears missing **on receipt of a packaged parcel**, the CI supervisor is **notified immediately**; on verification, **the sender is notified immediately** and asked to search; if not located, an inquiry is conducted per AR 15-6. |

**Consequences:**

- **`LOSS-002`:** 5 **working** days is the only numeric deadline AR 195-5 states for this
  workflow. It is measured in *working* days, which requires a duty calendar. See **AMB-02**.
- **`LOSS-005`:** *Relief for accountability granted* is a real terminal state named by the
  regulation, and it is what permits DA Form 4137 closure. EMC's state model must include it.
- **`LOSS-003`:** reporting to Army G-2X is a regulatory obligation EMC should prompt for and
  record — it must not claim to *perform* the report.

---

## 8. Temporary release and suspense

| Ref | Provision |
|---|---|
| **2-7a** | Evidence is removed from the evidence room **only** for permanent disposal or for temporary release for specific reasons. **"When evidence is temporarily released, the evidence custodian will maintain reasonable and adequate contact with the person or agency which temporarily receipted for the evidence."** This ensures accountability is maintained and that it is **returned as soon as it is no longer needed**. Common reasons: **(1) transmittal to a crime laboratory for forensic examination; (2) presentation at a criminal trial, grand jury proceeding or an Art. 32 hearing**. |
| **2-7b** | The recipient **physically inventories the evidence and signs the "Received By" column** of the Chain of Custody section on the **original and first copy**. The releaser **clearly informs** the recipient that they must safeguard the evidence, maintain the chain of custody until return, and return it as soon as it is no longer needed. The **original DA Form 4137 goes with the evidence**; the **first copy goes in the suspense folder**. **"The evidence custodian and supervisors of the evidence custodian will ensure the evidence is not released for an excessive period."** A recipient **presents appropriate identification**. When items on the same form go to more than one agency at the same time, **copies are used** and a note is made on the original and first copy. |
| **2-7e** | When evidence is mailed to USACIL it is sent by **registered or other accountable mail**. **"The evidence custodian will only enter the registered or other accountable mail number in the Received by block of the chain of custody section of the DA Form 4137."** On receipt USACIL records the number in the **Released by** block. |
| **2-7f** | Commercial accountable shipping must maintain a chain of custody with safeguards consistent with registered mail. |
| **2-7g** | Permanent transfer between evidence rooms: original and duplicate annotated forms accompany the evidence; the receiving custodian enters the next document number of the receiving room on both copies; the prior number is **lined through so that it remains legible**; the sending custodian files a copy showing disposition in the inactive file. |
| **2-7i** | US Government property and .0015 fund evidence may be temporarily released to a **non-DA law enforcement agency only after it has been processed into the evidence room** and with the approval of the appropriate CI commander/SAC. A suspense copy is maintained until return. |
| **2-7j** | Child-exploitation imagery **will not** be released to defence counsel **without an order from a judge**; viewing is permitted with trial counsel approval, in the presence of the agent, on a **standalone computer not connected to a network or the Internet**. |
| **2-7k** | Classified evidence is released **in accordance with AR 380-5**. **[REG-REF]** |
| **3-1a(4)** | The monthly inspector determines whether evidence on temporary release **"has not been so released for an excessive period."** |
| **App B-4c(11)** | Internal control: *"Were suspense folders periodically checked to ensure items of evidence had not been released for an excessively long period of time?"* |

**Critical negative finding (`SUSP-004`).** AR 195-5 states the standard as **"not ... an
excessive period"** and **"reasonable and adequate contact."** It **does not define a number of
days** for any temporary-release category. EMC therefore:

- computes and displays **days out** and lets the organization configure **local review
  thresholds**;
- labels those thresholds unmistakably as **local management thresholds**, never as an
  AR 195-5 deadline;
- cites 2-7a, 2-7b and 3-1a(4) as the reason the aging view exists.

**Suspense categories are named by the regulation** (2-4f(3), and the glossary entry *Evidence
custodian document suspense files*): `USACIL`, `ADJUDICATION`, `PENDING DISPOSITION APPROVAL`.
EMC uses these exact names and treats "other authorized temporary release" as a **[DESIGN]**
catch-all clearly distinguished from the three regulatory folders.

---

## 9. Disposition

| Ref | Provision |
|---|---|
| **2-8** (opening) | Property seized or held as evidence, other than contraband or property that cannot legally be returned, **is returned to its rightful owner** when it has no evidentiary value or when criminal proceedings have concluded and the time to initiate appeals has passed. **"All final disposition of evidence actions will be documented in appropriate hard copy investigation case files and online database case records in addition to on the DA Form 4137."** **Coordination with the servicing SJA office must occur prior to disposition of evidence.** |
| **1-4h(5)** | The custodian ensures the **Final Disposal Action** and **Final Disposal Authority** areas of the DA Form 4137 are completed **before it is signed by the approval authority**. |
| **2-8a(1)** | Items determined by the agent to have **no evidentiary value** may be disposed of **before** release to the custodian; the CI supervisor reviews the DA Form 4137 and approves by completing the **final disposal authority** section. |
| **2-8a(2)** | When it is impractical to keep items (vehicles, serial-numbered items, items needed by the owner, postal items, large amounts of money, explosives, perishable or unstable items) disposal may be immediate, coordinated with trial counsel; oral permission followed by written signature as final disposal authority is acceptable. |
| **2-8b(1)** | Items determined **by laboratory analysis** to be of no evidentiary value: disposal authority from trial counsel/civilian prosecutor in **known subject** cases, or from the CI commander/SAC in **unknown subject** cases. |
| **2-8c(1)** | Evidence in a **closed unfounded** investigation: CI commander/SAC reviews and approves. |
| **2-8c(2)** | Evidence in an **unsolved** investigation: trial counsel approves; **or** the CI commander/SAC may approve **without trial counsel approval 3 months after completion of the investigation**. |
| **2-8c(2)(a)** | **Evidence involving unsolved homicide, rape, sexual assault, undetermined death, and missing person cases, and any other offense with no statute of limitations, will be retained indefinitely.** |
| **2-8c(2)(c)** | Other serious unsolved cases may be retained indefinitely; a **memorandum explaining the reason for retention** is maintained in the case file. |
| **2-8c(3)** | Permanent release to a non-DA law enforcement or intelligence agency: final disposal authority completed by the CI commander/SAC. |
| **2-8e(4)** | Evidence released to trial counsel is returned as soon as it is no longer required, **unless it is entered as a permanent part in the record of trial** — in which case trial counsel immediately notifies the custodian so the DA Form 4137 can be annotated, and **this is considered final disposition**. |
| **2-8e(5)** | In **known subject** cases the **original DA Form 4137** is sent to trial counsel or the civilian prosecutor, who reviews and approves disposal by completing the **Final Disposal Authority** section. Where there is a high risk of losing the original, a letter/memorandum/email may be used with a copy attached; the approving correspondence is attached to the DA Form 4137. **Where more than one authority must authorize disposition on a single DA Form 4137, a continuation sheet containing the Final Disposal Authority verbiage is used and attached.** |
| **2-9** (opening) | Evidence is **expeditiously disposed of** after it has served its purpose. **A witness to destruction physically views the item(s) designated for destruction — not just the container.** Disposal by registered mail uses **PS Form 3811**, attached to the DA Form 4137 on return. |
| **2-9c** | Controlled substances destroyed **in the presence of a witness** (SA, NCO E-6 or above, or civilian GS-07 or above) **who is not in the chain of custody**. An alternate custodian who ever took control of the room while the evidence was in it **is considered to be in the chain of custody and is ineligible**. |
| **2-9d** | Counterfeit currency and counterfeiting equipment released to the **US Secret Service**. |
| **2-9r** | Classified items disposed of **in accordance with AR 380-5**. **[REG-REF]** |
| **2-15a** | Unrestricted sexual assault: physical and forensic evidence (less the SAFE kit) **must be retained for 5 years from the date of the seizure of evidence** (Section 586, Public Law 112-18 as cited in the regulation); after that period and the conclusion of all legal, adverse action and administrative proceedings, released per 2-9. |

**Consequences (`DISP-001` .. `DISP-008`):**

1. **Disposition is item-level.** 2-5b(1)(d) explicitly contemplates different disposition dates
   for different items on one DA Form 4137, and 2-4h defers voucher closure until *all* items
   are disposed. This is regulatory support for the design, not merely a convenience.
2. **Disposition is a multi-actor workflow, not a boolean.** The regulation separates:
   *coordination with SJA* (2-8 opening) → *identification of the correct approving authority*
   (2-8a-c, 2-8e, which authority depends on case posture) → *Final Disposal Authority signature
   on the DA Form 4137* (1-4h(5), 2-8e(5)) → *Final Disposal Action* (2-9) → *documentation in
   the case file and online database case records* (2-8 opening, 2-9 opening).
3. **Different items on one voucher may be approved by different authorities** — 2-8e(5)
   provides a continuation sheet precisely for that case.
4. Several **retention rules block disposition**: 2-8c(2)(a) indefinite retention, 2-15a five
   years. These are `RET-*` requirements.

---

## 10. Long-term retention (para 2-13)

| Ref | Provision |
|---|---|
| **2-13a** | Items are packed in boxes or crates by the **evidence custodian in the presence of a witness who is not in the chain of custody**. |
| **2-13b** | A **certificate/memorandum** is prepared **listing DA Form 4137 numbers included in the box**. The certificate reflects that the contents, **identified by specific document number and by citing the absence of specific item numbers**, were inventoried and sealed on the date indicated by the custodian and **witnessed by a disinterested witness (an individual not within the chain of custody)**. It is **signed by the evidence custodian and the disinterested witness**. A copy is attached to **each** DA Form 4137 identified on it, the original to the **first** form listed, and a copy is **affixed to the outside of the box**. **"The DA Form 4137 concerned will continue to be maintained in the active DA Form 4137 file."** |
| **2-13c** | **Firearms will not be stored or sealed in the consolidated evidence box.** |
| **2-13d** | The box is sealed so tape is damaged if opened; **signatures of the custodian and witness written in permanent ink across the tape seal** on top and bottom. **"The box or crate will not be opened to conduct inventories, unless tampering is evident or a competent authority so directs."** |
| **2-13f** | Evidence may be sent to a designated consolidated or long-term evidence room/facility established by the appropriate SAC or commander. |

**Consequence (`RET-004`).** The regulation's own model is exactly the one requested: a
long-term container references **document numbers included** *and* **item numbers excluded**, and
the underlying DA Forms 4137 stay **active**. The container is packaging, not a new item of
evidence. Modelling it as a new item would contradict 2-13b directly. Note also the interaction
with inventories: 2-13d means items sealed in a long-term box **cannot** be given a "physically
verified by sight" observation during a 100 percent inventory — the correct observation is
verification of the sealed container's integrity.

---

## 11. Storage, security, and classified evidence

| Ref | Provision |
|---|---|
| **4-1** | An evidence room is a structure, room or vault meeting the standards of chapter 4. The same procedures apply to consolidated and long-term rooms/facilities. |
| **4-1a** | **Routine office classified documents will not be stored in the evidence room. Only classified information determined to be evidence of a crime will be stored in an evidence room.** All containers used to store classified information must meet the security standards in **AR 380-5**. **The primary and alternate evidence custodians are required to have the necessary level of security clearance.** **[REG-REF]** |
| **4-1c** | Property that is not evidence will not be stored in the evidence room. |
| **4-1d** | Activities with insufficient evidence volume may use a **depository**: a **GSA-approved safe**, located in a **locked, controlled-access room**, with **all other administrative and accountability requirements of the regulation met**. |
| **4-2a(2)** | **CI units will store evidence in accordance with AR 381-20.** **[REG-REF]** |
| **4-3a** | **Temporary evidence facility**: a safe or secure filing cabinet for temporary storage during non-duty hours pending release to the custodian. **Access restricted to the person securing it.** Key-opened padlock; combination locks not permitted for this purpose. |
| **4-3c** | Separate building, room or fenced enclosure for unusually large items or large amounts of property. |
| **4-4a** | The evidence room is **locked at all times when not occupied** by the primary or alternate custodian. Authorized personnel have access **only when accompanied by the responsible custodian**; personnel are **never left in the evidence room without the custodian**. |
| **2-6h / 2-7k / 2-9r** | Classified evidence is **stored, released and disposed of in accordance with AR 380-5**. **[REG-REF]** |

**Consequence.** AR 195-5 delegates classified handling entirely to AR 380-5 and AR 381-20.
EMC must **not** invent classified-handling requirements. What it *must* do is avoid becoming an
unaccredited aggregation point for classified text. See the classification design boundary in
`docs/architecture.md` §9 and the open decision **DEC-06**.

---

## 12. Where AR 195-5 does *not* support a "requirement" EMC might be assumed to have

This section exists to prevent the most likely form of regulatory misstatement in this project.

| Claim EMC must **not** make | What the regulation actually says |
|---|---|
| "AR 195-5 requires a complete location history." | **2-4e** requires the current location **in pencil**, kept current **by erasing the previous entry**. Location *history* is **[DESIGN] + [CONTROL]** — valuable, defensible, and *not* mandated. |
| "AR 195-5 sets a deadline of N days for laboratory / adjudication / disposition-approval turnaround." | **2-7a/2-7b/3-1a(4)** require *reasonable and adequate contact* and that release not be for an *excessive period*. **No number is given.** Any threshold is a **local management threshold**. |
| "AR 195-5 requires electronic records to be append-only." | **2-5b(5)** requires that erroneous **ledger** entries remain readable and be corrected by strike-through and initials, and **1-7c(3)** requires an MFR and supervisor notification. EMC's append-only store is modelled on these; it is **[DESIGN] + [CONTROL]**. |
| "AR 195-5 requires the application to assign document numbers." | **2-4c** requires the **custodian** to assign the number **by order of precedence from the evidence ledger**. Until Army G-2X approves an automated equivalent (**2-5c**), the application must not. |
| "AR 195-5 requires OCR verification workflows / confidence levels." | The regulation is silent on OCR. Everything in the ingestion subsystem is **[DESIGN] + [CONTROL]**, justified by **2-3g** (custodian reviews and corrects the form) and **2-5b(5)** (originals are preserved). |
| "AR 195-5 requires CI quarterly inventories." | **3-2b** exempts CI units from the quarterly-inventory paragraph. What CI **does** have is a **monthly 100 percent joint inventory** under **3-1b(2)**. See **AMB-01**. |
| "An electronic signature in EMC satisfies AR 195-5." | The regulation requires **handwritten** signatures for ledger entries (**2-5b(2)**), custody transfers (**2-7b**), certifications (**3-1b(2)**, **3-2g**) and the Final Disposal Authority (**2-8e(5)**). In V1 an EMC attestation is a **record that a paper signature was executed**, not a signature. |
| "AR 195-5 defines 'working day'." | It does not. **2-4a** and **3-3a** both turn on working days. See **AMB-02**. |

---

## 13. Ambiguities requiring a policy decision from the organization

These are recorded in full, with recommended defaults, in `docs/open-policy-decisions.md`.
Summarised here because they are ambiguities *in the regulation as applied to CI*, not merely
product choices.

| ID | Ambiguity |
|---|---|
| **AMB-01** | **Does the CI exemption in 3-2b reach 3-2a(1)?** 3-2a(1) states inventories will be conducted "once in each calendar quarter". 3-2b is titled *Quarterly inventories* and its parenthetical exempts CI units from "the requirements of **this paragraph**" — whose subparagraphs are the disinterested-officer inventory (3-2b(1)) and the reverse inventory (3-2b(2)). It is genuinely unclear whether CI units are exempt from the *quarterly cadence* itself, or only from the two *methods* prescribed in 3-2b. Because 3-1b(2) already imposes a **monthly** 100 percent joint inventory on CI — a strictly higher standard — the practical effect is small, but the ledger certification wording differs. |
| **AMB-02** | **"Working day" is undefined** for the 2-4a turn-in expectation and the 3-3a 5-working-day inquiry clock. Federal holidays, local training holidays, and deployed schedules all change the answer, and 3-3a is a real deadline with real consequences. |
| **AMB-03** | **Scope of the document-number series.** 2-4c defines the series per calendar year; 2-7g shows it is per evidence room. Whether one EMC instance serves one CI evidence room or several determines uniqueness scoping, access control, and inventory population. |
| **AMB-04** | **What counts as one item.** 2-1b permits grouped items ("a box containing tools") to be listed as one item with one DA Form 4002, while 2-2f requires separately-numbered items to be sealed separately. The item is therefore the numbered line on the form, whose quantity may be greater than one. Confirm this reading before it is baked into inventory counts. |
| **AMB-05** | **Alternate-custodian authority window.** 1-4i defines temporary absence as more than 1 working day and not more than 30 consecutive days; 3-2d turns on 30 consecutive **calendar** days; 1-7c(2) speaks of 30 **calendar** days. Whether EMC should hard-block an alternate from acting on day 31, or warn, is a policy call. (The separate question of whether an alternate may act *at all* without an assumption of duties is **not** ambiguous — 1-4i answers it, and EMC blocks.) |
| **AMB-06** | **Whether EMC records may hold restricted-reporting sexual assault data at all** (2-16), and if so under what access control. 2-5b(1)(c) requires the ledger description to read *"Restricted Sexual Assault"*. Unlikely in a CI context but must be answered, not assumed. |
| **AMB-07** | **Records status of EMC's own data.** 1-5 places records management under ARIMS/RRS-A. If EMC's database is itself an Army record, its retention and disposition are governed by RRS-A — a determination only the organization's records manager can make. 2-4h (3 years after inactive) and 2-5a (3 years after last disposal) govern the *paper*. |

---

## 14. Provisions deliberately out of scope for this application

Recorded so that their absence is a decision, not an oversight.

| Ref | Subject | Why out of scope |
|---|---|---|
| **2-2, 2-14, ch. 4 (construction), ch. 5 (packaging/shipping)** | Physical packaging, sealing technique, protective clothing, wall construction, lock specifications, USACIL shipping mechanics | Physical procedures. EMC records *that* they occurred and their attributes; it does not instruct or enforce them. |
| **2-10** | Federal Grand Jury materials (Fed. R. Crim. P. 6(e)) | Distinct handling and access regime; requires its own design pass and legal review. Flagged, not modelled, in V1. |
| **2-11, 2-12** | Controlled substances for training; field testing | Not a CI evidence-room companion concern in V1. |
| **2-15, 2-16** | Sexual assault reporting procedures | 2-15a's 5-year retention rule is modelled as a retention constraint (`RET-002`); the reporting workflows are not. See **AMB-06**. |
| **5-1 – 5-5** | USACIL submission mechanics | V1 records a USACIL temporary release and its suspense; it does not generate DD Form 2922 or manage shipping. |
| **App B** | Internal control evaluation checklist | Used as a **source of verification questions** for inspection support (see `INSP-*`), not implemented as an automated compliance score in V1. |

---

## 15. Forms referenced

| Form | Role in AR 195-5 |
|---|---|
| **DA Form 4137** | Evidence/Property Custody Document. The custody document. Computer-generated version authorised (2-3a). |
| **DA Form 4002** | Evidence/Property Tag. One per item or container; carries the case control number and, once assigned, the document number (2-1b, 2-4c). |
| **DD Form 2922** | Forensic Laboratory Examination Request (2-6d, 2-7c(3)). |
| **DD Form 281** | Voucher for Emergency or Extraordinary Expense Expenditures — .0015 funds (2-3n). |
| **SF 700** | Security Container Information — spare keys and combinations (3-1a(2)). |
| **PS Form 3811** | Domestic Return Receipt — registered mail disposal (2-9 opening). |
| **DA Form 4283** | Facilities Engineering Work Request — waiver requests (1-6d(2)(a)). |

**AR 195-5 prohibits the establishment of command and local forms without prior approval from
the Director, U.S. Army Criminal Investigation Division** (Supplementation, title page). EMC
therefore **must not** create a substitute local evidence form. Its printed outputs are either a
compliant DA Form 4137 (once that capability is built and validated) or clearly-labelled
**working aids** that are not custody documents.
