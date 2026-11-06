# Message Link source

This is the source code for the open-source parts of Message Link: the helper programs that connect to each
service, and Message Link Server. Each tag is the exact source of one build.

- `v0.1.414` and the like: one Windows release (Messages for Windows until the Message Link name).
- `src-` followed by 12 characters: one build of the Message Link Server image. The server's page links to its own.

## What's here

- `experiments/GoogleMessages/helper`, `experiments/Messenger/helper`, `experiments/GoogleVoice/helper` and
  `experiments/Instagram/helper`: the helpers for Google Messages, Messenger, Google Voice and Instagram. They are built
  on Tulir Asokan's mautrix bridges ([gmessages](https://github.com/mautrix/gmessages),
  [meta](https://github.com/mautrix/meta), [gvoice](https://github.com/mautrix/gvoice)).
- `third_party`: the copies of mautrix code these helpers use, with local changes. Each folder's
  `NATIVE_MESSAGES_PATCH.md` lists what changed and why.
- `src`: Message Link Server for Windows (`NativeBeeper.Server`) and Linux (`NativeBeeper.Server.Linux`), and the libraries
  they share with the Windows app.
- `experiments/Tailnet/door`: the server's own Tailscale node.
- `docker/messages-server`: the Dockerfiles and install scripts for the server image.

The Windows app's interface and the Android app are separate programs and are not published here. They talk to the
server and the helpers over pipes and the network.

Each Go program has its dependencies in `vendor`, so it builds without downloading anything.

## Building

The helpers need the Go version named in their `go.mod`:

```
cd experiments/Messenger/helper
go build -trimpath -o messenger-helper .
```

The Linux server needs the .NET SDK named in `global.json`:

```
dotnet publish src/NativeBeeper.Server.Linux/NativeBeeper.Server.Linux.csproj -c Release -r linux-x64 -p:SelfContained=false -o out
```

Or build the whole image from the top of this repository:

```
docker build -f docker/messages-server/Dockerfile .
```

## Licence

The code written for Message Link is licensed under the GNU Affero General Public License, version 3 or (at your option)
any later version. The full text is in `LICENSE`. Third-party code keeps its own licence: see the licence files in
`third_party` and in each `vendor` folder, and `THIRD_PARTY_NOTICES.md`.

Message Link is a one-person project, so issues and pull requests here are not monitored.
