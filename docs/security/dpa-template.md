# Data Processing Agreement — TEMPLATE

> **This is a template, not legal advice.** It is a generic starting point for an operator of a self-hosted
> AgentOS.NET deployment who processes personal data on behalf of customers (for example, a company hosting
> AgentOS.NET for other teams or clients). The AgentOS.NET open-source project is **not** a party to it and
> processes no data from your deployment. Have qualified counsel adapt it to your jurisdiction (e.g. GDPR / UK
> GDPR, CCPA), your contracts and your actual configuration before use. Text in `[brackets]` must be replaced;
> the technical annexes point to [subprocessors.md](subprocessors.md), [data-flow.md](data-flow.md) and
> [hardening.md](hardening.md) — describe only the measures **you have actually enabled**.

---

**This Data Processing Agreement** ("DPA") is entered into between:

- **[Customer legal name]**, [address] ("**Controller**"), and
- **[Operator legal name]**, [address] ("**Processor**"),

and forms part of the [Master Services Agreement / Terms of Service] dated [date] (the "**Agreement**").

## 1. Definitions

Terms such as *personal data*, *processing*, *controller*, *processor*, *sub-processor*, *data subject* and
*personal data breach* have the meaning given in [Applicable Data Protection Law, e.g. Regulation (EU) 2016/679].
"**Service**" means the AgentOS.NET deployment operated by Processor at [URL / environment] as described in the
Agreement.

## 2. Subject matter, duration, nature and purpose

| Item | Description |
|---|---|
| Subject matter | Provision of the Service: AI-assisted software delivery (requirements, code, tests, review) on Controller's tickets and repositories |
| Duration | The term of the Agreement plus the return / deletion period in § 10 |
| Nature of processing | Storage, retrieval, transmission to the configured LLM provider(s) and source-control systems, generation of derived artifacts |
| Purpose | Performing the Service for Controller; no other purpose |

## 3. Categories of data subjects and personal data

- **Data subjects:** Controller's users of the Service (members, admins); persons named in Controller's tickets,
  repositories, commit history or uploaded files; recipients of invitations.
- **Personal data:** user name, e-mail address, role and tenant membership; sign-in and audit events (actor,
  time, action, and where captured IP address); any personal data contained in ticket text, source code,
  repository content, attached files or agent output.
- **Special categories:** none intended. Controller shall not submit special-category data unless agreed in
  writing in [Annex / clause].

## 4. Controller's instructions

Processor processes personal data only on Controller's documented instructions, including the configuration
Controller makes in the Service (choice of LLM provider per agent, connected repositories, tool allowlist,
budget caps). Processor informs Controller if an instruction appears to infringe Applicable Data Protection Law.

## 5. Confidentiality

Processor ensures that persons authorised to process personal data are bound by confidentiality.

## 6. Security of processing

Processor implements the technical and organisational measures in **Annex 2**, taking into account the state of
the art, costs, and the nature, scope, context and purposes of processing. Processor may update the measures if
the overall level of protection is not reduced.

## 7. Sub-processors

1. Controller gives [general / specific] authorisation to the sub-processors listed in **Annex 3**.
2. Processor notifies Controller of an intended addition or replacement at least [30] days in advance; Controller
   may object on reasonable data-protection grounds within [14] days.
3. Processor imposes data-protection obligations on each sub-processor equivalent to this DPA and remains liable
   for its sub-processors.
4. Where Controller itself selects a provider in the Service using **Controller's own account and API key**
   (for example its own Anthropic or Azure OpenAI key, or members' own CLI subscriptions through a paired
   runner), [state whether that provider is Controller's own processor rather than Processor's sub-processor].

## 8. International transfers

Personal data is transferred outside [EEA / UK / jurisdiction] only under a valid transfer mechanism
[e.g. Standard Contractual Clauses, adequacy decision]. Processing regions: [Azure region of the deployment];
[LLM provider regions].

## 9. Assistance, data-subject requests and breaches

1. Processor assists Controller, by appropriate technical and organisational measures, in responding to
   data-subject requests (access, rectification, erasure, portability). The Service provides tenant export and
   tenant purge (platform administrator) for this purpose.
