---
name: devthrottle-sessions
description: "Talk to the other DevThrottle sessions running across your machines - list them, rename this one, send a message, ask a question and wait for the answer, open a new session, and close one down. Use when the task involves: message another session, ask another session, list sessions, what sessions are running, spawn a session, rename this session, close this session."
license: MIT
---

# Talking to other DevThrottle sessions

A **session** is one running coding agent. DevThrottle keeps every session your
machines are running attached to one gateway, and `cc-devthrottle` is how a session
reaches the others - on this computer or on any other computer attached to the same
gateway.

There is nothing to configure. Every session DevThrottle launches already carries the
gateway address and its own credential in its environment, so the commands below work
as written. If one of them says `CC_GATEWAY_URL` or `CC_GATEWAY_SESSION_KEY` is unset,
this shell is not inside a DevThrottle-launched session.

## Find out what exists before you act

```
cc-devthrottle actions --json     # every verb this installed version supports
cc-devthrottle session whoami     # this session's own id, name, machine and repository
cc-devthrottle session list       # every session across every attached computer
```

Read `actions --json` when mapping a request onto a command rather than guessing at a
flag. It describes the version that is actually installed.

## Address a session by id prefix or by name

Every target below accepts a short prefix of a session id, or the session's exact
display name. If a prefix is ambiguous the command fails and says so - rerun with more
characters. It never picks one for you.

## Name this session

```
cc-devthrottle session rename "Frontend review"
cc-devthrottle session rename 9b2f "API rewrite"
```

The first form renames the current session; the second renames another one. Name every
session for the work it is doing. Several sessions often run in the same checkout, and
an unnamed one shows up as the bare folder name, indistinguishable from its neighbours.
Leave the repository out of the name - the session list already has a column for it.

## Send a message

```
cc-devthrottle message send 4c81 "I finished the API layer - the frontend is unblocked."
cc-devthrottle message send docs "Please refresh the API page when you get a chance."
cc-devthrottle message send all "Heads up: I am about to rebase our shared branch."
```

**Every message you send interrupts the session that receives it.** It is typed into
that agent's composer and starts a turn there. Treat it as a claim on someone else's
attention and scope it accordingly.

`message send all` reaches only the sessions working alongside you - the same piece of
work, or the same repository on the same computer. That is the everyday broadcast, and
it is the right tool for a heads-up about a shared checkout. It does not reach sessions
in other repositories.

A whole-fleet broadcast interrupts every session on every computer. The gateway refuses
one unless a person has granted it. If you believe you need one, ask the person running
the fleet; do not look for a way around the refusal.

## Ask a question and wait for the answer

```
cc-devthrottle message ask 9b2f "Which database schema is loaded in your checkout?"
cc-devthrottle message ask docs "What is the title of the API page?" --timeout-ms 60000
```

`message ask` is always single-target and blocks until the other session answers.

**Prefer `ask` over `send` whenever the answer matters.** `send` reports that a message
was delivered; delivery is not an answer, and a session that received your message and
then did nothing looks identical to one that acted on it. If you need to know something,
ask and read the reply.

## Open a new session

```
cc-devthrottle session spawn /path/to/repo --name "Frontend review"
cc-devthrottle session spawn /path/to/repo --purpose "run the test suite" --agent ClaudeCode --prompt "Run the tests and report failures."
cc-devthrottle session spawn /path/to/repo --name "build" --machine other-computer
```

Give every spawn a `--name` or a `--purpose`; a spawn with neither is warned about, and
a name equal to the bare folder name is rejected.

**Spawning is a commitment, not a resource request.** A session you started is yours to
drive to completion. Before you spawn one, be able to say what specifically has to
happen for it to be finished - a session with no stated exit does not acquire one on its
own. Decide up front where its output has to end up, because that decides whether its
work survives: output left in a throwaway checkout dies with that checkout.

## Close a session down

```
cc-devthrottle session done            # flag THIS session for removal
cc-devthrottle session done <target>   # flag another one
```

`session done` marks a session for graceful removal. It does not kill it mid-turn - the
session finishes what it is doing and is reaped shortly after. Use it on unattended runs
so a finished session does not sit idle.

## Check the plumbing

```
cc-devthrottle selftest
```

Opens two throwaway local sessions, proves list, send and ask all work end to end, then
removes them.
