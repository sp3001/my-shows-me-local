---
status: current
note: template, not an instance - the instances it generates carry their own front matter
---
# Task template

Copy to `tasks/<step-id>.md` when starting a step. Fill every section before writing code; delete
none. Move the finished file to `tasks/done/`.

The point of this file is **bounded reading**: if you find yourself reading a reference document end
to end, the `Read` list below was wrong — fix it, don't just read more.

```markdown
---
status: current
---

# Step <id> — <title>

## Read
Name the section, not the file, where a section is long. One line each, with why.
- `docs/STATE.md` — current state, and any open question this step consumes
- `docs/<owner>.md §n` — <the specific fact this step needs>

## Do
What to build, in the order that keeps the build green. Operational consequences belong here
("apply `ValueGenerated.None()` by convention"); canonical definitions do not ("ids are
client-generated because…").

## Acceptance criteria
Copy from `IMPLEMENTATION_PLAN.md`. Do not soften them, and do not add new ones here — if a criterion
is wrong, fix the plan, which owns it.

**This copy is meant to fork.** Criteria are the one section a task file may duplicate rather than
link, because they are read once, at the moment of verification, and an agent should not have to
re-navigate to the plan to know when it is finished. The accepted cost is that a later plan edit does
not propagate into an in-flight task file, which is why these rules exist:

- **More specific: yes.** Add the concrete numbers, fixture ids or commands this step needs
  (`assert 22410 → tt2085059`) as this step's instantiation of the plan's criterion.
- **Softer, narrower, or dropped: never.** Omitting a copied criterion is how a criterion silently
  stops existing.
- **New: no.** Fix the plan, then copy it down.
- If the plan changes while this file is open, **re-copy before finishing**, and note the re-copy in
  the evidence so the divergence is visible rather than silent.
- [ ] ...

## Constraints
Anything that would make a plausible-looking implementation wrong.

## Evidence
Goes to the Step Log in `docs/LOG.md`: the exact command run and its observed output, deviations and
why, new facts, what is unblocked. Then update the step status in `docs/STATE.md`.
```

## What does not belong here

- **A restatement of a rule.** Link the owner. If the link is not enough to act on, the owner
  document is the one that needs fixing.
- **A copy of an input.** Acceptance criteria fork on purpose; **inputs never do**. Property lists,
  file lists, project sets, table lists and column lists are pointers to the owning section — write
  "the props list in `IMPLEMENTATION_PLAN.md` step 0.1", not the items themselves. A copied input is a
  second owner for a fact the plan already owns, and it goes stale quietly, because nothing fails when
  the plan changes and the copy does not.
- **A copy of the acceptance criteria with edits.** The plan owns them; you may only specialise them.
- **The result.** Results go to `LOG.md`, which is append-only; this file moves on.
