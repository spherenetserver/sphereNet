# Sphere 56x Protocol Matrix

This matrix tracks incoming client opcodes that SphereNet currently routes through
`PacketManager`. It is intentionally focused on parser coverage: every registered
incoming handler below must be documented here, and tests fail if registry/docs drift.

## Mandatory Implemented
- `0x00` Create character
- `0x02` Movement request
- `0x03` Speech request
- `0x05` Attack request
- `0x06` Double click
- `0x07` Item pickup
- `0x08` Item drop
- `0x09` Single click
- `0x12` Text command
- `0x13` Equip item
- `0x22` Resync request
- `0x34` Status request
- `0x3A` Skill lock
- `0x5D` Character select
- `0x6C` Target response
- `0x72` War mode
- `0x73` Ping
- `0x75` Rename
- `0x80` Login request
- `0x91` Game login
- `0xA0` Server select
- `0xAD` Unicode speech
- `0xB1` Gump response
- `0xB8` Profile request
- `0xBD` Client version
- `0xBF` Extended command
- `0xF0` New movement / extension request
- `0xF8` Create character HS
- `0x8D` Create character (KR / Enhanced Client; profession table as upstream)

## Optional Implemented
- `0x3B` Vendor buy
- `0x56` Map pin edit
- `0x6F` Secure trade
- `0x71` Bulletin board
- `0x90` Map detail
- `0x93` Book header
- `0x98` All names request
- `0x9F` Vendor sell
- `0xA4` System info
- `0xAC` Gump text entry
- `0xB2` Legacy chat text-in accepted/ignored; outgoing conference chat uses `0xB2`
- `0xB3` Chat action (talk/join/create/leave — conference chat system)
- `0xB5` Chat window open (sends channel list, fires @UserGlobalChatButton)
- `0xBB` Legacy mail message (fires @UserMailBag on the recipient)
- `0xBE` Assist version
- `0xC8` View range
- `0xD6` AOS tooltip request
- `0xD7` Encoded command
- `0xD9` Hardware info
- `0xE1` Client type
- `0xE3` KR encryption negotiation
- `0xC2` Unicode prompt response (counterpart of the `0x9A` ASCII one)
- `0xEC` Equip item macro (Source-X `PacketEquipItemMacro`, batch capped at 3)
- `0xED` Unequip item macro (Source-X `PacketUnEquipItemMacro`, batch capped at 3)
- `0xB6` Old tooltip request (pre-AOS; same route as `0xD6`)
- `0xE0` Bug report (fires @UserBugReport)
- `0xF1` Time sync request (answered with `0xF2`)
- `0xF4` Crash report (logs and fires @UserBugReport)
- `0xFA` Ultima Store button (fires @UserUltimaStoreButton)
- `0xFB` Show public house content toggle
- `0xA7` Tip window paging (answered with a `[TIP n]` section as a `0xA6` scroll)
- `0xF9` Global chat request (CHATFLAGS `0x10`; status toggle answered with `0xF9`)

- `0x01` Disconnect notification (closes the session)
- `0x2C` Death menu (request / resurrect / ghost)
- `0x66` Book page write (pages are parsed and stored on the book)
- `0x7D` Menu choice (old-style menu response)
- `0x83` Character delete
- `0x95` Dye response (applies the chosen hue)
- `0x9A` Prompt response (ASCII; `0xC2` is the Unicode one)
- `0x9B` Help request
- `0xD1` Logout request
- `0xD4` New book header (AOS+ variable-length format)

## Known Ignored
Nothing. Every opcode this server registers reaches a handler that acts on it; an
opcode it does not register is not listed here, it simply falls to the unknown path
below. The list that used to sit here named eight opcodes that all had working
handlers, which is the failure this section is now shaped to avoid: if an opcode is
registered, it does not belong under this heading, and the guardrail test enforces
exactly that.

## Unknown / Drop
Unknown opcodes are routed to the network unknown-packet path and must not crash the
server. Variable-length packets with invalid lengths are rejected by `NetworkManager`.

## 0xBF Extended Subcommands
Known incoming subcommands are centralized in `ExtendedCommandRegistry`.

- `0x0005` Screen size
- `0x0006` Party
- `0x000B` Chat/language button path
- `0x0010` Old-style tooltip request (clients before 5.0.9; same answer as `0xD6`)
- `0x0013` Context menu request
- `0x0015` Context menu response
- `0x001A` Stat lock change
- `0x001C` Spell select (the client's new spell-select command)
- `0x0024` Known ignored
- `0x0028` Guild button
- `0x002C` Bandage macro (targeted bandage use)
- `0x0032` Gargoyle flight toggle — note the value is reused in the other
  direction, where upstream calls it the quest button (`EXTAOS_QuestButton`);
  this section is about what the CLIENT sends, and there it is the flight toggle
