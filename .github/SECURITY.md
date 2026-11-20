# Security policy

## Report a security problem

Email **wrenchwareapps@gmail.com** with what you found and how to reproduce it. Please don't open a public issue or post
it anywhere until a fix is out. You'll get a reply, and credit in the fix's notes if you'd like it.

## What's covered

This repository holds the source of Message Link Server and the service helpers it runs. Reports about Message Link for
Windows and Message Link for Android are welcome at the same address.

## How Message Link is secured

- [Security](https://wrenchware.github.io/message-link/security/): what each part protects, in plain terms.
- [Security details](https://wrenchware.github.io/message-link/security/details/): the threat model, the cryptography,
  the isolation and the supply chain, with the numbers.

## Checking a Linux package

Every Linux package on this repository's releases has an SSH signature beside it (`.sig`). The nightly update and
`install-proxmox.sh` check it themselves. To check one by hand:

```sh
printf 'message-link-release ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIASDuy6t9lRx7d8PjcN7n7aYxvFR9AJkzDQA1z1huRWg\n' > allowed_signers
ssh-keygen -Y verify -f allowed_signers -I message-link-release -n message-link-package \
    -s messages-server-linux-x64.tar.gz.sig < messages-server-linux-x64.tar.gz
```
