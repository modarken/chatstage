# Contributing

Thanks for helping. Bug reports, fixes and ideas are welcome.

## Before you start

For anything bigger than a small fix, open an issue first so we can agree on the approach.

## Building and testing

You need the .NET SDK named in [`global.json`](global.json) (later feature bands of it work too).

```sh
dotnet build
dotnet test
```

The tests include end-to-end runs against a real Agent Framework server, in memory, with a fake model: no network and
no API keys.

To try the chat by hand on Windows, run the sample agent server, then the WPF sample:

```sh
dotnet run --project samples/ChatStage.Samples.AgentServer
dotnet run --project samples/ChatStage.Samples.Wpf
```

## Pull requests

- Keep each pull request to one change, with tests.
- The build treats warnings as errors and enforces the style in [`.editorconfig`](.editorconfig).
- Public APIs need XML documentation.
- Add a line to [`CHANGELOG.md`](CHANGELOG.md) under **Unreleased**.
- CI must pass before a pull request is merged.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md).
