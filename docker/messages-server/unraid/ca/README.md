# Community Applications: what is ready and what is left

`messages-server.xml` here is the template for Unraid's Community Applications (CA), and `ca_profile.xml` the
maintainer profile CA shows beside it. Neither is listed anywhere yet: the image is private until the owner decides
otherwise. The template pulls `ghcr.io/wrenchware/messages-server:latest`, carries no one's own addresses,
passwords or folders, and keeps Unraid's nightly update working through CA's own update check against that tag.

What CA asks of a listing: a public repository with an OSI-approved licence holding the template and `ca_profile.xml`,
an image anyone can pull, and a support thread on Unraid's forum.

## Left for the owner

1. **Make the image public.** In GitHub, the `messages-server` package's settings, change its visibility to Public.
   The image workflow refuses to publish to a package that is not private, so turn that check off in the same change
   (`.github/workflows/messages-server.yml`). Making it public distributes the AGPL helpers inside it.
2. **The source offer** (`docs/release.md`, "Source offer"): every image that is public must have its source in
   https://github.com/wrenchware/switchboard-messages, tagged for the build. Publish the source for the commit the
   public image is built from, and keep doing so for every image the workflow pushes while it is public.
3. **Put the template where CA reads it.** CA reads a template repository: the public repository above works, with
   `ca_profile.xml` at its root and the template under `docker/messages-server/unraid/ca/`. Copy `ca_profile.xml` to
   the root when exporting, and check `<TemplateURL>` and `<Icon>` point at the public repository's default branch.
4. **Open a support thread** in Unraid's forum (Docker Containers, under Community Applications support) and replace
   `SUPPORT-THREAD-TO-OPEN` in both files with its address. Weigh it against "must not take over your life": the thread
   can point to the project's issues instead of promising answers.
5. **Submit** at https://ca.unraid.net/submit with the repository's address. CA's moderators check the template; their
   current rules are on that page.

## Choices in the template to confirm

- **Chrome without its sandbox** is on (`MESSAGES_CHROMIUM_NO_SANDBOX=1`), as `install-unraid.sh` does
  without `--chrome-sandbox`: the sandbox needs the seccomp profile `chrome-seccomp.json` on the host, which a template
  cannot ship. An advanced user can switch to it by hand (the main README, "Chrome's sandbox").
- **Reach from your tailnet** is on by default, since the phone needs it; the server's page signs it in.
- **The folders' owner.** Docker makes a missing folder owned by root, and the server, which runs as 2787, then refuses
  to start (`data_not_writable`). The overview gives the one command to make both folders first. A server that set
  their owner itself would need root in the container, which this image never has.