2. Processor notifies Controller of a personal data breach without undue delay and in any case within [48] hours
   of becoming aware, with the information required by [Art. 33(3) GDPR] as it becomes available.
3. Processor assists with data-protection impact assessments and prior consultations where required.

## 10. Return and deletion

At the end of the Agreement, Processor shall, at Controller's choice, return Controller's data (tenant export)
and/or delete it (tenant purge, then expiry of backups within [backup retention, e.g. 7-35 days]), unless law
requires storage. Processor confirms deletion in writing on request.

## 11. Audits

Processor makes available the information necessary to demonstrate compliance and allows for audits by Controller
or an auditor mandated by it, with [30] days' notice, at most [once per year] unless a breach occurred, subject to
confidentiality. Processor may satisfy this with [independent reports / certifications] where available.

## 12. Liability and precedence

Liability is governed by the Agreement. In case of conflict on data protection, this DPA prevails.

**Signatures**

| Controller | Processor |
|---|---|
| Name: | Name: |
| Title: | Title: |
| Date: | Date: |

---

## Annex 1 — Processing details

Fill in from § 2-3 and from [data-flow.md](data-flow.md) § 4-5 (what crosses each boundary and data classes).

## Annex 2 — Technical and organisational measures (select what you enabled)

Each line maps to a control in the product or the hardening checklist. Delete the ones you have not enabled.

| Area | Measure | Reference |
|---|---|---|
| Access control | Single sign-on through Keycloak (OIDC code flow + PKCE), role-based access (member / admin / platform-admin), [MFA enforced for administrators] | [threat-model.md § 4](threat-model.md#4-tb2--web--api--keycloak); [hardening.md § 1](hardening.md#1-identity--keycloak) |
| Tenant isolation | Tenant id on every row; application filters and Postgres row-level security bound to the caller's tenant | [ADR-0012](../adr/0012-postgres-row-level-security.md) |
| Encryption in transit | TLS on all external endpoints; [TLS to the database] | [hardening.md § 2](hardening.md#2-tls-and-the-edge) |
| Encryption at rest | Managed storage encryption of the database; credentials encrypted with ASP.NET Core DataProtection; [key ring wrapped with Key Vault / certificate] | [hardening.md § 3](hardening.md#3-dataprotection-key-ring) |
| Least privilege for AI agents | Tool calls pass a per-tenant allowlist and are recorded as evidence; generated code builds only in an isolated, network-less container; runner work uses scoped autonomy | [threat-model.md § 11](threat-model.md#11-tool-policy-and-evidence-itoolgateway) |
| Logging and audit | Append-only audit of settings, member lifecycle, invitations, sign-in, exports; secrets never recorded | [SECURITY.md](../../SECURITY.md) |
| Availability | Automated database backups [retention, geo-redundancy]; documented key-ring recovery | [dataprotection-dr-runbook.md](../dataprotection-dr-runbook.md) |
| Data minimisation | Only content needed for the requested task is sent to the LLM provider; [provider zero-data-retention enabled] | [subprocessors.md](subprocessors.md) |
| Vulnerability management | Dependency scanning, CodeQL, pinned CI actions; private vulnerability reporting | [SECURITY.md](../../SECURITY.md) |

## Annex 3 — Sub-processors

Build this list from [subprocessors.md](subprocessors.md), including **only** the services enabled in your
deployment.

| Sub-processor | Service | Data | Location | Transfer mechanism |
|---|---|---|---|---|
| [e.g. Microsoft Ireland Operations Ltd] | [Hosting — Azure Container Apps, PostgreSQL, Key Vault] | [All Service data] | [Region] | [SCCs / n/a] |
| [e.g. Anthropic PBC] | [LLM inference — Claude] | [Prompts: ticket text, code] | [Region] | [SCCs] |
| [e.g. GitHub, Inc.] | [Source control] | [Code, PRs, issue text] | [Region] | [SCCs] |
| [SMTP provider] | [Transactional e-mail] | [E-mail address, invitation link] | [Region] | [ ] |
