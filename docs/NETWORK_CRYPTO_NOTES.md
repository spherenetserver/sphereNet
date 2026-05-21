# Network and Crypto Notes

SphereNet supports multiple Ultima Online client eras, so network encryption and
compression changes must be treated as high risk. Login, relay keys, game
encryption, packet framing, and Huffman compression are tightly coupled; small
changes can break login before gameplay code is reached.

## Current Decision

Do not change the inbound Huffman receive path until there is an automated
client-login regression test or a captured-packet replay test. The current
outbound game path compresses and encrypts packets, and crypto tests cover the
existing encryption primitives. The receive path should remain stable until the
expected client traffic is proven with fixtures.

## Before Changing Crypto Or Compression

- Add a packet fixture or loopback integration test for login and game-login.
- Cover `USECRYPT=0/1` and `USENOCRYPT=0/1` configurations.
- Verify at least ClassicUO plus the target legacy client version.
- Keep changes small and behind a config/feature switch when possible.
- Save captured before/after packet traces for rollback comparison.

## Candidate Tests

- Login server handshake: seed, account login, relay.
- Game server login: auth id, character list, character select.
- Movement packet parse after entering world.
- Unknown packet hook still receives unhandled opcodes.
- Huffman fixture only if a real client capture proves inbound compression is
  present for the target era.
