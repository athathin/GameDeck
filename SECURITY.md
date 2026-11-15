# Security

## Reporting a problem

If you find a security problem in GameDeck (something that could harm a PC or leak someone's data), please **don't** post it
as a public issue. Use GitHub's private reporting instead:

1. Open the [Security tab](https://github.com/athathin/GameDeck/security) of this repository.
2. Choose **Report a vulnerability** and describe what you found and how to repeat it.

Only the owner can see the report. You will get an answer as soon as possible, and the fix will be released as an update
(GameDeck installs updates by itself) before the details are made public.

Ordinary bugs and ideas belong in the [issues](https://github.com/athathin/GameDeck/issues/new/choose).

## Supported versions

Only the latest release is supported. GameDeck updates itself each time it starts, so installed copies stay current.

## How updates are protected

- GameDeck only downloads updates from the releases of this repository, over HTTPS.
- The size and the SHA-256 checksum of a download are checked before the running program is replaced.
- The previous version is kept until the new one has run properly; a version that doesn't start is rolled back.
