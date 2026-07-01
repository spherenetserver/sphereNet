# Packet Akış Rehberi

Bu doküman Source-X bilen bir geliştiricinin SphereNet içinde paketleri takip
edebilmesi için yazıldı. Amaç şudur:

- Client'tan gelen paket nerede okunuyor?
- Hangi dosyada hangi handler'a bağlanıyor?
- Davranış hangi `GameClient` dosyasında çalışıyor?
- Server client'a cevap olarak hangi outgoing paketi gönderiyor?
- Item drop sound, item flip, world item refresh gibi Source-X parity akışları
  nerede doğrulanmalı?

Kısa opcode listesi için ayrıca [PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md)
dosyasına bakılabilir. Bu dosya ise akışı ve dosya yerlerini anlatır.

---

## En Kısa Özet

SphereNet'te gelen paketler doğrudan oyun davranışı çalıştırmaz. Paket sınıfı
sadece byte okur ve `NetState` üstünden server tarafına haber verir.

Genel zincir:

```text
Client paketi
  -> NetworkManager.ProcessInput
  -> PacketManager handler bulur
  -> PacketHandler.OnReceive paketi parse eder
  -> NetState.OnXxx çağrılır
  -> Program.NetworkHandlers.OnXxx çağrılır
  -> GameClient.HandleXxx oyun davranışını çalıştırır
  -> gerekirse PacketWriter ile outgoing packet gönderilir
```

Source-X karşılıkları:

| Source-X | SphereNet | Dosya |
|---|---|---|
| `CNetworkManager` | `NetworkManager` | `src/SphereNet.Network/Manager/NetworkManager.cs` |
| `CNetState` | `NetState` | `src/SphereNet.Network/State/NetState.cs` |
| `CPacketManager` | `PacketManager` | `src/SphereNet.Network/Packets/PacketManager.cs` |
| packet length table | `PacketDefinitions` | `src/SphereNet.Network/Packets/PacketDefinitions.cs` |
| receive packet | `PacketHandler` | `src/SphereNet.Network/Packets/Incoming/` |
| send packet | `PacketWriter` | `src/SphereNet.Network/Packets/Outgoing/` |
| `CClient` | `GameClient` partial class | `src/SphereNet.Game/Clients/` |

---

## Aynı Paketleri Sürekli Yazıyor muyuz?

Hayır. Normal tasarımda aynı packet byte'ları her yerde tekrar tekrar yazılmıyor.

Doğru pattern:

1. Paket bir kere `PacketWriter` class olarak tanımlanır.
2. Davranış dosyaları bu class'ı çağırır.
3. Ortak davranışlar helper fonksiyonlardan gönderilir.

Örnekler:

```csharp
_netState.Send(new PacketDropAck());
_netState.Send(new PacketContainerItem(...));
_netState.Send(new PacketWornItem(...));
_netState.SendPriority(new PacketMoveAck(...));
BroadcastNearby?.Invoke(position, range, new PacketSound(...), excludeUid);
```

Outgoing paket class'ları çoğunlukla burada:

| Dosya | Ne içerir |
|---|---|
| `src/SphereNet.Network/Packets/Outgoing/GamePackets.cs` | login, movement, speech, container gibi temel packetler |
| `src/SphereNet.Network/Packets/Outgoing/ExtendedPackets.cs` | world item, draw object, status, gump, sound, effect gibi geniş packetler |

Bazı eski/çok küçük packetler raw buffer ile gönderilmiş olabilir. Örneğin pickup
fail `0x27`. Yeni işlerde tercih edilen yol named `PacketWriter` kullanmaktır.

---

## Paket Takip Etme Yöntemi

Bir paketi takip ederken şu sırayla git:

1. Opcode'u `PacketDefinitions.cs` içinde bul.
2. Incoming parser class'ını `Incoming/*.cs` içinde bul.
3. `NetworkManager.RegisterStandardPackets()` içinde register edilmiş mi bak.
4. Parser içindeki `state.OnXxx(...)` çağrısını bul.
5. `NetState` içinde `OnXxx` ve delegate property hangi isimde bak.
6. `Program.NetworkHandlers.cs` içinde `OnXxx` metoduna git.
7. Oradan çağrılan `GameClient.HandleXxx` metodunu aç.
8. Method içinde hangi outgoing paketler gönderiliyor kontrol et.
9. Eğer world item/mob görünümü değişiyorsa `GameClient.ViewUpdate.cs` kontrol et.

---

## Ana Dosyalar

| Dosya | Görev |
|---|---|
| `src/SphereNet.Network/Packets/PacketDefinitions.cs` | Opcode length table. `0x08` gibi era'ya göre değişen length burada ele alınır. |
| `src/SphereNet.Network/Packets/PacketManager.cs` | Opcode -> `PacketHandler` map'i. |
| `src/SphereNet.Network/Manager/NetworkManager.cs` | Socket input okur, handler dispatch eder, packetleri register eder. |
| `src/SphereNet.Network/State/NetState.cs` | Connection state, send queue, delegate köprüsü. |
| `src/SphereNet.Network/Packets/Incoming/LoginPackets.cs` | Login, movement, speech gibi paket parser'ları. |
| `src/SphereNet.Network/Packets/Incoming/GamePackets.cs` | Double click, item pickup/drop/equip, target, gump, vendor, trade parser'ları. |
| `src/SphereNet.Network/Packets/Outgoing/GamePackets.cs` | Temel outgoing packet writer'ları. |
| `src/SphereNet.Network/Packets/Outgoing/ExtendedPackets.cs` | World item, draw object, sound, effect, gump packetleri. |
| `src/SphereNet.Server/Program.NetworkHandlers.cs` | `NetState` event'lerini `GameClient` davranışına bağlar. |
| `src/SphereNet.Server/Program.EngineWiring.cs` | Network handler wiring ve engine config aktarımı. |
| `src/SphereNet.Game/Clients/GameClient.*.cs` | Asıl oyun davranışı. Source-X `CClient` karşılığı. |

---

## GameClient Dosya Haritası

`GameClient` tek dosya değil, partial class olarak bölünmüş.

| Dosya | Ne zaman bakılır? |
|---|---|
| `GameClient.cs` | Ana field'lar, constructor, callback/delegate tanımları. |
| `GameClient.Login.cs` | Login, karakter seçimi, dünyaya giriş. |
| `GameClient.Combat.cs` | Movement, war mode, attack, movement ack/reject. |
| `GameClient.Inventory.cs` | Item pickup, drop, equip, container, status/profile. |
| `GameClient.ItemUse.cs` | Double click, item kullanma, tool/forge/door benzeri davranışlar. |
| `GameClient.PacketHelpers.cs` | Draw object, world item, container, target, tooltip helper'ları. |
| `GameClient.ViewUpdate.cs` | Görünürlük delta sistemi, world item/mob draw/remove. |
| `GameClient.WorldFeatures.cs` | Door toggle, context menu, bazı `0xBF` feature akışları. |
| `GameClient.ScriptConsole.cs` | Komutlar, sysmessage, notoriety hesapları. |

---

## Incoming Paketler: Nerede Okunuyor, Nereye Gidiyor?

