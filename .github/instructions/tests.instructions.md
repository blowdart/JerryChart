---
applyTo: '**/*.test.cjs,**/*Tests.cs,.github/workflows/**'
description: 'Cross-platform test and CI requirements.'
---

# Cross-platform tests

- Tests must support both Windows development machines and Linux CI runners.
- Use platform-aware path APIs. Do not assume Windows drive letters, backslash separators, case-insensitive filenames, or a particular working directory.
- JavaScript test loaders must normalize both slash and backslash separators before resolving module paths.
- Keep shell commands compatible with the operating system used by each workflow job.
- When changing test loaders, filesystem handling, or test commands, validate on both Windows and Linux. A passing Windows run alone does not establish Linux compatibility.
- Preserve readable failure output in CI logs as well as machine-readable test artifacts.
