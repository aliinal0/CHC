# Security policy

CHC can run as administrator and can install the PawnIO driver, so security problems matter. Thank you for
reporting them responsibly.

## Supported versions

Only the latest release gets security fixes. Please check that the problem still happens with it.

## Reporting a problem

Please **don't open a public issue**. Report it privately instead:
[Security → Report a vulnerability](https://github.com/aliinal0/CHC/security/advisories/new).

Include what an attacker could do, the CHC version and how to reproduce it. You'll get an answer as soon as
possible, and you'll be credited in the release notes unless you'd rather not be.

## Out of scope

- Problems in the PawnIO driver itself belong to [PawnIO](https://github.com/namazso/PawnIO).
- SmartScreen's "Windows protected your PC" warning: CHC isn't code-signed yet. Compare the SHA-256 of the
  file you downloaded with the one in the release notes.
