# Contributing to ApiInsights

Thanks for helping out! 

## Getting started

```bash
git clone <repo-url>
cd ApiInsights
dotnet test
dotnet run --project samples/DemoApi   # then open /_api-stats
```

## Pull requests

1. Create a feature branch (`git checkout -b feature/my-feature`).
2. Add or update tests. PRs that change behavior without tests will be asked for them.
3. Make sure `dotnet test` passes.
4. Keep the README in sync when you change the public API or options.
5. Open the pull request and describe the *why*, not just the *what*.

## Guidelines

- Analytics must never slow down or break the host application: no blocking calls in the request path.
- Anything that comes from a request (paths, headers) is untrusted. Validate it or encode it.
- The dashboard must stay locked by default.
