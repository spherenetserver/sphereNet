# Ses, Görüntü ve Hareket Paketleri — Source-X ↔ SphereNet Parite Rehberi

Bu doküman `tools/source-reference` (Source-X) içindeki **ses**, **görsel efekt** ve **hareket/görünüm güncelleme** paketlerinin tam envanterini ve SphereNet’teki karşılıklarını kaydeder. Sonraki parity çalışmalarında tek referans noktası olarak kullanılmalıdır.

İlgili dosyalar:
- Source-X paket tanımları: `tools/source-reference/src/network/send.h`, `send.cpp`
- Source-X client API: `tools/source-reference/src/graysvr/CClient.h`, `CClientMsg.cpp`
- Source-X obje komutları: `tools/source-reference/src/graysvr/CObjBase.cpp`, `CCharAct.cpp`
- SphereNet paket sınıfları: `src/SphereNet.Network/Packets/Outgoing/`
- SphereNet davranış: `src/SphereNet.Game/Clients/`, `Movement/`, `Program.EngineWiring.cs`

---

## 1. Source-X — Üst seviye API (paket öncesi katman)

Source-X’te oyun kodu doğrudan opcode yazmaz; `CClient` üzerinden gider:

| API | Gönderilen paket(ler) | Dosya |
|-----|----------------------|-------|
| `addSound(id, src, repeat)` | `0x54 PacketPlaySound` | `CClientMsg.cpp:710` |
| `addEffect(...)` | `0x70` / `0xC0` / `0xC7 PacketEffect` (parametreye göre) | `CClientMsg.cpp:975` |
| `addAction` (dolaylı: `CChar::UpdateAnimate`) | `0x6E PacketAction` veya `0xE2 PacketActionNew` | `CCharAct.cpp:1064` |
| `addCharMove(char[, dir])` | `0x77 PacketCharacterMove` | `CClientMsg.cpp:1107` |
| `addChar(char)` | `0x78 PacketCharacter` (tam spawn) | `CClientMsg.cpp:1121` |
| `addPlayerUpdate()` | `0x20 PacketPlayerUpdate` + walk seq sıfırlama | `CClientMsg.cpp:1969` |
| `addObjectRemove(uid)` | `0x1D PacketRemoveObject` | `CClient.h:671` |
| `addLight()` | `0x4F PacketGlobalLight` | `CClientMsg.cpp:657` |
| `addSeason(season)` | `0xBC PacketSeason` + `addLight()` | `CClientMsg.cpp:623` |
| `addWeather(weather)` | `0x65 PacketWeather` | `CClientMsg.cpp:639` |
| `addMusic(id)` | `0x6D PacketPlayMusic` | `CClientMsg.cpp:683` |
| `addSpeedMode(mode)` | `0xBF.0x26 PacketSpeedMode` | `CClientMsg.cpp:3324` |
| `addSwing(defender)` | `0x2F PacketSwing` | `send.h` |
| Drag (pickup/drop) | `0x23 PacketDragAnimation` | `send.cpp:699` |

Obje/script komutları (`CObjBase`):
- `SOUND id[,repeat]` → yakındaki clientlara `addSound` (`CObjBase.cpp:329`)
- `EFFECT ...` → yakındaki clientlara `addEffect` (`CObjBase.cpp:346`)
- `ANIM ...` → `CChar::UpdateAnimate` → `PacketAction` / `PacketActionNew`

---

## 2. Source-X — Ses ve görüntü paketleri (outgoing)

### 2.1 Ses / müzik / ortam

| Opcode | Sınıf | Amaç | Öncelik |
|--------|-------|------|---------|
| `0x54` | `PacketPlaySound` | Konumlu ses efekti (mode, volume, x,y,z) | NORMAL |
| `0x6D` | `PacketPlayMusic` | MIDI müzik parçası | IDLE |
| `0x65` | `PacketWeather` | Yağmur/fırtına/kar partikülleri | IDLE |
| `0xBC` | `PacketSeason` | Mevsim değişimi (+ isteğe bağlı ses) | NORMAL |