Bu tablo en çok bakılacak paketleri gösterir.

| Opcode | Client isteği | Parser class / dosya | Server bridge | Game davranışı |
|---|---|---|---|---|
| `0x00` | Create character | `PacketCreateCharacter` / `Incoming/LoginPackets.cs` | `Program.NetworkHandlers.cs` | `GameClient.Login.cs` |
| `0x02` | Move request | `PacketMoveRequest` / `Incoming/LoginPackets.cs` | `OnMoveRequest` | `GameClient.Combat.cs` |
| `0x03` | ASCII speech | `PacketSpeechRequest` / `Incoming/LoginPackets.cs` | `OnSpeech` | Speech/command path |
| `0x05` | Attack request | `PacketAttackRequest` / `Incoming/LoginPackets.cs` | `OnAttackRequest` | `GameClient.Combat.cs` |
| `0x06` | Double click | `PacketDoubleClick` / `Incoming/GamePackets.cs` | `OnDoubleClick` | `GameClient.ItemUse.cs` |
| `0x07` | Item pickup | `PacketItemPickup` / `Incoming/GamePackets.cs` | `OnItemPickup` | `GameClient.Inventory.cs` |
| `0x08` | Item drop | `PacketItemDrop` / `Incoming/GamePackets.cs` | `OnItemDrop` | `GameClient.Inventory.cs` |
| `0x09` | Single click | `PacketSingleClick` / `Incoming/GamePackets.cs` | `OnSingleClick` | `GameClient.Inventory.cs` / label |
| `0x12` | Text command | `PacketTextCommand` / `Incoming/LoginPackets.cs` | `OnTextCommand` | command/speech path |
| `0x13` | Item equip | `PacketItemEquip` / `Incoming/GamePackets.cs` | `OnItemEquip` | `GameClient.Inventory.cs` |
| `0x22` | Resync request | `PacketResyncRequest` / `Incoming/LoginPackets.cs` | `OnResyncRequest` | `GameClient.Login.cs` / view refresh |
| `0x34` | Status request | `PacketStatusRequest` / `Incoming/GamePackets.cs` | `OnStatusRequest` | `GameClient.Inventory.cs` |
| `0x3A` | Skill lock | `PacketSkillLock` / `Incoming/LoginPackets.cs` | `OnSkillLock` | skills path |
| `0x3B` | Vendor buy | `PacketVendorBuy` / `Incoming/GamePackets.cs` | `OnVendorBuy` | `GameClient.ItemUse.cs` vendor methods |
| `0x5D` | Character select | `PacketCharSelect` / `Incoming/LoginPackets.cs` | `OnCharSelect` | `GameClient.Login.cs` |
| `0x6C` | Target response | `PacketTargetResponse` / `Incoming/GamePackets.cs` | `OnTargetResponse` | `GameClient.PacketHelpers.cs` / target methods |
| `0x6F` | Secure trade | `PacketSecureTrade` / `Incoming/GamePackets.cs` | `OnSecureTrade` | trade path |
| `0x72` | War mode | `PacketWarMode` / `Incoming/LoginPackets.cs` | `OnWarMode` | `GameClient.Combat.cs` |
| `0x73` | Ping | `PacketPing` / `Incoming/LoginPackets.cs` | `OnPing` | network/client state |
| `0x75` | Rename | `PacketRename` / `Incoming/GamePackets.cs` | `OnRename` | rename behavior |
| `0x7D` | Menu choice | `PacketMenuChoice` / `Incoming/GamePackets.cs` | `OnMenuChoice` | menu/crafting path |
| `0x80` | Login request | `PacketLoginRequest` / `Incoming/LoginPackets.cs` | `OnLoginRequest` | `GameClient.Login.cs` |
| `0x83` | Character delete | `PacketCharDelete` / `Incoming/GamePackets.cs` | `OnCharDelete` | login/account path |
| `0x91` | Game login | `PacketGameLogin` / `Incoming/LoginPackets.cs` | `OnGameLogin` | `GameClient.Login.cs` |
| `0x95` | Dye response | `PacketDyeResponse` / `Incoming/GamePackets.cs` | `OnDyeResponse` | `GameClient.ItemUse.cs` |
| `0x98` | All names | `PacketAllNamesReq` / `Incoming/GamePackets.cs` | `OnAllNamesRequest` | label/name path |
| `0x9A` | Prompt response | `PacketPromptResponse` / `Incoming/GamePackets.cs` | `OnPromptResponse` | prompt/script path |
| `0x9F` | Vendor sell | `PacketVendorSell` / `Incoming/GamePackets.cs` | `OnVendorSell` | vendor path |
| `0xA0` | Server select | `PacketServerSelect` / `Incoming/LoginPackets.cs` | `OnServerSelect` | login path |
| `0xAC` | Text entry | `PacketGumpTextEntry` / `Incoming/GamePackets.cs` | `OnGumpTextEntry` | dialog/script input |
| `0xAD` | Unicode speech | `PacketSpeechUnicode` / `Incoming/GamePackets.cs` | `OnSpeech` | speech/command path |
| `0xB1` | Gump response | `PacketGumpResponse` / `Incoming/GamePackets.cs` | `OnGumpResponse` | dialog/gump path |
| `0xB6` / `0xD6` | Tooltip request | tooltip parser / `Incoming` | tooltip bridge | `GameClient.PacketHelpers.cs` |
| `0xBD` | Client version | `PacketClientVersion` / `Incoming/GamePackets.cs` | `OnClientVersion` | client feature state |
| `0xBF` | Extended command | `PacketExtendedCommand` / `Incoming/GamePackets.cs` | `OnExtendedCommand` | `GameClient.WorldFeatures.cs` |
| `0xD7` | Encoded command | `PacketEncodedCommand` / `Incoming/GamePackets.cs` | `OnEncodedCommand` | house/custom design path |
| `0xF0` | New movement | `PacketNewMovementRequest` / `Incoming/LoginPackets.cs` | `OnMovementBatch` | `GameClient.Combat.cs` |
| `0xF8` | Create character HS | `PacketCreateCharacterHS` / `Incoming/LoginPackets.cs` | create char bridge | `GameClient.Login.cs` |

Tüm register listesi için `NetworkManager.RegisterStandardPackets()` içine bak.
Bu method, server'ın hangi incoming paketleri tanıdığını en net gösteren yerdir.

---

## Outgoing Paketler: Nerede Tanımlı, Nerede Kullanılıyor?

