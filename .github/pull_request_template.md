## What and why

Requirement IDs:

## Checklist

- [ ] Tests written first, at the layers the requirement names; each rejection test has a passing control case
- [ ] Sensitivity check on any critical rule changed: a planted bug is caught by the suite
- [ ] Size limits met (method <= 60, class <= 500, file <= 600 logical lines), or a decision record explains the exception
- [ ] CI green: locked restore, format, zero-warning build, full suite, coverage floor, money-type check
- [ ] Clean-clone check passes (once the one-command run exists)
- [ ] Requirements traceability (`docs/requirements.md` §10) updated
- [ ] Decision record added for any significant or hard-to-reverse choice