**Tetiklenme örnekleri (Source-X):**
- Item drop: `CClientEvent.cpp` → `GetDropSound()` ile item’a özel ses
- Altın drop: `SOUND_DROP_GOLD1/2`
- Kapı: item `IT_SOUND` tipi
- Savaş: swing/hit/miss sesleri (`CCharFight.cpp`)
- Büyü: cast/fizzle/resurrect efekt sesleri (`CCharSpell.cpp`)
- Sector tick: rüzgar vb. ambient ses (`CSector.cpp:928`)
- Hallucination: rastgele ses (`CCharSpell.cpp:1826`)
- `g_Cfg.m_fGenericSounds` kapalıysa tüm `addSound` sessiz

### 2.2 Görsel efekt / animasyon

| Opcode | Sınıf | Amaç |
|--------|-------|------|
| `0x6E` | `PacketAction` | Klasik animasyon (pre-SA) |
| `0xE2` | `PacketActionNew` | SA+ vücut-bağımsız animasyon |
| `0x70` | `PacketEffect` | Temel grafik efekt (bolt, explode, fixed) |
| `0xC0` | `PacketEffect` (ctor 2) | Renkli/hued efekt (`dwColor`, `dwRender`) |
| `0xC7` | `PacketEffect` (ctor 3) | Parçacık efekt (`wEffectID`, `wExplodeID`, `wExplodeSound`, `dwItemUID`, `bLayer`) |
| `0x23` | `PacketDragAnimation` | Sürükle-bırak animasyonu |
| `0x2C` | `PacketDeathMenu` | Ölüm menüsü/efekt |
| `0xAF` | `PacketDeath` | Ölüm animasyonu + ceset |
| `0x2F` | `PacketSwing` | Saldırı swing bildirimi (saldırgana) |

**`addEffect` seçim mantığı (`CClientMsg.cpp:975`):**
```
wEffectID || wExplodeID  → 0xC7 (particle)
dwColor || dwRender      → 0xC0 (hued)
else                     → 0x70 (basic)
```

### 2.3 Işık / görünüm / dünya objesi

| Opcode | Sınıf | Amaç |
|--------|-------|------|
| `0x4F` | `PacketGlobalLight` | Global ışık seviyesi |
| `0x4E` | (client protokolü; Source-X personal light ayrı paket) | — |
| `0x1A` | `PacketWorldItem` | Yerdeki item |
| `0xF3` | `PacketWorldObj` | SA+ yerdeki obje (item veya mobile) |
| `0x1D` | `PacketRemoveObject` | Görünümden kaldır |
| `0x20` | `PacketPlayerUpdate` | Kendi karakterinin body/hue/pozisyon yenilemesi |
| `0x78` | `PacketCharacter` | Mobile ilk spawn (ekipman listesi dahil) |
| `0x77` | `PacketCharacterMove` | Bilinen mobile pozisyon/yön güncellemesi |
| `0x95` | `PacketShowDyeWindow` | Boyama penceresi |

### 2.4 Hareket senkronizasyonu

| Opcode | Sınıf | Amaç | Öncelik |
|--------|-------|------|---------|
| `0x22` | `PacketMovementAck` | Yürüme kabul + notoriety | HIGHEST |
| `0x21` | `PacketMovementRej` | Yürüme red + eski pozisyon | HIGHEST |
| `0x77` | `PacketCharacterMove` | Diğer oyuncuların hareketi | NORMAL |
| `0xBF.0x26` | `PacketSpeedMode` | Mount/run hız modu | HIGH |
| `0xF6` | `PacketMoveShip` | Gemi + bileşen listesi kaydırma | NORMAL |

**Hareket broadcast akışı (`CChar::UpdateMove`, `CCharAct.cpp:1117`):**
```
Kendi client (m_pClient):
  → addPlayerView(ptOld)   // kendi ekranını güncelle

Diğer clientlar:
  göremiyorsa + önceden görüyordu → addObjectRemove (0x1D)
  görüyorsa + önceden görüyordu   → addCharMove (0x77)
  görüyorsa + ilk kez             → addChar (0x78)

Statü değişimi (hide, polymorph, warmode):
  CChar::Update(fFull) → fFull ? addChar : addCharMove + health bar
```

