# How standalone work with review runs

One agent does the work, a second and separate agent reviews it before it is called done.

This is the default for ordinary work: small enough not to need an Architect, big enough that nobody
should mark their own homework.

## The conduct

- One agent takes the work to a pull request, with the proof that it does what it is supposed to,
  in its own worktree cut from the main branch.
- A SEPARATE agent - never the one that wrote it - verifies the work against the reported symptom
  rather than trusting the report, then passes it or sends it back with a written defect.
- The reviewer passing it and the pull request merging is what "done" means.
- The human is bothered once, when the review passes.
