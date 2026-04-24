# Contributing to SqliteWasmBlazor

Thanks for your interest in SqliteWasmBlazor. Issues, bug reports, ideas, and discussions are always welcome — please open them any time.

For code contributions we follow an **issue-first policy**. This document explains what that means and why.

## Why issue-first?

Writing code has become the cheap part. The hard work is now deciding *what* should be built, how it fits the existing architecture, and verifying that the result actually works in context. A pull request that shows up without this groundwork shifts all of that work onto the maintainer — even when the code itself is formally correct.

To keep review attention meaningful, we ask contributors to start with an issue and agree on the direction before writing code.

## When to open an issue first

Please open an issue and wait for a short design discussion before writing code if your change is any of the following:

- A new feature or public API addition
- A refactor touching multiple files or crossing module boundaries
- A behavioural change, even if framed as a bug fix
- A dependency addition, upgrade, or removal
- A performance-motivated change (please include a benchmark sketch)

A good issue contains:

1. **Problem** — what is broken, missing, or inefficient, in a concrete situation.
2. **Proposed approach** — a short sketch of how you would solve it. Bullet points are fine.
3. **Rationale and trade-offs** — why this matters and what it costs.

A polished spec is not required. A few honest sentences that show you have thought about the problem are enough to start a conversation.

## When you can skip the issue

Direct PRs are welcome for:

- Typo fixes in documentation or comments
- Obvious, narrowly-scoped bug fixes where the diff is self-explanatory
- Small improvements to existing tests

When in doubt, open an issue — it is cheaper than rewriting a rejected PR.

## AI-assisted contributions

AI-assisted contributions are welcome. This project itself is developed with heavy AI assistance, and we draw no moral distinction between code typed by hand and code generated with a model. What matters is:

- You understand the code you are submitting and can defend its design choices.
- You have run it and tested it against the real codebase.
- The contribution is grounded in the actual project context, not in a plausible-sounding hallucination.

Drive-by PRs consisting of unreviewed model output — large refactors, speculative "cleanup" passes, invented security fixes — will be closed without detailed review.

### Disclosure is required

Every PR must fill in the *AI assistance disclosure* section of the pull-request template. This is not a gatekeep — AI-assisted PRs are accepted on equal terms — but reviewers need to know the production mode to pick the right review focus:

- A **hand-written** or **AI-assisted-typing** PR gets reviewed primarily for correctness and edge cases; the design is human-vetted.
- An **AI-led** PR gets design-level review too, because different models carry different defaults and blind spots.

Naming the specific model and any guidance in play (system prompt, skill, agent configuration) also helps — two PRs produced by two different AIs with different training and guidance can look equally polished and propose incompatible solutions. Knowing that upfront reframes the conversation from *"whose code is right"* to *"which shape fits this repo's conventions"*.

PRs that leave the disclosure section blank will receive a request to fill it in before review continues.

## Attribution

Contributors are credited via Git trailers in the commit message and, for notable changes, in `CHANGELOG.md` and release notes. We use the standard Git conventions:

- `Reported-by:` — you reported the underlying problem.
- `Suggested-by:` — you proposed the approach that was implemented.
- `Helped-by:` — substantial debugging, design, or review help.
- `Co-authored-by:` — substantial co-authorship of the code itself. GitHub recognises this trailer and displays you as a co-author on the commit and in the contributor graph.

An issue that shapes the implementation of a feature is a real contribution and will be credited accordingly, even if you did not write the final code.

## What happens to code PRs without a linked issue

Non-trivial PRs without prior issue discussion will be closed with a friendly pointer back to this document and an invitation to open an issue. No hard feelings — the PR is not lost, and a short design discussion almost always leads to a better outcome.

## Code of conduct

Be kind. Assume good faith. Disagree on technical substance, not on people.
