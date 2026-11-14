# Code signing policy

FluxGet's Windows installer and FluxGet's own executables and libraries are code-signed. Bundled third-party programs (yt-dlp, FFmpeg, Deno) are shipped exactly as their publishers release them and are never re-signed by this project.

## What gets signed

- Release builds produced by `scripts/package.ps1` from a tagged commit of this repository.
- Only artifacts built from source in this repository: `FluxGet.exe`, `FluxGet.Host.exe`, FluxGet DLLs, `Setup.exe`.

## Team roles

| Role | Members |
|---|---|
| Authors / committers | [maestroohk](https://github.com/maestroohk) |
| Reviewers | [maestroohk](https://github.com/maestroohk) |
| Approvers (release signing) | [maestroohk](https://github.com/maestroohk) |

All members use multi-factor authentication on GitHub.

## Privacy

FluxGet does not transfer any information to networked systems unless specifically requested by the user or the person installing or operating it. Downloads go only to the addresses the user adds. The app does not collect telemetry. Update checks, when enabled, contact only the project's GitHub Releases.

## Certificate

Development builds are signed with a self-signed test certificate, which Windows does not trust. Public releases will be signed with a certificate from a trusted provider once one is approved for the project (applications pending: SignPath Foundation's free programme for open-source projects).
