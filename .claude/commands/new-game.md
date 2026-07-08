---
description: Scaffold a new game module using the established IGameModule boundary
---

Use the `game-scaffolder` subagent to add a new game to the platform.

If the game's mechanic isn't already described in this conversation, ask
for a brief summary first (what does a player do, what does "correct"
mean, what does a round/instance of this game look like) — don't scaffold
against a guess.

Once scaffolded, report back: what was created, which REQ-stub range was
used in `docs/requirements-document.md`, and a reminder to run
`architecture-reviewer` once real game logic is added, before it goes far
enough to be expensive to unwind if a boundary rule got violated.
