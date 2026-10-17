# Project skills — AgentOS.NET

Custom skills shipped with the repo. Loaded automatically by every Claude Code session opened in this project.

`.claude/skills` is the single source for these skills — the old `.agents/skills` mirror has been removed.

| Skill | Trigger | When |
|---|---|---|
| **agent-scaffold** | `/agent-scaffold X`, "scaffold agent X" | Add a new pipeline agent (`AgentOs.Modules.Pipeline.Agents.{Name}Agent`). Generates contract + impl + DI registration + xUnit test stub. |
| **plugin-scaffold** | `/plugin-scaffold X`, "scaffold a plugin" | Scaffold a runtime-discovered `IAgentOsPlugin` (dropped in `plugins/`, no host reference). Generates the project + manifest + a contributed tool / provider / window. |
| **prompt-tune** | `/prompt-tune {Name}Agent`, "tune prompt for X" | A/B-test prompt variants over an eval fixture set. Reports pass-rate / JSON-valid / token diff. |
| **cost-report** | `/cost-report week`, "weekly cost" | Aggregate structured logs → xlsx + markdown cost report grouped by agent / provider / model / date. |
| **design-review** | `/design-review`, "design check" | Enforce the design system on Blazor UI; phantom-CSS detector + vocabulary mapping. |

## Format

Each skill = directory with `SKILL.md` (YAML frontmatter `name` + `description` + body). Claude Code reads them automatically when the project root is opened.

## Edit

Edit `SKILL.md` directly. The `description` field controls auto-trigger matching — be specific.

## Add

```bash
mkdir .claude/skills/<new-name>
# Create SKILL.md with frontmatter + body
```

Or invoke the built-in `anthropic-skills:skill-creator` for guided creation.