| Outgoing | Ne işe yarar? | Tanım dosyası | Tipik kullanım dosyaları |
|---|---|---|---|
| `PacketMoveAck` `0x22` | Walk kabul | `Outgoing/GamePackets.cs` | `GameClient.Combat.cs` |
| `PacketMoveReject` `0x21` | Walk reject/resync | `Outgoing/GamePackets.cs` | `GameClient.Combat.cs` |
| `PacketDropAck` `0x29` | Item drop kabul | `Outgoing/GamePackets.cs` | `GameClient.Inventory.cs` |
| `PacketDropReject` `0x28` | Item drop reject | `Outgoing/GamePackets.cs` | `GameClient.Inventory.cs` |
| raw pickup fail `0x27` | Pickup fail reason | raw helper | `GameClient.ScriptConsole.cs` / inventory path |
| `PacketContainerItem` `0x25` | Container içinde item update | `Outgoing/GamePackets.cs` | `GameClient.Inventory.cs`, `GameClient.PacketHelpers.cs` |
| `PacketContainerContent` `0x3C` | Container içeriği | `Outgoing/GamePackets.cs` | `GameClient.PacketHelpers.cs` |
| `PacketOpenContainer` `0x24` | Container aç | `Outgoing/GamePackets.cs` | `GameClient.PacketHelpers.cs`, item use |
| `PacketWornItem` `0x2E` | Mobile üstünde equip item | `Outgoing/GamePackets.cs` | `GameClient.Inventory.cs` |
| `PacketDeleteObject` `0x1D` | Objeyi client view'dan sil | `Outgoing/GamePackets.cs` | `GameClient.ViewUpdate.cs`, inventory/equip path |
| `PacketWorldItem` `0x1A` | Ground item draw/update | `Outgoing/ExtendedPackets.cs` | `GameClient.PacketHelpers.cs`, view update |
| `PacketWorldItemSA` `0xF3` | SA+ ground item draw/update | `Outgoing/ExtendedPackets.cs` | `GameClient.PacketHelpers.cs`, view update |
| `PacketDrawObject` `0x78` | Mobile + equipment draw | `Outgoing/ExtendedPackets.cs` | `GameClient.PacketHelpers.cs`, view update |
| `PacketUpdateMobile` `0x77` | Mobile position/appearance update | `Outgoing/ExtendedPackets.cs` | `GameClient.PacketHelpers.cs`, view update |
| `PacketDrawPlayer` `0x20` | Self draw/update | `Outgoing/GamePackets.cs` | login/resync/movement paths |
| `PacketSound` `0x54` | Ses efekti | `Outgoing/ExtendedPackets.cs` | doors, combat, skill/item use, inventory drop |
| `PacketEffect` `0x70` | Görsel efekt | `Outgoing/ExtendedPackets.cs` | spell/combat/death/script effects |
| `PacketTarget` `0x6C` | Target cursor aç | `Outgoing/GamePackets.cs` | `GameClient.PacketHelpers.cs` |
| `PacketGumpDialog` `0xB0` | Gump aç | `Outgoing/ExtendedPackets.cs` | gump/dialog path |
| `PacketSpeechUnicodeOut` `0xAE` | Unicode speech/sysmessage | `Outgoing/GamePackets.cs` | `SysMessage`, overhead label, speech |
| `PacketStatus` / stat packets | Status window/stat update | outgoing files | `GameClient.PacketHelpers.cs`, inventory/status |

Bir outgoing paketin nerede çağrıldığını bulmak için class adını aramak yeterli:

```text
PacketSound
PacketWorldItem
PacketDropAck
PacketContainerItem
```

---

## Davranışlara Göre Paket Sıraları

### Login

```text
0x80 / 0x91 client login
  -> LoginPackets.cs
  -> Program.NetworkHandlers.cs
  -> GameClient.Login.cs
  -> login response packetleri
  -> world draw başlar
```

Tipik outgoing:

| Packet | Anlam |
|---|---|
| `0x8C PacketRelay` | Login server'dan game server'a geçiş |
| `0xA9 PacketCharList` | Karakter listesi |
| `0x1B PacketLoginConfirm` | Dünyaya giriş pozisyonu/body |
| `0xB9 PacketFeatureEnable` | Client feature flags |
| `0xBC PacketSeason` | Season |
| `0x78`, `0x77`, `0x1A`, `0xF3` | Yakındaki obje/mob çizimleri |

---

### Movement

```text
0x02 MoveRequest
  -> PacketMoveRequest
  -> GameClient.QueueMoveRequest
  -> MovementEngine.CanWalkTo
  -> 0x22 ack veya 0x21 reject
  -> yakındaki clientlara mobile update
```

Dosyalar:

| Adım | Dosya |
|---|---|
| Parser | `Incoming/LoginPackets.cs` |
| Handler bridge | `Program.NetworkHandlers.cs` |
| Davranış | `GameClient.Combat.cs` |
| Movement kuralları | `src/SphereNet.Game/Movement/MovementEngine.cs` |
| Outgoing ack/reject | `Outgoing/GamePackets.cs` |

---

### Speech / Command

```text
0x03 veya 0xAD
  -> speech parser
  -> GameClient / SpeechEngine
  -> normal konuşma, command veya script trigger
  -> 0x1C / 0xAE outgoing speech
```

Dosyalar:

| Adım | Dosya |
|---|---|
| ASCII speech parser | `Incoming/LoginPackets.cs` |
| Unicode speech parser | `Incoming/GamePackets.cs` |
| Speech engine | `src/SphereNet.Game/Speech/SpeechEngine.cs` |
| SysMessage/helper | `GameClient.ScriptConsole.cs` |
| Outgoing speech packetleri | `Outgoing/GamePackets.cs` |

---

### Single Click / Name

```text
0x09 SingleClick
  -> PacketSingleClick
  -> GameClient.HandleSingleClick
  -> overhead label gönderilir
```

Kullanılan outgoing genelde `PacketSpeechUnicodeOut` (`0xAE`).
Karakter name hue, notoriety'e göre hesaplanır. `ColorNoto*` ayarları
`sphere.ini` içinden okunup `GameClient.NotorietyHues` olarak kullanılır.

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `Incoming/GamePackets.cs` |
| Name/hue davranışı | `GameClient.Inventory.cs` |
| Notoriety hesapları | `GameClient.ScriptConsole.cs` |
| Outgoing speech | `Outgoing/GamePackets.cs` |

---

### Double Click / Use

```text
0x06 DoubleClick
  -> PacketDoubleClick
  -> GameClient.HandleDoubleClick
  -> item/char/container/door/tool davranışı
  -> ilgili outgoing packetler
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `Incoming/GamePackets.cs` |
| Ana davranış | `GameClient.ItemUse.cs` |
| Container açma helper'ları | `GameClient.PacketHelpers.cs` |
| Door/context gibi world feature'lar | `GameClient.WorldFeatures.cs` |
| Sound/effect packetleri | `Outgoing/ExtendedPackets.cs` |

Örnek: door toggle akışı Source-X parity için iyi bir örnek. Door state değişir,
world item update broadcast edilir ve `PacketSound` gönderilir.

---

## Item Drag Akışları

### Pickup: `0x07`

```text
Client 0x07 gönderir
  -> PacketItemPickup serial + amount okur
  -> GameClient.HandleItemPickup
  -> ölü mü, range uygun mu, housing izin veriyor mu kontrol edilir
  -> @Pickup_* trigger çalışır
  -> item ground/container/equip içinden çıkarılır
  -> item.ContainedIn = karakter UID
  -> karakter TAG.DRAGGING = item UID
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `src/SphereNet.Network/Packets/Incoming/GamePackets.cs` |
| Davranış | `src/SphereNet.Game/Clients/GameClient.Inventory.cs` |
| Pickup fail packet | raw `0x27`, helper inventory/script console tarafında |
| Observer remove | `PacketDeleteObject` / `Outgoing/GamePackets.cs` |

