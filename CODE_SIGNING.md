# Code signing policy

> **Status:** ShinyForge's downloads are **not code-signed yet**. Windows may warn before running them - see
> [If Windows blocks the installer](README.md#if-windows-blocks-the-installer). This page describes how releases are
> built and how they will be signed, so the process is already in place.
>
> When signing is set up through SignPath, this line applies:
> Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

## How a release is built

Every download is built from the source code in this repository by GitHub Actions
([`.github/workflows/build.yml`](.github/workflows/build.yml)), not on a personal computer:

1. A version tag (for example `v1.6.0`) starts the build. The tag must match `version.txt`.
2. The Windows installer and portable ZIP are built on a GitHub-hosted Windows machine with [`build.ps1`](build.ps1)
   (Inno Setup for the installer). Third-party web code that ships inside ShinyForge (DexTracker, MIT) is rebuilt from
   its source in [`external/`](external) in the same run.
3. The macOS and Linux downloads are built on a GitHub-hosted Linux machine with [`build-unix.ps1`](build-unix.ps1).
4. Each file gets a SHA-256 checksum (`SHA256SUMS.txt`, `SHA256SUMS-unix.txt`) and a signed
   [build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations)
   that records the commit and workflow that produced it.
5. The result is a **draft** release. A maintainer checks it and publishes it by hand.

Check that a download came from this process:

```
gh attestation verify ShinyForge-Setup.exe --repo Domsanna/ShinyForge
```

## What gets signed

Only ShinyForge's own files, built by the workflow above: `ShinyForge-Setup.exe` and the program files in
`ShinyForge-Portable.zip`. Files from other open-source projects that ship with ShinyForge
([PKHeX.Core](https://github.com/kwsch/PKHeX), the .NET runtime, DexTracker, TPPFLUSH) are not signed with
ShinyForge's certificate. Programs ShinyForge can download for you later (FFmpeg, Python, PKHeX, sys-botbase, ...)
come from their own publishers and are never signed by this project.

Every signing request needs a manual approval. Nothing is signed automatically.

## Team roles

ShinyForge is maintained by one person, who holds all three roles:

| Role | Who | What it means |
| --- | --- | --- |
| Committer and reviewer | [Domsanna](https://github.com/Domsanna) | Writes and reviews all changes. Changes from anyone else come in as pull requests and are reviewed before they're merged. |
| Approver | [Domsanna](https://github.com/Domsanna) | Approves each release for signing. |

Everyone with one of these roles uses multi-factor authentication for GitHub and for the signing service.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it. The details are in [PRIVACY.md](PRIVACY.md).

## Reporting a problem with a signed file

If you think a signed ShinyForge file is malicious or was not built from this repository, open an issue
(or contact the maintainer through GitHub) with the file's name and SHA-256 checksum.
