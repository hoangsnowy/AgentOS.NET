# Integrations — where code and tickets come from

A **board** (Board › Projects) has two sides:

| Side | Options | What it does |
|---|---|---|
| **Code in** | GitHub · Azure Repos | The repositories the agents clone, branch and open pull requests on. |
| **Tickets from** | GitHub Projects · Azure DevOps Boards · Jira | Where Autopilot finds work, reads the description and the people's decisions, and reports back. |

GitHub Projects tickets need GitHub code (the default, unchanged). Azure DevOps Boards and Jira are
**trackers** (E1.8): the board keeps its code host, and its tickets come from the tracker instead.

> Pull requests are opened on GitHub today. A board whose code is in Azure Repos reads its repositories and
> its Azure DevOps Boards tickets, but Autopilot's pull request flow still needs GitHub code (plan 0.10).

## How a tracker board works

- **Ticket numbers.** A work item id (Azure DevOps, shown `AB#42`) or the number of a Jira key in the board's
  project (`SHOP-12` → 12). One Jira project per board keeps the numbers unique; an issue moved to another
  project leaves the board.
- **Which repository.** A tracker knows nothing of repositories: a ticket labelled / tagged
  `repo:<name>` (or `repo:<owner>/<name>`) is built there, any other in the board's first repository. A ticket
  naming a repository the board doesn't have is skipped, never built in the wrong place.
- **Approval and priority.** As on GitHub: the label / tag `ai:ready` lets Autopilot pick a ticket, `p0` / `p1`
  order them, `Depends on #11` in the description waits for ticket 11 to be done. A Jira issue type becomes a
  `type:` label (a Bug is a `fix:` pull request).
- **Done.** Jira: the status category *Done* (or the board's merge transition target). Azure DevOps: *Closed*,
  *Done*, *Completed*, *Removed* or the board's merge state.
- **The discussion.** The agents read the ticket's newest comments. A comment counts as a **maintainer
  decision** (it overrides the description, newest wins) when its author holds a project role:

  | Tracker | Maintainer (`PROJECT_ADMIN`) | Maintainer (`PROJECT_MEMBER`) | Everyone else |
  |---|---|---|---|
  | Azure DevOps | *Project Administrators* | *Contributors* | discussion |
  | Jira | *Administrators* (team-managed: *Administrator*) | *Developers* (team-managed: *Member*) | discussion |

  Groups are expanded (nested ADO groups; Jira groups in a role, the first five). Bots (Jira apps, ADO build
  service identities) and AgentOS.NET's own signed comments are left out. If the token can't read the roles,
  every comment is discussion — never the other way round — and the task's feed says so.
- **Write-back.** When a run starts, the ticket moves to the board's *start* state / transition; the pull
  request names the ticket (`feat: Add coupons (SHOP-12)`, body `Ticket: [SHOP-12](…)` — never `Closes #12`,
  which would close an unrelated GitHub issue) and is linked on the ticket (Jira remote link, ADO hyperlink);
  questions for a person are posted as comments; on merge the ticket moves to the *done* state. These writes are
  best-effort: a tracker hiccup is logged, never fails the run.

## Azure DevOps Boards

1. Create a PAT (User settings → Personal access tokens) with **Work Items — Read & write** and
   **Identity — Read** (for the maintainer roles). With Azure Repos code in the same organization, one PAT with
   **Code — Read & write** as well serves both: leave *Boards PAT* blank.
2. Board › Projects → *Tickets from: Azure DevOps Boards*, then:
   - **Boards organization / project** — `contoso` / `Shop` (the name, not the URL).
   - **Area path / Iteration path** — optional, `UNDER` match (`Shop\Checkout`); the iteration may be
     `@CurrentIteration` or `@CurrentIteration('[Shop]\Shop Team')`.
   - **States / Tags** — optional, comma separated; all tags must be on a work item.
   - **State when work starts / merged** — default `Active` / `Closed` (Agile). Scrum: `Committed` / `Done`;
     Basic: `Doing` / `Done`. Leave one empty to never move.
3. *Connect* validates the project **and runs the query** (a wrong area path fails now, not at the first pass).

New tickets from *New idea* are created as **Task** work items in the board's area / iteration, tagged.

## Jira Cloud

1. Create an API token at https://id.atlassian.com/manage-profile/security/api-tokens for the account the
   agents act as (it needs *Browse projects*, *Add comments*, *Transition issues*, *Link issues*; *Administer
   Projects* lets it read the roles for the maintainer rule).
2. Board › Projects → *Tickets from: Jira*, then:
   - **Jira site** — `https://contoso.atlassian.net` (any `*.atlassian.net` is allowed; Jira Data Center needs
     its host under *Settings › Platform › Workspaces: allowed hosts*).
   - **Project key** — `SHOP`.
   - **JQL** — optional, ANDed with the project (`labels = "ai:ready" AND sprint in openSprints()`); its own
     `ORDER BY` is dropped. Keep done issues in the result if tickets use *Depends on*.
   - **Transition when work starts / merged** — a transition name or the status it leads to; default
     `In Progress` / `Done`.
   - **Account e-mail + Jira API token** — HTTP Basic (`e-mail:token`).
3. *Connect* validates the project **and runs the JQL**.

New tickets from *New idea* are created as **Task** issues; labels lose their spaces (Jira forbids them).

## Security

- Tokens are validated, then stored **encrypted** in AppConfig, per tenant: the code token under
  `workspace/<id>/token`, the tracker token under `workspace/<id>/tracker-token`. The board row
  (`workspaces.Tracker`, JSON) holds only coordinates, filter and states — never a token. Removing the board
  deletes both.
- Tenant-supplied hosts go through the SSRF-hardened HTTP handler (no private / loopback / link-local
  addresses, also after redirects) and the allowed-hosts policy. A URL returned by Jira (role links) is never
  followed — only its id is read.
- WIQL and JQL values are quoted and escaped; `@CurrentIteration` is accepted only in its exact forms.
- Ticket text is third-party input: descriptions and comments are fenced as untrusted data in every prompt.

## Under the hood

`ISourceProvider` gained ticket writes (`CommentOnTicketAsync`, `MoveTicketAsync`, `LinkPullRequestAsync`). The
resolver wraps each code host in `TrackerRoutingSourceProvider`: for the ticket members it asks `IBoardTrackers`
(Workspaces) whether the board has a tracker and, if so, calls `AzureDevOpsSourceProvider` /
`JiraSourceProvider` with the tracker's token and binding; repository and pull request members always stay on
the code host. Callers (Autopilot, the Board, the agents) didn't change. Real-service tests: `docs/LAB.md`.