Outgoing:

| Durum | Gönderilen |
|---|---|
| başarısız pickup | `0x27` pickup failed |
| equip üstünden alma | observerlara `0x1D PacketDeleteObject` |
| normal başarılı pickup | genelde ack yok; client drag cursor'u zaten aldı |

Parity notu:

- Pickup sound şu an genel inventory path içinde gönderilmiyor.

---

### Drop: `0x08`

```text
Client 0x08 gönderir
  -> PacketItemDrop serial, x, y, z, container UID okur
  -> GameClient.HandleItemDrop
  -> TAG.DRAGGING doğrulanır
  -> hedefe göre branch edilir
```

Drop branch tablosu:

| Hedef | Davranış | Outgoing |
|---|---|---|
| invalid drag | reject | `0x28 PacketDropReject` |
| trade container | trade container içine koy, acceptance reset | `0x25`, partner update, `0x29` |
| normal container | range/depth/limit kontrol, container'a ekle | `0x25 PacketContainerItem`, `0x29 PacketDropAck` |
| kendi karakteri | pack'e koy | `0x25`, `0x29` |
| başka player | trade başlat | trade packetleri, `0x29` |
| NPC | `@ReceiveItem`, `@NPCRefuseItem`, accept/refuse | `0x29` |
| ground | range/map/house kontrol, `@DropOn_Ground`, world'e koy | `0x29`, world draw sonraki view update |
| stack merge | mevcut stack amount artır, dragged item delete | `0x29`, redraw sonraki view update |

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `Incoming/GamePackets.cs` |
| Ana drop davranışı | `GameClient.Inventory.cs` |
| `PacketDropAck` / `PacketDropReject` | `Outgoing/GamePackets.cs` |
| `PacketContainerItem` | `Outgoing/GamePackets.cs` |
| Ground placement | `src/SphereNet.Game/World/GameWorld.cs` |
| World item redraw | `GameClient.ViewUpdate.cs`, `GameClient.PacketHelpers.cs` |

Ground drop için önemli sıra:

```text
HandleItemDrop
  -> _world.PlaceItemWithDecay(item, groundPos)
  -> _netState.Send(new PacketDropAck())
  -> world/view dirty olur
  -> UpdateClientView
  -> SendWorldItem
  -> 0x1A PacketWorldItem veya 0xF3 PacketWorldItemSA
```

Parity notları:

- Item yere düşünce `PacketSound` (`0x54`) şu anda inventory drop path içinde
  gönderilmiyor.
- World item görseli çoğu zaman anlık broadcast değil, view delta ile bir sonraki
  refresh'te gönderiliyor.
- Stack merge sonrası amount redraw da view delta'ya kalıyor.

Item drop sound davranışını doğrulamak için bakılacak yer:

```text
src/SphereNet.Game/Clients/GameClient.Inventory.cs
  -> HandleItemDrop
  -> başarılı ground/stack-merge branch sonrası
  -> BroadcastNearby(... new PacketSound(...))
```

Güncel kod `GetDropSound` ile altın miktarına göre 0x2E4-0x2E6, diğerleri için
0x42 seçer. Daha ileri Source-X paritesi için item/tiledata bazlı ses tablosu
ayrıca incelenmelidir.

---

### Equip: `0x13`

```text
Client 0x13 gönderir
  -> PacketItemEquip serial + layer + char UID okur
  -> GameClient.HandleItemEquip
  -> ownership/layer kontrol
  -> @EquipTest
  -> Character.Equip
  -> PacketWornItem gönderilir
  -> @Equip
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `Incoming/GamePackets.cs` |
| Davranış | `GameClient.Inventory.cs` |
| Equip packet | `PacketWornItem` / `Outgoing/GamePackets.cs` |
| Character equip state | `src/SphereNet.Game/Objects/Characters/Character.cs` |

Outgoing:

| Alıcı | Paket |
|---|---|
| itemi giyen client | `0x2E PacketWornItem` |
| yakındaki observerlar | `0x2E PacketWornItem` broadcast |

Parity notu:

- Genel equip/drop sound inventory path içinde yok.

---

## World Item / Mobile Görünürlük Sistemi

SphereNet çoğu world görünümünü tek tek davranış içinde hemen yollamak yerine
view delta sistemiyle yollar.

```text
Game state değişir
  -> obje/client dirty olur
  -> GameClient.UpdateClientView
  -> BuildViewDelta
  -> ApplyViewDelta
  -> gerekli draw/update/delete packetleri gönderilir
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Delta build/apply | `GameClient.ViewUpdate.cs` |
| World item packet helper | `GameClient.PacketHelpers.cs` |
| World item packet class | `Outgoing/ExtendedPackets.cs` |
| Delete object packet | `Outgoing/GamePackets.cs` |

Giden packetler:

| Packet | Ne için? |
|---|---|
| `0x1A PacketWorldItem` | legacy ground item draw/update |
| `0xF3 PacketWorldItemSA` | SA+ ground item draw/update |
| `0x1D PacketDeleteObject` | item/mob client view'dan sil |
| `0x78 PacketDrawObject` | mobile + equipment çiz |
| `0x77 PacketUpdateMobile` | mobile pozisyon/görünüm update |
| `0x20 PacketDrawPlayer` | self draw/update |

Bir item state değişti ama client görmüyorsa şunları kontrol et:

1. Item gerçekten değişti mi?
2. Item ground'da mı, container'da mı, equipped mi?
3. Dirty/view refresh tetiklendi mi?
4. `BuildViewDelta` itemi görüyor mu?
5. `ApplyViewDelta` değişen alanı fark ediyor mu?
6. `BuildWorldItemPacket` doğru graphic/hue/amount/direction gönderiyor mu?

---

## Sound ve Effect Paketleri

Sound/effect packet sistemi var. Eksik olan çoğu zaman packet class değil,
davranışın o packet'i çağırmaması.

| Packet | Dosya | Kullanım |
|---|---|---|
| `PacketSound` `0x54` | `Outgoing/ExtendedPackets.cs` | door, combat, bazı item use/skill feedback |
| `PacketEffect` `0x70` | `Outgoing/ExtendedPackets.cs` | spell, combat, death, script effect |

`NetState` slow client durumunda bazı cosmetic paketleri düşürebilir:

| Droppable | Neden |
|---|---|
| `0x54` sound | state taşımaz, düşerse desync olmaz |
| `0x1C` / `0xAE` speech | cosmetic text |

Item yere düşünce ses doğrulaması için en olası kontrol noktası:

```text
PacketSound var
GameClient.Inventory.HandleItemDrop içinde ground/stack-merge başarı yolunda çağrılıyor
```

---

## Item Flip Notları

Şu anki durum:

