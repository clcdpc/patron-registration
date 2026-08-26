# Repository engineering guidance

## Primary engineering principle

Prefer the simplest implementation that satisfies the actual application
requirement.

Do not introduce frameworks, generalized infrastructure, compatibility layers,
historical-state models, or extensive defensive machinery unless the current
requirement demonstrably needs them.

When several approaches are correct, prefer the one with:

1. fewer concepts,
2. less code,
3. fewer maintained states,
4. fewer abstractions,
5. easier deletion or replacement,
6. tests focused on observable behavior rather than implementation machinery.

## Scope discipline

Solve the requested problem, not hypothetical future versions of it.

Before adding infrastructure to support a possible edge case, historical state,
extension point, or future requirement, verify that the repository currently
needs to support it.

Do not preserve complexity solely because an earlier implementation introduced
it.

## Review and fix cycles

A review finding is not automatically a request for a generalized solution.

When fixing a finding:

- make the smallest change that corrects the verified problem;
- do not broaden the design unless required for correctness;
- reconsider whether existing complexity can be removed instead of extended;
- avoid creating new abstractions solely to make a local fix cleaner;
- preserve the original architectural goal of the task.

After each review/fix cycle, explicitly check for complexity creep:

- Did the fix materially increase implementation size?
- Did it introduce another representation of state?
- Did it add machinery for scenarios the application does not actually support?
- Can code added in an earlier cycle now be deleted?
- Is the resulting design simpler to explain than before?

If a sequence of individually reasonable fixes causes the implementation to
become substantially more complex, stop extending the design and reconsider
the underlying approach.

## Testing

Tests should primarily verify externally meaningful behavior and important
failure guarantees.

Do not build large test matrices around internal machinery that should not
exist in the first place.

Prefer representative state transitions and invariants over exhaustive
permutations of implementation details.

## Definition of done

Before considering a task complete:

- run the repository's normal build and test commands;
- verify the requested behavior;
- inspect the final diff for unnecessary complexity;
- remove obsolete code, tests, and documentation;
- compare the final implementation against the simplicity of the original
  requirement, not merely against the previous iteration.
