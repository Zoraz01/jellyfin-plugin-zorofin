# Contributing

Thanks for helping with the Zorofin plugin for Jellyfin. Bug reports, fixes and small, focused
improvements are welcome.

## Before you start

- For anything bigger than a bug fix, open an issue first so we can agree on
  the approach before you spend time on it.
- Keep one change per pull request, with tests for new behavior.
- Never include personal data, server addresses, API keys, tokens or media
  file paths in code, tests, logs or screenshots. Use `example.com` hosts and
  made-up ids.

## Checks

Run these before opening a pull request:

```sh
dotnet test                                   # unit tests (.NET 10 SDK)
node tests/web/config-page-browser.test.cjs   # setup page (needs Playwright)
node tests/web/activity-page-browser.test.cjs # Activity page (needs Playwright)
```

## Sign your commits (DCO)

Every commit must carry a `Signed-off-by` line (`git commit -s`). It certifies
the [Developer Certificate of Origin](https://developercertificate.org/): that
you wrote the change or have the right to submit it under this project's
license (GPL-3.0). By contributing, you agree that your contribution is licensed
under that license.

## AI-assisted contributions

This project is built largely with AI coding assistants, and AI-assisted
contributions are welcome. You are responsible for every line you submit:
review it, test it, and say in the pull request which parts an AI tool wrote.

## Conduct

Be respectful and constructive. Harassment or personal attacks lead to removal
from the project's spaces.