| Konu | Durum | Dosya |
|---|---|---|
| item `FLIP` verb | bazı item logic içinde graphic değiştirebiliyor | `src/SphereNet.Game/Objects/Items/Item.cs` |
| `ItemDef.FlipId` | parse ediliyor ama tüm akışlarda kullanılmıyor | item definition dosyaları |
| `CanFlags.I_Flip` | enum olarak var | `src/SphereNet.Core/Enums/CanFlags.cs` |
| world item direction | packet tarafında destek var | `Outgoing/ExtendedPackets.cs` |
| `BuildWorldItemPacket` | direction çoğunlukla default gidiyor | `GameClient.PacketHelpers.cs` |
| double click ile genel item flip | genel davranış olarak bağlı değil | `GameClient.ItemUse.cs` |

Source-X parity için beklenen genel yaklaşım:

```text
item double click veya FLIP verb
  -> item graphic/direction değişir
  -> item dirty olur
  -> gerekiyorsa anlık BroadcastNearby(PacketWorldItem)
  -> gerekiyorsa PacketSound
```

Bakılacak dosyalar:

| Ne için? | Dosya |
|---|---|
| double click'te flip bağlamak | `GameClient.ItemUse.cs` |
| item graphic/flip state değiştirmek | `Objects/Items/Item.cs` |
| flip id / item def verisi | item definition loader dosyaları |
| client'a world item redraw göndermek | `GameClient.PacketHelpers.cs`, `GameClient.ViewUpdate.cs` |
| ses eklemek | `PacketSound` / `Outgoing/ExtendedPackets.cs` |

Door toggle mevcutta iyi örnek: graphic değiştiriyor, world item update
broadcast ediyor, sound gönderiyor. Genel item flip aynı mantığa yaklaştırılabilir.

---

## Target Cursor Akışı

Server target cursor açar:

```text
GameClient.BeginXxxTarget
  -> _netState.Send(new PacketTarget(...))  // 0x6C
```

Client cevap verir:

```text
0x6C PacketTargetResponse
  -> state.OnTargetResponse
  -> Program.OnTargetResponse
  -> GameClient.HandleTargetResponse
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Target outgoing | `PacketTarget` / `Outgoing/GamePackets.cs` |
| Target parser | `PacketTargetResponse` / `Incoming/GamePackets.cs` |
| Pending target state | `GameClient.cs`, `GameClient.PacketHelpers.cs` |
| Target davranışları | `GameClient.PacketHelpers.cs`, `GameClient.ItemUse.cs`, command paths |

---

## Gump / Dialog Akışı

Server gump açar:

```text
PacketGumpDialog 0xB0
veya input için PacketGumpValueInput 0xAB
```

Client cevap verir:

```text
0xB1 PacketGumpResponse
0xAC PacketGumpTextEntry
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Gump response parser | `Incoming/GamePackets.cs` |
| Gump outgoing packet | `Outgoing/ExtendedPackets.cs`, `Outgoing/GamePackets.cs` |
| Dialog script/native dispatch | `GameClient.ScriptConsole.cs`, gump/helper dosyaları |

---

## 0xBF Extended ve 0xD7 Encoded Ayrımı

`0xBF` extended command:

```text
PacketExtendedCommand
  -> subcommand oku
  -> registered extended handler varsa onu çalıştır
  -> yoksa state.OnExtendedCommand
  -> GameClient.HandleExtendedCommand
```

Dosyalar:

| Konu | Dosya |
|---|---|
| Parser | `Incoming/GamePackets.cs` |
| Known subcommand listesi | `ExtendedCommandRegistry` |
| Davranış | `GameClient.WorldFeatures.cs` |
| Dokümantasyon | `PROTOCOL_MATRIX.md` |

`0xD7` encoded command ayrı tutulur. Çünkü `0xD7` subcommand alanı ile `0xBF`
subcommand alanı çakışabilir. House/custom design gibi işler buradan akar.

---

## Source-X Parity Eksiklerini Arama Rehberi

Bir davranış Source-X gibi çalışmıyorsa şu soruları sor:

1. Client paketi parse ediliyor mu?
2. `NetState.OnXxx` çağrılıyor mu?
3. `Program.NetworkHandlers` doğru `GameClient` metodunu çağırıyor mu?
4. `GameClient` içinde Source-X'teki trigger sırası var mı?
5. Game state değişiyor mu?
6. Client'a gerekli outgoing packet gönderiliyor mu?
7. Gerekli packet hemen mi gönderilmeli, yoksa view delta yeterli mi?
8. Sound/effect gerekiyorsa `PacketSound` veya `PacketEffect` çağrılıyor mu?
9. World item redraw için `0x1A`/`0xF3` gidiyor mu?
10. Packet sırası Source-X ile aynı mı?

Örnek: item yere bırakınca ses yok.

```text
0x08 parse ediliyor
HandleItemDrop çalışıyor
DropAck gidiyor
World item view delta ile çiziliyor
Ama PacketSound çağrısı yok
```

Örnek: item flip olmuyor.

```text
DoubleClick veya FLIP verb nerede çalışıyor?
Item graphic/direction değişiyor mu?
ItemDef.FlipId kullanılıyor mu?
Client'a PacketWorldItem redraw gidiyor mu?
Sound gerekiyorsa PacketSound var mı?
```

---

## Hızlı Dosya Rehberi

| Aradığın şey | İlk bakılacak dosya |
|---|---|
| Opcode length | `PacketDefinitions.cs` |
| Incoming packet var mı? | `NetworkManager.RegisterStandardPackets()` |
| Incoming parser | `Incoming/LoginPackets.cs`, `Incoming/GamePackets.cs` |
| Outgoing packet class | `Outgoing/GamePackets.cs`, `Outgoing/ExtendedPackets.cs` |
| Network dispatch | `NetworkManager.cs`, `NetState.cs` |
| NetState -> GameClient bridge | `Program.NetworkHandlers.cs` |
| Item pickup/drop/equip | `GameClient.Inventory.cs` |
| Double click/use | `GameClient.ItemUse.cs` |
| World item draw/update | `GameClient.ViewUpdate.cs`, `GameClient.PacketHelpers.cs` |
| Sound/effect | `PacketSound`, `PacketEffect` in `Outgoing/ExtendedPackets.cs` |
| Movement | `GameClient.Combat.cs`, `MovementEngine.cs` |
| Gump/dialog | `Incoming/GamePackets.cs`, gump helper/script files |
| Extended command | `GameClient.WorldFeatures.cs`, `ExtendedCommandRegistry` |

---

## Yeni Paket Eklerken

Incoming packet ekleme:

1. `Incoming/*.cs` içinde `PacketHandler` class ekle.
2. `NetworkManager.RegisterStandardPackets()` içine register et.
3. `NetState` içine `OnXxx` ve delegate property ekle.
4. `NetworkManager.SetHandlers(...)` içinde delegate'i bağla.
5. `Program.NetworkHandlers.cs` içine `OnXxx` ekle.
6. `GameClient.HandleXxx` davranışını yaz.
7. Test ekle.
8. `PROTOCOL_MATRIX.md` ve bu rehberi güncelle.

Outgoing packet ekleme:

1. `Outgoing/GamePackets.cs` veya `Outgoing/ExtendedPackets.cs` içine
   `PacketWriter` class ekle.
2. Tek client için `_netState.Send(new PacketXxx(...))` kullan.
3. Yakındakilere göndermek için `BroadcastNearby` kullan.
4. Ortak davranışsa helper fonksiyon yaz.
5. Packet sequence testi veya behavior testi ekle.

