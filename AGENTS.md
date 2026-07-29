# Repository Agent Instructions

## Remote pushes are build requests

The repository's GitHub builds are shared and expensive. Local commits are checkpoints; a remote
push requests shared validation.

- Make as many local commits as help the work, but batch them into one push for each coherent,
  reviewable change.
- Before the first push, finish the implementation, tests, proof, and applicable local checks.
- Do not push merely to save progress, refresh a pull request, or publish proof separately.
- When more changes are already known, finish and validate that correction batch before pushing it.
- During long missions, push once at a completed handoff boundary. Delay opening the pull request
  until a coherent slice is ready, or keep it draft while more work is expected.
- Push earlier when a remote handoff or recovery copy is genuinely required. Never suppress the
  final validation required before merge.