**Yön değişimi (`CChar::UpdateDir`):** sadece `m_dirFace` güncellenir → `UpdateMove(GetTopPoint())` → `0x77`.

**Görünüm zorla yenileme:** `addPlayerUpdate()` → `0x20` + server walk sequence = 0.

---

## 3. SphereNet — Karşılık tablosu

| Source-X | SphereNet sınıfı | Opcode | Durum |
|----------|------------------|--------|-------|
| `PacketPlaySound` | `PacketSound` | `0x54` | ✅ Var |
| `PacketPlayMusic` | `PacketPlayMusic` | `0x6D` | ✅ Var (sınırlı kullanım) |
| `PacketWeather` | `PacketWeather` | `0x65` | ✅ Login/global + region geçiş resync |
| `PacketSeason` | `PacketSeason` | `0xBC` | ✅ Login + admin broadcast + region geçiş resync |
| `PacketGlobalLight` | `PacketGlobalLight` | `0x4F` | ✅ Login + world tick + region geçiş resync |
| Personal light | `PacketPersonalLight` | `0x4E` | ✅ Login/spell |
| `PacketAction` | `PacketAnimation` | `0x6E` | ✅ Var |
| `PacketActionNew` | `PacketNewAnimation` | `0xE2` | ✅ Var + client tipine göre seçim |
| `PacketEffect` basic | `PacketEffect` | `0x70` | ✅ Var |
| `PacketEffect` hued | `PacketEffectHued` | `0xC0` | ✅ Var |
| `PacketEffect` particle | `PacketEffectParticle` | `0xC7` | ✅ Var |
| `PacketDragAnimation` | `PacketDragAnimation` | `0x23` | ✅ Pickup + ground-drop path |
| `PacketSwing` | `PacketSwing` | `0x2F` | ✅ Saldırgan client’a |
| `PacketDeath` / menu | `PacketDeathAnimation` / `PacketDeathStatus` | `0xAF` / `0x2C` | ✅ Var |
| `PacketWorldItem` | `PacketWorldItem` / `PacketWorldItemSA` | `0x1A` / `0xF3` | ✅ Var |
| `PacketRemoveObject` | `PacketDeleteObject` | `0x1D` | ✅ Var |
| `PacketPlayerUpdate` | `PacketDrawPlayer` | `0x20` | ✅ Var |
| `PacketCharacter` | `PacketMobileIncoming` (ExtendedPackets) | `0x78` | ✅ Var |
| `PacketCharacterMove` | `PacketMobileMoving` | `0x77` | ✅ Var |
| `PacketMovementAck` | `PacketMoveAck` | `0x22` | ✅ Var |
| `PacketMovementRej` | `PacketMoveReject` | `0x21` | ✅ Var |
| `PacketSpeedMode` | `PacketSpeedMode` | `0xBF` | ✅ Var |
| `PacketMoveShip` | `PacketBoatSmoothMove` | `0xF6` | ⚠️ Basitleştirilmiş (bileşen listesi yok) |
| Force walk | `PacketWalkForce` | `0x97` | ✅ Var (kayıt/diagnostic) |

---

## 4. SphereNet — Ses/görüntü kullanım haritası