---

## En Önemli Not

SphereNet'te packet sistemi büyük ölçüde merkezi ve reusable. Eksik hissettiren
Source-X davranışlarının çoğu "packet yok" değil, "davranış içinde doğru packet
çağrısı eksik" tipindedir.

Özellikle item drop sound, item flip ve view refresh için bakılacak ilk yerler:

- `GameClient.Inventory.cs`
- `GameClient.ItemUse.cs`
- `GameClient.PacketHelpers.cs`
- `GameClient.ViewUpdate.cs`
- `Outgoing/ExtendedPackets.cs`
- `Objects/Items/Item.cs`
# Packet Flow Guide for Source-X Developers

This guide explains how SphereNet reads client packets, where game behavior is
called, and how outgoing packets are sent. It is written for developers who know
Source-X/Sphere 56x and want to find packet gaps such as missing item drop sounds,
item flip refreshes, or incomplete visual updates.

For a strict opcode coverage list, see [PROTOCOL_MATRIX.md](PROTOCOL_MATRIX.md).
This document focuses on flow and behavior.

---

## Source-X Mapping

| Source-X concept | SphereNet equivalent | Main files |
|---|---|---|
| `CNetworkManager` | `NetworkManager` | `src/SphereNet.Network/Manager/NetworkManager.cs` |
| `CNetState` | `NetState` | `src/SphereNet.Network/State/NetState.cs` |
| `CPacketManager` / packet table | `PacketManager`, `PacketDefinitions` | `src/SphereNet.Network/Packets/` |
| receive packet class | `PacketHandler` | `src/SphereNet.Network/Packets/Incoming/` |
| send packet class | `PacketWriter` | `src/SphereNet.Network/Packets/Outgoing/` |
| `CClient` game behavior | `GameClient` partial class | `src/SphereNet.Game/Clients/` |

The important architectural difference is that incoming packet classes do not
run gameplay directly. They parse bytes, then call a `NetState` delegate. The
server layer looks up the `GameClient`, and only then does game logic run.

---

## Incoming Packet Dispatch

Incoming packets follow this chain:

```text
socket bytes
  -> NetworkManager.ProcessInput
  -> PacketDefinitions.GetPacketLength(opcode, state)
  -> PacketManager.GetHandler(opcode)
  -> PacketHandler.OnReceive(buffer, state)
  -> NetState.OnSomething(...)
  -> Program.NetworkHandlers.OnSomething(...)
  -> GameClient.HandleSomething(...)
```

Where to look:

- Packet length table: `src/SphereNet.Network/Packets/PacketDefinitions.cs`
- Incoming parser classes: `src/SphereNet.Network/Packets/Incoming/*.cs`
- Registration: `NetworkManager.RegisterStandardPackets()`
- Runtime delegate wiring: `NetworkManager.SetHandlers(...)`
- Server bridge handlers: `src/SphereNet.Server/Program.NetworkHandlers.cs`
- Game behavior: `src/SphereNet.Game/Clients/GameClient.*.cs`

Example, double click:

```text
0x06 client packet
  -> PacketDoubleClick.OnReceive
  -> state.OnDoubleClick(serial)
  -> Program.OnDoubleClick
  -> client.HandleDoubleClick(serial)
  -> item/NPC/action logic
```

Example, item drop:

```text
0x08 client packet
  -> PacketItemDrop.OnReceive
  -> state.OnItemDrop(serial, x, y, z, container)
  -> Program.OnItemDrop
  -> client.HandleItemDrop(...)
```

---

## Outgoing Packet Pattern

No, SphereNet should not repeatedly hand-write the same packet bytes everywhere.
The normal pattern is:

1. Define a reusable `PacketWriter` class once.
2. Send it with `_netState.Send(new PacketXxx(...))`.
3. For common behavior, call a helper such as `SendWorldItem`, `SendDrawObject`,
   `PlaceItemInPack`, `SysMessage`, `BroadcastNearby`, or `SendCharacterStatus`.

Outgoing packet definitions live mainly in:

- `src/SphereNet.Network/Packets/Outgoing/GamePackets.cs`
- `src/SphereNet.Network/Packets/Outgoing/ExtendedPackets.cs`

Typical send styles:

```csharp
_netState.Send(new PacketDropAck());
_netState.Send(new PacketContainerItem(...));
_netState.SendPriority(new PacketMoveAck(...));
BroadcastNearby?.Invoke(position, range, new PacketSound(...), excludeUid);
```

For broadcasts, the server usually builds the packet once and shares the buffer
to every recipient through `BroadcastNearby` / `NetState.EnqueueShared`. This is
the equivalent of Source-X sending one packet type from a central send class, not
copy-pasting raw bytes in every behavior.

Raw buffers still exist in a few compatibility spots, for example pickup-failed
`0x27`, but new work should prefer a named `PacketWriter`.

---

## Where GameClient Is Split

`GameClient` is partial, so behavior is separated by domain:

| File | Purpose |
|---|---|
| `GameClient.cs` | core fields, delegates, constructor |
| `GameClient.Login.cs` | login, character select, enter world |
| `GameClient.Combat.cs` | movement queue, walk ack/reject, war, attack |
| `GameClient.Inventory.cs` | pickup, drop, equip, containers, status/profile |
| `GameClient.ItemUse.cs` | double click / use actions |
| `GameClient.PacketHelpers.cs` | draw, status, container, target, tooltip helpers |
| `GameClient.ViewUpdate.cs` | visible object delta and world draw/remove packets |
| `GameClient.WorldFeatures.cs` | doors, context menu, 0xBF features |
| `GameClient.ScriptConsole.cs` | commands, sysmessage, notoriety helpers |

When comparing to Source-X `CClient`, start in the incoming packet parser, then
jump to the matching `GameClient` partial file.

---

## Behavior Packet Sequences

### Login And Enter World

Typical path:

```text
0x80 LoginRequest / 0x91 GameLogin
  -> LoginPackets.cs parser
  -> Program.NetworkHandlers
  -> GameClient.HandleGameLogin / char selection flow
  -> outgoing relay/list/login/world packets
```

Important outgoing packets include:

| Packet | Meaning |
|---|---|
| `PacketRelay` (`0x8C`) | relay from login to game server |
| `PacketCharList` (`0xA9`) | character list |
| `PacketLoginConfirm` (`0x1B`) | enter world position/body |
| `PacketFeatureEnable` (`0xB9`) | enabled client feature flags |
| `PacketSeason` (`0xBC`) | season |
| `PacketMapChange` / map packets | map selection |
| world draw packets | nearby mobiles/items after login |

After login, object visibility is mostly driven by `UpdateClientView()`.

---

### Movement

Incoming:

```text
0x02 PacketMoveRequest
  -> state.OnMoveRequest
  -> Program.OnMoveRequest
  -> GameClient.QueueMoveRequest
  -> movement queue / MovementEngine.CanWalkTo
```

Outgoing:

| Packet | Meaning |
|---|---|
| `0x22 PacketMoveAck` | movement accepted |
| `0x21 PacketMoveReject` | movement rejected/resync |
| `0x77` / `0x78` / `0x20` | update/draw mobile for observers or self |

