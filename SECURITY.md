# Security Policy

## Reporting A Vulnerability

Please do not open a public GitHub issue for security-sensitive reports.

Use GitHub's private vulnerability reporting flow if it is enabled for the repository. If private reporting is not available yet, contact the maintainers through a private channel and share:

- a concise summary of the issue
- affected versions or commit range
- reproduction steps or proof of concept
- expected impact

## Scope

Security reports are especially helpful for:

- credential or token handling
- remote execution paths
- WinRM, PowerShell, and file-access flows
- packaging and plugin-loading behavior
- data exposure in logs, screenshots, or exported artifacts

## Response Goals

This project is maintained on a best-effort basis. The maintainers will try to:

- acknowledge new reports promptly
- reproduce and scope the issue
- prepare a fix or mitigation
- coordinate disclosure once a patch is available