| Özellik | Dosya | Paket |
|---------|-------|-------|
| Kapı sesi | `GameClient.WorldFeatures.cs` | `PacketSound` |
| Savaş swing/hit/miss | `GameClient.Combat.cs`, `Program.EngineWiring.cs` | `PacketSound`, `PacketAnimation`, `PacketEffect` |
| Item drop | `GameClient.Inventory.cs` | `PacketSound(0x0042)` + `PacketWorldItem` |
| Item use (yemek, potion, vb.) | `GameClient.ItemUse.cs` | `PacketSound`, `PacketAnimation`, `PacketEffect` |
| Skill feedback | `GameClient.Skills.cs`, `SkillHandlers.cs`, `ActiveSkillEngine.cs` | `PacketSound`, `PacketAnimation` |
| Script `SOUND`/`EFFECT`/`ANIM` | `Character.cs` | `PacketSound`, `PacketEffect`, `PacketAnimation` |
| NPC script `SOUND`/`EFFECT` (ObjBase) | `ObjBase.cs` | ❌ Stub — paket göndermiyor |
| Ölüm / resurrect | `GameClient.Combat.cs`, `DeathEngine.cs` | `PacketEffect`, `PacketSound`, `PacketDrawPlayer` |
| Teleport | `Program.EngineWiring.cs` | `PacketSound(0x01FE)` |
| Işık/mevsim (login) | `GameClient.Login.cs` | `PacketGlobalLight`, `PacketPersonalLight`, `PacketSeason` |
| Global ışık tick | `Program.Tick.cs` | `PacketGlobalLight` broadcast |
| Hava (admin) | `Program.NetworkHandlers.cs`, `Program.EngineWiring.cs` | `PacketSeason`, `PacketWeather` |
| Gemi | `Program.EngineWiring.cs` | `PacketBoatSmoothMove` |
| NPC facing | `Program.EngineWiring.cs` → `BroadcastFacingUpdate` | `PacketMobileMoving` |

---

## 5. Eksikler ve farklar (öncelik sırasıyla)

### 🔴 Yüksek — oynanışta hissedilir

| # | Konu | Source-X | SphereNet | Etki |
|---|------|----------|-----------|------|
| 1 | **Gemi hareketi `0xF6`** | Tam bileşen listesi | `PacketBoatSmoothMove` sadeleştirilmiş | Multi component pozisyonları Source-X kadar ayrıntılı değil |
| 2 | **Container-drop drag animasyonu** | Drag animation farklı drop hedeflerinde zengin | `0x23` pickup + ground-drop path'te bağlı | Container içi animasyon paritesi daha sınırlı |

### 🟡 Orta

| # | Konu | Source-X | SphereNet |
|---|------|----------|-----------|
| 3 | Drop sesi item/tiledata bazlı | `GetDropSound(pObjOn)` | Altın miktarı ayrışıyor; diğerleri hâlâ genel 0x42 |
| 4 | `GenericSounds` config | `sphere.ini` → `addSound` gate | Config anahtarı + `SERV.GENERICSOUNDS` yüzeyi var (W-I1); ses gönderimini kapatan gate henüz bağlı değil — kapalıyken sesler yine çalıyor |
| 5 | Sector ambient ses (rüzgar vb.) | `CSector` tick → periyodik `addSound` | Yok |
| 6 | Kendi hareketinde `addPlayerView` | Self client view refresh | `BroadcastMoveNearby` ile kısmen; tam `addPlayerView` eşdeğeri belirsiz |

### 🟢 İyi / parite sağlanmış

- Player walk: `0x22` ack, `0x21` reject, sequence yönetimi, turn-in-place
- Diğer mobile: `0x77` broadcast, view delta ile `0x78`/`0x1D`
- `0x20` + sequence reset: death, polymorph, teleport, login
- Animasyon: `0x6E` + `0xE2` client tipi seçimi (`BroadcastAnimation`)
- Savaş sesleri ve çoğu skill/item feedback
- Item drop artık `PacketSound` içeriyor (`ClientInventoryHandler.cs`)
- NPC facing: `OnNpcFacingChanged` → `BroadcastFacingUpdate`
- Ölüm paket dizisi: `0xAF`, `0x2C`, ghost `0x77`/`0x20`

---

## 6. Önerilen parity kontrol listesi

Yeni özellik veya bug fix öncesi:

```
[ ] Ses gerekiyor mu?     → PacketSound (0x54) + BroadcastNearby
[ ] Animasyon?            → BroadcastAnimation (0x6E/0xE2 seçimi)
[ ] Büyü/efekt?           → 0x70 yeterli mi? hue/particle gerekiyorsa 0xC0/0xC7 ekle
[ ] Görünüm değişimi?     → 0x20 (self) veya 0x78 (spawn) veya 0x77 (move)
[ ] Hareket?              → 0x22 ack + 0x77 broadcast (exclude self)
[ ] Işık değişimi?        → 0x4F (+ gerekirse 0x4E personal)
[ ] Item mi char mi?      → ObjBase stub mu Character tam mı?
```

