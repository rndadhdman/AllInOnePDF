# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub instead: open the
repository's **Security** tab and choose **Report a vulnerability**. Include the version (Settings › About ›
**Copy diagnostics**), the steps or a sample file that triggers the problem, and what an attacker could do with it.

You will get an answer on the report within a week. A fix is released as a new version, and the report is credited
in the release notes unless you ask otherwise.

## Supported versions

Only the latest release on the [Releases page](https://github.com/rndadhdman/AllInOnePDF/releases) receives fixes.

## What is in scope

AllInOne PDF opens files from untrusted sources, so the most relevant problems are in the importers and the renderer:
a crafted PDF, Office file, EPUB, HTML, Markdown, RTF or image that crashes the app, reads or writes files outside the
folders the user chose, runs script, or makes a network request. The app is designed to do none of those things (see
"Security posture" in the [README](README.md)).

The web host (`src/AllInOnePdf.Web`) is meant for local previews and binds to 127.0.0.1. Running it on a public
address is not a supported configuration.
