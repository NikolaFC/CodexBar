# Third-party notices

CodexBar.Windows is an independent, native C# port of the CodexBar project. This
file records the upstream project it is based on and the third-party components
that ship with, or are required by, this port. The full licence text of the
upstream project is reproduced verbatim in [LICENSE](LICENSE).

## Upstream project

| Item | Value |
| --- | --- |
| Project | CodexBar (macOS menu bar app for AI coding provider limits) |
| Repository | https://github.com/NikolaFC/CodexBar |
| Basis of this port | commit `278e9056e830000532e4c3a6342dc0b2c75f00d6`, version `0.75.1` |
| Licence | MIT |
| Copyright | Copyright (c) 2026 Peter Steinberger |

The MIT licence text that governs the upstream project is kept unchanged in
[LICENSE](LICENSE), including the original copyright notice, as the MIT licence
requires. This port is maintained independently of the upstream project and is
not produced, reviewed or endorsed by its author.

The upstream project is written in Swift; this repository is a C#/.NET
implementation. What it takes from upstream is the shape of the integration:
the OAuth usage endpoint for each provider, how credentials are located and
parsed, and how the usage response is mapped onto the provider's quota windows.

## Runtime components shipped in the portable build

The portable `win-x64` build is published self-contained, which means the .NET
runtime is redistributed inside the package instead of being installed on the
target machine. That redistribution carries its own notices, and the full text
of both files ships inside the package under `licenses/`:

| File in the package | Origin | What it covers |
| --- | --- | --- |
| `licenses/dotnet-LICENSE.txt` | `LICENSE.txt` in the resolved .NET SDK/runtime root, next to `dotnet.exe` | Microsoft .NET Library licence terms for the redistributed runtime |
| `licenses/dotnet-ThirdPartyNotices.txt` | `ThirdPartyNotices.txt` in the same directory | Third-party licence notices for components inside the .NET runtime |

`build.ps1` copies those two files from the SDK or runtime layout that produced
the build (the directory containing `dotnet`), so the notices always match the
runtime bits that were actually published. If either file is missing there, the
build stops instead of producing a package without notices. On CI the same
files come from the SDK that `actions/setup-dotnet` installs.

| Component | Notes |
| --- | --- |
| .NET 10 runtime (`Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App`) | Redistributed inside the self-contained portable build. Licence text: `licenses/dotnet-LICENSE.txt`. |
| Windows Presentation Foundation (WPF) | Part of the Windows Desktop runtime; used for the settings window and the tray user interface. |
| Windows Forms | Part of the Windows Desktop runtime; used for the notification-area icon and its context menu. |

## Application dependencies

The first version of this port targets only the .NET base class library. No
third-party NuGet package is referenced by the application projects, and the
build tooling (`build.ps1`, the GitHub Actions workflow and the GitHub-hosted
runner images) adds no runtime dependency of its own.

When a NuGet package is introduced, add a row here with the package id, the
version, the licence and a link to its licence text, and make sure the licence
is compatible with the MIT terms above.

## Credentials and user data

This port reads existing provider credentials in place and never copies, moves
or rewrites them. It does not bundle, transmit or log credentials, tokens or
account identifiers.