---

## 7. Hızlı opcode referansı (ses/görüntü/hareket)

```
SES / ORTAM
  0x54  Sound         Konumlu efekt
  0x6D  Music         Arka plan müziği
  0x65  Weather       Hava partikülleri
  0xBC  Season        Mevsim
  0x4F  GlobalLight   Ortam ışığı
  0x4E  PersonalLight Kişisel ışık (SphereNet)

GÖRÜNTÜ / ANİMASYON
  0x6E  Animation     Klasik
  0xE2  NewAnimation  SA+/KR
  0x70  Effect        Temel
  0xC0  EffectEx      Renkli (Source-X only)
  0xC7  EffectParticle Parçacık (Source-X only)
  0x23  DragAnim      Sürükleme (Source-X only)
  0x2F  Swing         Saldırı bildirimi
  0xAF  Death         Ölüm
  0x2C  DeathMenu     Ölüm menüsü

HAREKET / GÖRÜNÜM
  0x22  MoveAck
  0x21  MoveReject
  0x77  MobileMoving
  0x78  MobileIncoming (spawn)
  0x20  DrawPlayer (self update)
  0x1D  DeleteObject
  0x1A  WorldItem
  0xBF  SpeedMode (sub 0x26)
  0xF6  BoatMove
  0x97  WalkForce
```

---

## 8. Sonraki adımlar (önerilen sıra)

1. `PacketBoatSmoothMove` için Source-X `PacketMoveShip` bileşen listesi paritesi
2. `0x23` drag animation container-drop path'i
3. `GetDropSound` item def / tile bazlı ses tablosu
4. `GenericSounds` config gate
5. Sector ambient sesleri

---

*Oluşturulma: 2026-06-07 — Source-X referans: `tools/source-reference`, SphereNet: mevcut `src/` ağacı.*

---

## 9. Uygulama güncellemesi — 2026-06-07

Bu pass'te aşağıdaki parite açıkları kapatıldı:

- `ObjBase.SOUND` ve `ObjBase.EFFECT` artık stub değil; item/char tabanlı script komutları `BroadcastNearby` ile paket yayıyor.
- `Character.SOUND` / `Character.EFFECT` aynı ortak ObjBase yolunu kullanıyor.
- `EFFECT` komutu Source-X seçim mantığına yaklaştırıldı:
  - particle argümanı varsa `0xC7 PacketEffectParticle`
  - hue/render varsa `0xC0 PacketEffectHued`
  - aksi halde mevcut `0x70 PacketEffect`
- `PacketEffectHued` (`0xC0`) ve `PacketEffectParticle` (`0xC7`) outgoing writer sınıfları eklendi.
- Script `FACE` artık direction set etmekle kalmıyor; server wiring üzerinden yakına `PacketMobileMoving` (`0x77`) broadcast ediyor.
- Region değişiminde client'a environment resync gönderiliyor:
  - `PacketGlobalLight` (`0x4F`)
  - sessiz `PacketSeason` (`0xBC`)
  - region weather için `PacketWeather` (`0x65`)
- `PacketDragAnimation` (`0x23`) writer eklendi ve pickup / ground-drop başarı yollarına bağlandı. Source-X `canSendTo` kuralına yakın olacak şekilde KR, Enhanced ve Stygian Abyss destekli clientlar filtreleniyor.
- Yeni regresyon testleri eklendi: `SoundVisualParityTests`.

Kalan işler:

- Drop sesi hâlâ item/tile bazlı değil; `GameClient.Inventory` sabit `0x0042` kullanıyor.
- `GenericSounds` config anahtarı yüklendi ve `SERV.GENERICSOUNDS` ile okunuyor (W-I1),
  ama ses gönderim gate'i henüz bağlı değil; paketler global ayarla kapatılamıyor.
- Sector ambient sesleri (rüzgar vb.) henüz yok.
- `0x23` container-drop path'i tam Source-X seviyesinde modellenmedi; bu pass pickup ve ground-drop akışını kapsıyor.
- `PacketBoatSmoothMove` hâlâ Source-X `PacketMoveShip` bileşen listesi kadar kapsamlı değil.