Movement ack/reject uses high priority queues in `NetState`, so it does not wait
behind lower-priority world/UI traffic.

New movement (`0xF0`) is routed separately for newer clients and then normalized
into the same movement handling.

---

### Speech And Commands

Incoming:

| Opcode | Parser | Behavior |
|---|---|---|
| `0x03` | `PacketSpeechRequest` | ASCII speech |
| `0xAD` | `PacketSpeechUnicode` | Unicode/encoded speech |
| `0x12` | `PacketTextCommand` | command text path |

Flow:

```text
speech packet
  -> state.OnSpeech / OnTextCommand
  -> Program.NetworkHandlers
  -> SpeechEngine / CommandHandler through GameClient
```

Outgoing speech is usually:

| Packet | Use |
|---|---|
| `0x1C` | ASCII speech/system text |
| `0xAE` | Unicode speech/system text |

`GameClient.SysMessage(...)` is the common helper for system messages.

---

### Single Click / Names

Incoming:

```text
0x09 PacketSingleClick
  -> GameClient.HandleSingleClick
  -> PacketSpeechUnicodeOut overhead label
```

Name hue for characters is computed from notoriety in `GameClient.Inventory.cs`.
The `ColorNoto*` values are now loaded from `sphere.ini` through `SphereConfig`
and assigned at startup to `GameClient.NotorietyHues`.

---

### Double Click / Use

Incoming:

```text
0x06 PacketDoubleClick
  -> GameClient.HandleDoubleClick
  -> item/char/door/container/action handling
```

Common outgoing packets:

| Packet | Use |
|---|---|
| `0x24 PacketOpenContainer` | open container |
| `0x3C PacketContainerContent` | send container contents |
| `0x1A` / `0xF3` | redraw world item after use |
| `0x54 PacketSound` | sounds for doors/actions where implemented |
| `0x70 PacketEffect` | visual effects |
| `0x6C PacketTarget` | open target cursor |

Door toggling is a useful reference for Source-X-style immediate visual and sound
behavior: it changes the item graphic, broadcasts a world item update, then sends
`PacketSound`.

---

### Target Cursors

Outgoing target:

```text
GameClient.BeginXxxTarget
  -> _netState.Send(new PacketTarget(...))  // 0x6C
```

Incoming response:

```text
0x6C PacketTargetResponse
  -> state.OnTargetResponse(...)
  -> Program.OnTargetResponse
  -> GameClient.HandleTargetResponse(...)
```

Many commands and spells store pending state in `GameClient` before sending the
target cursor. When the client responds, `HandleTargetResponse` consumes that
pending state and runs the correct behavior.

---

### Gumps / Dialogs

Outgoing:

| Packet | Use |
|---|---|
| `0xB0 PacketGumpDialog` | normal gump |
| `0xDD` | compressed gump, where implemented |
| `0xAB` | text input dialog |

Incoming:

| Packet | Use |
|---|---|
| `0xB1 PacketGumpResponse` | button/switch/text response |
| `0xAC PacketGumpTextEntry` | input text response |

Runtime path:

```text
GameClient opens gump
  -> stores callback/pending gump state
  -> client sends 0xB1 or 0xAC
  -> GameClient dispatches callback or script dialog handler
```

---

### Containers

Opening a container usually sends:

```text
0x24 PacketOpenContainer
0x3C PacketContainerContent
```

Adding/moving one item inside an already open container usually sends:

```text
0x25 PacketContainerItem
```

Important helpers:

- `SendOpenContainer`
- `SendContainerContents`
- `PlaceItemInPack`
- `PacketContainerItem`

If an item disappears from the container UI after a drag/drop, check whether the
drop path sent `0x25` after mutating container state.

---

## Item Drag Flows

These are the flows most useful for investigating Source-X parity gaps.

### Pickup (`0x07`)

```text
client sends 0x07 serial + amount
  -> PacketItemPickup
  -> GameClient.HandleItemPickup
  -> validate dead/range/housing/triggers
  -> split stack if needed
  -> remove from equipment/container/sector
  -> set item.ContainedIn = character UID
  -> set character TAG.DRAGGING = item UID
```

Outgoing:

| Case | Packet behavior |
|---|---|
| fail | raw `0x27` pickup failed |
| ground/container success | usually no ack; client already owns drag cursor |
| equipped item | broadcasts `0x1D PacketDeleteObject` to observers |

Current parity note: pickup does not send a pickup sound.

---

### Drop (`0x08`)

```text
client sends 0x08 serial + x/y/z + container UID
  -> PacketItemDrop
  -> GameClient.HandleItemDrop
  -> verify TAG.DRAGGING
  -> branch by drop target
```

Target branches:

| Target | Behavior | Outgoing |
|---|---|---|
| invalid drag | reject | `0x28 PacketDropReject` |
| trade container | add to trade container, reset acceptance | `0x25`, partner update, `0x29` |
| normal container | validate reach/depth/limits, add item | `0x25`, `0x29` |
| self | place in pack | `0x25`, `0x29` |
| other player | initiate trade | trade packets, `0x29` |
| NPC | `@ReceiveItem`, `@NPCRefuseItem`, maybe accept pack | `0x29` |
| ground | validate range/map/house, `@DropOn_Ground`, place item | `0x29`; world draw later |
| stack merge | merge amount, delete dragged item | `0x29`; existing item redraw later |

Ground drops rely on the view delta system to send the world item draw/update on
the next view refresh:

```text
PlaceItemWithDecay
  -> world/sector state changes
  -> dirty nearby clients
  -> GameClient.UpdateClientView
  -> SendWorldItem
  -> 0x1A PacketWorldItem or 0xF3 PacketWorldItemSA
```

Current parity notes:

- Drop sound is sent from `HandleItemDrop` on accepted ground/stack-merge paths.
- Ground placement does not immediately broadcast a `PacketWorldItem`; it relies
  on dirty/view refresh. This is usually fine, but Source-X often feels more
  immediate for some item actions.
- Stack merge sends a sound; amount redraw still depends on dirty/view refresh.

---

### Equip (`0x13`)

```text
client sends 0x13 serial + layer + target char
  -> PacketItemEquip
  -> GameClient.HandleItemEquip
  -> validate ownership/layer
  -> @EquipTest
  -> handle 2H/shield swap
  -> Character.Equip
  -> send worn item
  -> @Equip
```

Outgoing:

| Packet | Recipient |
|---|---|
| `0x2E PacketWornItem` | self |
| `0x2E PacketWornItem` | nearby observers via `BroadcastNearby` |

Current parity note: equip does not send an equip/drop sound unless some script or
separate behavior sends one.

---

## World Visibility And Draw Packets

World object drawing is mostly centralized in `GameClient.ViewUpdate.cs`.

```text
Game/world mutation
  -> mark object/client dirty
  -> GameClient.UpdateClientView
  -> BuildViewDelta
  -> ApplyViewDelta
  -> SendDrawObject / SendWorldItem / PacketDeleteObject
```

Important outgoing packets:

