---
name: asking-user-questions
description: "Use only when composing a question round inside a workflow, or when a workflow skill names it at a question step. Shared norms for asking the user — not a workflow, nothing to execute."
---

# Asking User Questions

The workflow family's shared norms for questioning the user: how to interview in rounds, what makes a
question worth asking, how to shape options, and how to degrade when answers don't come. Process
skills name this concept at the steps that ask; *when* to ask — and where the answers get recorded —
stays with the referencing skill.

Ask through your question tool when the session has one (a structured multiple-choice prompt);
otherwise ask in a plain message, numbering the questions and their options, and wait for the reply.

## Interview in rounds until nothing is assumed

- Map the subject as a **design tree**: every decision branches into the decisions that hang off it.
  The **frontier** is every open decision whose prerequisites are already settled — the questions you
  can ask *now* without guessing at answers you haven't heard.
- One round = the whole frontier, asked together. A question whose answer depends on another question
  still open belongs to a *later* round, never the same one. Never split independent questions across
  back-to-back rounds. When the question tool limits how many questions one call holds, fill each call
  and send the rest of the frontier immediately after.
- **A round blocks the current step.** Don't keep working on the blocked step or assume an answer until
  it arrives. If the user replies with a free message instead of answering, treat it as their reply and
  re-ask only what still matters.
- After each round, recompute the frontier: answers settle branches, unblock their dependents, and
  prune branches that no longer apply. Ask the next round; never pre-write later rounds.
- The interview is done when the frontier is empty — every branch visited, nothing material silently
  assumed. Then continue the referencing skill's next step; there is no extra "are we aligned?" round
  (the referencing skill's own gates still apply).
- Depth follows the work, not a quota: work with no open user decision gets zero rounds; a
  decision-heavy change may take several.

## Facts are yours, decisions are theirs

- Never ask the user for a fact the workspace, the specs, or the web can answer. Look it up. When a
  lookup is slow, start it in the background (a sub-agent, when the session offers one) and ask the
  rest of the frontier meanwhile — only the questions downstream of that lookup wait.
- Every decision — scope, observable behavior, trade-offs the user will live with — goes to the user.
  Picking an option yourself and moving on is answering your own question, not inferring.
- If candidate options differ only internally — identical observable behavior — it is not a user
  decision: decide yourself and record the reasoning in the workflow's artifact.

## What makes a question worth asking

- **It changes the outcome.** Each answer leads somewhere different that the user will see or live
  with. If every option ends in the same place, drop the question.
- **It is specific to this work.** Name the actual feature, screen, file, or user ("when a project is
  renamed while collapsed…"), never a generic template question.
- **It probes what people silently assume.** Sweep the tree's usual blind spots: scope edges and
  non-goals, empty / error / failure states, edge cases and limits, concurrency or multiple
  instances, existing data and behavior that must migrate or stay, defaults, and who the work is for.
- **It surfaces tension.** A conflict with a recorded spec decision, an earlier answer, or the
  request itself is asked about outright, quoting both sides.
- **It never re-asks.** Anything the request, a prior answer, or the specs already settled is
  settled; build on it.
- **A concrete scenario beats an abstract principle.** Ask "a user drags a file onto a busy session —
  queue it or reject it?", not "how should concurrency be handled?".
- If the question can't be settled by talking (how something should look or feel), show a concrete
  artifact — a mockup, snippet, or config — with the option instead of describing it.

## Options

- Recommended option first, labelled "(Recommended)", with one line on why you recommend it over the
  alternatives.
- Every option: a concise label (1–5 words) + a description carrying the trade-off or consequence of
  choosing it. Tailor options to the work at hand — never generic placeholders.
- Options must be **decidable by the asked user**: frame them as observable behavior or outcomes
  ("collapsing a project stays collapsed after a rename"), never as implementation mechanics
  ("semantic guard", "activation ref").
- Don't author your own "Other" or free-text option when the question tool already offers one; in a
  plain message, the user can always answer in their own words.
- Allow several answers at once when several are valid (feature checklists); a single choice when
  confirming something or choosing one path.

## Confirming an inference

When you have inferred something and need a yes/adjust rather than an open answer: the inferred
statement *is* the question, with "Looks right" as the first option (accurate as written) and a genuine
rejection option second (e.g. "Off base — ask me directly"). Read the response as:

- **"Looks right"** → the inference holds; continue unchanged.
- **Free-text tweak** (one fact changes) → update that field only; don't re-derive anything else.
- **Substantial rewrite** → re-derive every inference that came from that statement before continuing.
- **Rejection** → discard the inference entirely and ask an open-ended question instead.

## Degradation

- A skipped or declined question is not a blocker: settle it on your recommended answer, recorded as
  unconfirmed in the workflow's artifact (the referencing skill says where), and keep working its
  dependents from that assumption — don't re-ask it.
- A whole round skipped means the user wants to stop being interviewed: ask no further rounds and
  proceed on recorded assumptions for everything still open.
- When the session runs without a user to answer (a non-interactive run), state your assumptions the
  same way instead of blocking.
- "I don't know / help me understand" is a mis-framing signal, not a missing-knowledge one: re-explain
  from user-visible behavior in plain language, then re-ask with behavior-framed options — don't
  repeat the same technical options with more detail.

## Red flags

- You stopped after one round while decisions that depended on its answers are still open.
- You chose an option for the user instead of asking — or asked the user something you could have
  looked up.
- One round holds two questions where one answer would change the other.
- A question restates what the request or an earlier answer already settled.
- Every question would fit any project — nothing names this work's specifics.