| Packet | Meaning |
|---|---|
| `0x78 PacketDrawObject` | draw mobile with equipment |
| `0x77 PacketUpdateMobile` | update mobile movement/appearance |
| `0x20 PacketDrawPlayer` | draw/update self in some flows |
| `0x1A PacketWorldItem` | draw/update world item for legacy clients |
| `0xF3 PacketWorldItemSA` | draw/update world item for SA+ clients |
| `0x1D PacketDeleteObject` | remove object from client view |

`GameClient.PacketHelpers.cs` contains the helpers:

- `SendDrawObject`
- `SendUpdateMobile`
- `SendWorldItem`
- `BuildWorldItemPacket`
- `SendWorldItemAllShow`
- `SendDrawObjectHidden`

When an object appears stale, ask:

1. Did the game state actually change?
2. Was the object/client marked dirty?
3. Is it visible in `BuildViewDelta`?
4. Does `ApplyViewDelta` see changed position/body/hue/amount?
5. Is the right packet emitted for this client era?

---

## Sound And Effects

Sound and effects are normal outgoing packets, not special engine events.

| Packet | Class | Typical use |
|---|---|---|
| `0x54` | `PacketSound` | doors, combat, item use, skill feedback |
| `0x70` | `PacketEffect` | spells, combat, death, scripted effects |

To verify Source-X-like item drop sound, the relevant hook is:

```text
GameClient.Inventory.HandleItemDrop
  -> after accepted ground/stack-merge branch
  -> BroadcastNearby(... new PacketSound(...))
```

The packet system and ground/stack-merge call are present. The remaining parity
work is richer item/tile-data sound selection beyond the current `GetDropSound`
rules.

`NetState` treats sound packets as droppable under heavy backpressure. Dropping a
sound may lose feedback but will not desync the client.

---

## Item Flip / Orientation Notes

Current state:

| Area | Status |
|---|---|
| `Item` verb `FLIP` | toggles item graphic in item script/object logic |
| `ItemDef.FlipId` | parsed but not fully used by every flow |
| `CanFlags.I_Flip` | defined, not broadly enforced in inventory/dclick paths |
| world item packet direction | supported by packet structs |
| `BuildWorldItemPacket` | currently sends item direction as the default value |
| double click to flip item | not a general inventory behavior today |

Likely Source-X parity work:

1. Decide where flip should happen: script verb only, double-click, or both.
2. Use `ItemDef.FlipId`/`I_Flip` instead of guessing `BaseId ^ 1` where possible.
3. After changing graphic/direction, trigger a world item refresh:

```text
item graphic/direction changes
  -> mark item dirty
  -> immediate BroadcastNearby(PacketWorldItem/PacketWorldItemSA) if needed
  -> optional PacketSound
```

Door toggling in `GameClient.WorldFeatures.cs` is the best existing example:
change graphic/state, broadcast world item update, play sound.

---

## 0xBF Extended And 0xD7 Encoded Commands

`0xBF` is the extended-command router:

```text
PacketExtendedCommand
  -> read subcommand
  -> PacketManager registered extended handler if present
  -> otherwise state.OnExtendedCommand
  -> GameClient.HandleExtendedCommand
```

Known subcommands are documented in `PROTOCOL_MATRIX.md` and centralized in
`ExtendedCommandRegistry`.

`0xD7` encoded commands are intentionally separate:

```text
PacketEncodedCommand
  -> state.OnEncodedCommand
  -> GameClient house/custom design path
```

Do not merge `0xD7` with `0xBF`; their subcommand spaces overlap.

---

## Adding Or Fixing A Packet

### Incoming Client Packet

1. Add or update a `PacketHandler` in `src/SphereNet.Network/Packets/Incoming/`.
2. Register it in `NetworkManager.RegisterStandardPackets()`.
3. Add a `NetState.OnXxx(...)` method and handler delegate if it is a new action.
4. Wire the delegate in `NetworkManager.SetHandlers(...)`.
5. Add `Program.NetworkHandlers.OnXxx(...)`.
6. Add or call the correct `GameClient.HandleXxx(...)`.
7. Add/update tests and `PROTOCOL_MATRIX.md`.

### Outgoing Server Packet

1. Add a `PacketWriter` in `Outgoing/GamePackets.cs` or `Outgoing/ExtendedPackets.cs`.
2. Use `_netState.Send(new PacketXxx(...))` for one client.
3. Use `BroadcastNearby` for nearby observers.
4. Use a helper if the packet is a common behavior.
5. Add a focused test or packet roundtrip test.

### Behavior Parity Fix

For Source-X parity gaps, the packet class often already exists. The missing work
is usually one of these:

- the behavior did not call an outgoing packet helper;
- the world change was not marked dirty;
- the view delta ignores the changed field;
- packet order differs from Source-X;
- the correct sound/effect/animation ID is missing from definitions;
- a trigger fires on items but not on char `EVENTS`, or vice versa.

---

## Quick Opcode Reference By Behavior

| Behavior | Incoming | Outgoing |
|---|---|---|
| login | `0x80`, `0x91`, `0xA0`, `0x5D` | `0x8C`, `0xA9`, `0x1B`, `0xB9`, world draw |
| movement | `0x02`, `0xF0` | `0x22`, `0x21`, `0x77`, `0x78`, `0x20` |
| speech/commands | `0x03`, `0xAD`, `0x12` | `0x1C`, `0xAE` |
| single click/name | `0x09`, `0x98` | `0xAE` overhead label |
| double click/use | `0x06` | depends: `0x24`, `0x3C`, `0x6C`, `0x54`, `0x70`, world draw |
| pickup | `0x07` | fail `0x27`, sometimes `0x1D` to observers |
| drop | `0x08` | `0x28`, `0x29`, `0x25`, world draw later |
| equip | `0x13` | `0x2E` |
| target | `0x6C` response | `0x6C` target cursor |
| gump | `0xB1`, `0xAC` | `0xB0`, `0xAB`, sometimes `0xDD` |
| vendor | `0x3B`, `0x9F` | buy/sell list packets |
| trade | `0x6F` | secure trade packets, container item packets |
| tooltip | `0xD6`, `0xB6` | `0xD6`/AOS tooltip responses |
| sound/effect | none usually | `0x54`, `0x70` |

---

## Practical Debug Checklist

When a Source-X behavior feels wrong, for example "item drops but no sound" or
"item flips in state but not on the client":

1. Find the incoming opcode in `PROTOCOL_MATRIX.md`.
2. Open the parser in `Incoming/*.cs`.
3. Follow `state.OnXxx` to `Program.NetworkHandlers`.
4. Open the target `GameClient.*.cs` method.
5. Check which outgoing packets are sent immediately.
6. Check whether a deferred view update is expected instead.
7. If deferred, check `GameClient.ViewUpdate.cs` and `BuildWorldItemPacket`.
8. Compare packet order with Source-X.
9. Add the missing `PacketWriter` call or dirty/view update.
10. Add a test that asserts the expected packet sequence.

For item drop sound specifically: `PacketSound` exists and `HandleItemDrop` calls
it on accepted ground/stack-merge paths. For item flip: the server can mutate item
graphic state, but a full Source-X-like double-click/flip/refresh/sound path is
not wired as a general item behavior yet.
