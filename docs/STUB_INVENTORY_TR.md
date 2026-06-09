# SphereNet Stub ve Eksik İşlev Envanteri

Bu doküman, kodda **tanımlı görünüp fiilen çalışmayan** veya **yalnızca uyumluluk için sessiz kabul eden** yolların listesidir. Sonraki parity çalışmalarında referans olarak kullanılmalıdır.

**Not — terminoloji:** Metinde *oyun karakteri* için `Character` sınıfı (`Character.cs`) kullanılır. Türkçe *karakter* kelimesi yazı kodlaması (UTF-8) anlamında değildir; bu dosya UTF-8 ile kaydedilmiştir.

İlgili test guardrail: `src/SphereNet.Tests/TriggerCoverageGuardrailTests.cs`  
Ses/görüntü/hareket parity: `docs/SOUND_VISUAL_MOVEMENT_PARITY_TR.md`

Son güncelleme: 2026-06-09

---

## Özet — en kritik boşluklar

| Öncelik | Konu | Etki |
|---------|------|------|
| ~~P0~~ ✅ | Guild/ittifak kanal konuşması | **Çözüldü (2026-06-09):** `OnChannelMessage` artık alıcı taşıyor ve EngineWiring'de subscribe; üyelere 0xAE tip 0xD/0xE ile gidiyor |
| ~~P0~~ ✅ | Custom housing editörü (0xD7/0xD8) | **Çözüldü (2026-06-09):** `CustomHousingEngine` + `GameClient.HandleEncodedCommand`; 0xD8 design stream, 0xBF 0x1D/0x1E/0x20, DESIGN_n tag persistence |
| ~~P1~~ ✅ | Bilinmeyen `SERV.*` script fiilleri | **Çözüldü (2026-06-09):** fiil başına bir kez warning log + her kullanımda debug log; GM'lere SysMessage |
| ~~P1~~ ✅ | Item script `OPEN` / `DCLICK` / `USE` | **Çözüldü (2026-06-09):** `Item.OnScriptOpen` / `OnScriptDClick` hook'larıyla GameClient paket yoluna köprülendi |
| ~~P1~~ ✅ | `Character.DISMOUNT` script fiili | **Çözüldü (2026-06-09):** `Character.OnScriptDismount` → `MountEngine.Dismount` (client yolu + headless fallback) |
| ~~P1~~ ✅ | Help menüsü stuck/page | **Çözüldü (2026-06-09):** stuck → güvenli noktaya taşıma (jail/combat'ta reddedilir), page → `.PAGE` komut yolu + kayıt listesi, page list gump'ı |
| P2 | Trigger backlog | 11 char + 11 item trigger ateşlenmiyor (HouseDesignCommit/Exit, ExpChange/ExpLevelChange, UserVirtue, UserKRToolbar artık ateşleniyor) |
| ~~P2~~ ✅ | Region geçişinde client ışık paketi | **Zaten bağlıymış:** `Program.EngineWiring.cs` OnRegionChanged → 0x4F + sezon + bölgesel weather (envanter eskimişti) |

**Kalan açık alanlar (özet):** custom housing devamı (foundation deed + commit sonrası multi rebuild), global/legacy chat (0xB2/0xF9 — bilinçli erteleme), kalan P2 trigger'lar (paket handler'ı olmayan User* butonları), sector ambient ses (kozmetik), `HitIgnore`/`NPCSeeWantItem` (altyapı gerekçeleriyle ertelendi), script console `BUY`/`BYE`/`DIALOGCLOSE`/`ISDIALOGOPEN` compat stub'ları, `Character.BOUNCE`/`DROP` (drag-cursor altyapısı yok), item `FIXWEIGHT`.

---

## 1. Script komutları (verb) — `return true`, iş yok veya kısmi

### 1.1 `ObjBase` (`ObjBase.cs`)

| Komut | Durum |
|-------|--------|
| `TRIGGER` | ✅ `ObjBase.OnScriptTrigger` → `FireCharTriggerByName` / `FireItemTriggerByName`; özel (custom) trigger adları dahil |
| `FIX` | ✅ Terrain Z'sine oturtur (`GetEffectiveZ`; karakterler `MoveCharacter`, yerdeki item'lar doğrudan pozisyon) |
| `SOUND` / `EFFECT` (switch içi) | Üst satırlardaki `EmitScriptSound` / `EmitScriptEffect` çalışır; switch dalı ölü kod |

**Çalışan:** `SOUND` / `EFFECT` üst handler üzerinden (`EmitScriptSound`, `EmitScriptEffect` — `0x70` / `0xC0` / `0xC7` seçimi dahil).

### 1.2 `Item` (`Item.cs`)

| Komut | Durum |
|-------|--------|
| `OPEN` | ✅ `Item.OnScriptOpen` → `GameClient.OpenContainerFromScript` (script otoritesi, snoop/trap atlanır) |
| `DCLICK` / `USE` | ✅ `Item.OnScriptDClick` → `GameClient.HandleDoubleClick` (tam client çift-tık yolu) |
| `FIXWEIGHT` | No-op |
| `COMMIT` (MultiCustom) | Tag yazar; client design sync editör Commit'inden (0xD7 0x04) geçiyor — script COMMIT hâlâ rebuild tetiklemez |

**Çalışan:** `DELETE`, `EMPTY`, `MOVE`, `FLIP` (flip çifti varsa), gemi komutları → `ShipEngine`, guild stone → `GuildManager`.

### 1.3 `Character` sınıfı (`Character.cs`)

| Komut | Durum |
|-------|--------|
| `BOUNCE` | Sunucu drag cursor yok; sessiz no-op |
| `DROP` | Aynı — paket yolu inline drop |
| `DISMOUNT` | ✅ `Character.OnScriptDismount` → client `UnmountSelf` / `MountEngine.Dismount`; headless'ta flag fallback |
| `TAGLIST` | ✅ TAG'leri çağıran konsola döker |
| `BARK` | Ses var; body-id türetilmiş (sound tablosu yok) |

**Çalışan:** `SOUND`, `EFFECT`, `ANIM`, `FACE` (+ `OnFacingChanged` → `0x77`), `GO`, `SAY`/`EMOTE`, party/vendor çoğu fiil, `CONSUME`, `PACK`/`BANK`.

### 1.4 `Region` / `Room` (`Region.cs`, `Room.cs`)

| Komut | Durum |
|-------|--------|
| `ALLCLIENTS` | ✅ `Region.OnAllClients` / `Room.OnAllClients` — SYSMESSAGE teslimi veya bölgedeki her client char için script fonksiyonu |
| `TAGLIST` | ✅ Tag dump çağıran konsola |

**Çalışan:** `@RegionEnter` / `@Leave` / `@Step` (oyuncu yürüyüşü); `@RoomEnter` / `@Leave` / `@Step`.

---

## 2. Script konsolu ve `SERV.*`

Dosya: `GameClient.ScriptConsole.cs`, `Program.Scripting.cs`

### Stub / kısmi

| Öğe | Durum |
|-----|--------|
| `BUY` | Placeholder — script vendor buy yok |
| `BYE` | NPC oturumu kapatılmaz |
| `DIALOGCLOSE` | Açık dialog kaydı yok |
| `ISDIALOGOPEN.*` | Hep `"0"` |
| Yakalanmayan `SERV.*` | ✅ Fiil başına bir kez warning log, sonrası debug; GM çağırana SysMessage |
| `SERV.CHATFLAGS` | Sabit `"0"` |
| `SERV.ACCOUNT.n` (sayısal) | `"0"` |

### Çalışan örnekler

`SERV.NEWITEM`, `SERV.ALLCLIENTS`, `SERV.LOG`, `DIALOG`/`SDIALOG`, `MENU`, `GO`, `GONAME`, `BANKSELF`, `FILE.*`, `DB.*`, `SERV.MAP*`, `SERV.GMPAGE` (script bağlı), `UID.*.DIALOG` (online oyuncu).

---

## 3. Gelen paketler — kabul, işlem yok

| Opcode | Açıklama |
|--------|----------|
| `0xB2` | Legacy chat — sessiz ignore |
| `0xD7` | ✅ Custom house design — `GameClient.HandleEncodedCommand` (Build/Delete/Stairs/Roof/Level/Commit/Revert/Backup/Restore/Sync/Close) |
| `0xD8` | (giden paket — bkz. bölüm 4) |
| `0xF9` | Global chat — handler yok |
| `0xA5` | Web link — handler yok |
| `0xB5` | Chat — handler yok |
| `0xFA` | Ultima Store — handler yok |
| `0xD9` / `0xBE` | Hardware/system — kabul only |
| `0xE3` | KR encryption seed — kabul only |
| `0xF4` | Crash report — log only |
| `0xBF.0x24` | Boş extended handler |
| `0xF0` (kısa) | Party/guild/razor extension — ignore |

**Çalışan:** Hareket, konuşma, savaş, trade (`0x6F`), vendor buy/sell paketleri, gump, hedefleme, party `0xBF.0x06`, context menu, kitap/pano vb.

---

## 4. Giden paket — tanımlı ama gameplay eksik

| Paket | Durum |
|-------|--------|
| `0xA5` Web | ✅ `PacketWebLink` + script `WEBLINK` verb + guild gump "Visit Web Page" butonu |
| `0xD8` House design | ✅ `PacketHouseDesignDetailed` (zlib mode-0 plane'ler) + `0xBF 0x1D` revision + `0xBF 0x20` mode switch |
| `0xF9` Global chat | Yok |
| `0xF6` Boat | `PacketBoatSmoothMove` basitleştirilmiş (bileşen listesi yok) |

**Güncel (artık var):** `0xC0` / `0xC7` efekt, `0x23` drag anim — `ObjBase.EmitScriptEffect`, `GameClient.Inventory` pickup.

**Kısmi:** Vendor buy gump — container serial uyuşmazsa client gump'ı sessizce düşer (`ExtendedPackets.cs` yorumu).

---

## 5. Sistem alanları

### Çalışan (özet)

Hareket ack/reject, savaş, çoğu skill handler, party, secure trade, gemi, standart housing, guild stone gump, spatial speech, büyü motorunun çoğu.

### Eksik / kısmi

| Alan | Eksik |
|------|--------|
| Custom housing UI | ✅ House sign gump "Customize House" → `CustomHousingEngine` oturumu; 0xD7 komutları + 0xD8 stream + DESIGN_n tag persistence. Eksik kalan: foundation yerleştirme aracı (custom foundation deed), commit sonrası fiziksel multi component rebuild |
| Guild/ittifak kanal | ✅ `OnChannelMessage(speaker, recipient, text, mode)` EngineWiring'de subscribe; karşılıklı ittifak (mutual ally) filtresiyle |
| Global/legacy chat | 0xB2, 0xF9, `CHATFLAGS` — **bilinçli erteleme:** UO chat-room alt sistemi; modern client kullanımı yok denecek kadar az |
| In-client web | ✅ 0xA5 `PacketWebLink` + `WEBLINK` verb |
| Help stuck/page | ✅ Stuck güvenli noktaya taşır (jail/combat reddi), page `.PAGE` yoluna bağlı, page list gump'ı |
| XP / level | ✅ `Character.ChangeExperience` pipeline: `@ExpChange` (ARGN1 ayarlanabilir/iptal), level eşiğinde `@ExpLevelChange`; NPC kill'de kurbanın EXP'i ödül; `LevelNextAt`/`LevelModeDouble` ayarları |
| Memory inspection | `MEMORYFINDTYPE` kısmi; guild stone pozisyonu master'a fallback |
| Region ışık | `@EnvironChange` var; client `PacketGlobalLight` yok |
| Sector ambient ses | Source-X `CSector` rüzgar sesi yok (bilinçli erteleme — kozmetik) |
| Item drop sesi | ✅ `GetDropSound`: altın için miktara göre 0x2E4-0x2E6, diğerleri 0x42 |
| `GenericSounds` | `sphere.ini` karşılığı yok |

---

## 6. Trigger backlog (CI guardrail)

Kaynak: `TriggerCoverageGuardrailTests.cs`

### Oyun karakteri trigger'ları — ateşlenmiyor

**P0 (bilinçli erteleme, guardrail yorumunda gerekçeli):** `HitIgnore` (AttackerRecord'da ignore bayrağı yok), `NPCSeeWantItem` (`@NPCLookAtItem` ile örtüşüyor)

**P2:** `UserBugReport`, `UserExWalkLimit`, `UserGlobalChatButton`, `UserMailBag`, `UserQuestArrowClick`, `UserSpecialMove`, `UserUltimaStoreButton`, `ToolTip`, `Targon_Cancel`, `NPCLostTeleport`

**Ateşleniyor (2026-06-09):** `HouseDesignCommit`, `HouseDesignExit` (0xD7 Commit/Close), `ExpChange`, `ExpLevelChange` (`ChangeExperience` pipeline), `UserVirtue` (0xBF 0x2C), `UserKRToolbar` (0xBF 0x24).

### Item trigger'ları — ateşlenmiyor (hepsi P2)

`RegionEnter`, `RegionLeave`, `Start`, `Stop`, `Level`, `Complete`, `AddRedCandle`, `AddWhiteCandle`, `DelRedCandle`, `DelWhiteCandle`, `Tooltip`

**Çalışan örnekler (char):** `@UserWarmode`, `@UserStats`, `@PersonalSpace`, `@EnvironChange`, `@Jail`, `@PartyDisband`, combat/skill/party/ship çoğu.

**Çalışan örnekler (item):** `ShipMove`/`Stop`/`Turn`, `Redeed`, `MemoryEquip`, `PickupSelf`/`PickupStack`.

---

## 7. Skill sistemi

- Kayıtlı handler yoksa → `SkillEngine.UseQuick` (genel zorluk kontrolü).
- `skill_noskill` mesajı tanımlı; skill kodunda kullanılmıyor.
- Aktif skill'lerin çoğu (`ActiveSkillEngine`) wired: hiding, stealing, taming, mining, healing, lockpick vb.
- Crafting skill handler'ları `SkillHandlers` içinde kayıtlı.

---

## 8. Bilinçli no-op (düşük öncelik)

| Öğe | Neden |
|-----|--------|
| `CTAG` item üzerinde | Client-session yalnızca online `Character` |
| `DIALOGCLOSE` compat | Dialog registry yok |
| Ephemeral engine tag'leri | Bilerek yutulur |
| `FINDLAYER` boş layer | Sphere uyumu — `true` |
| Stale walk seq drop | Client storm önleme |
| `InfoSkillEngine.CanTouch` | Basitleştirilmiş menzil kuralı |

---

## 9. Önerilen wiring sırası

1. ~~`SpeechEngine.OnChannelMessage` → guild/ittifak teslimatı~~ ✅ (2026-06-09)
2. ~~`0xD7` encoded handler + custom housing state machine~~ ✅ (2026-06-09)
3. ~~`Character.DISMOUNT` → `MountEngine.Dismount`~~ ✅ (2026-06-09)
4. ~~Item `OPEN` / `DCLICK` script → `GameClient` paket köprüsü~~ ✅ (2026-06-09)
5. ~~Region geçişi → `PacketGlobalLight` + bölgesel `PacketWeather`~~ ✅ (zaten bağlıydı; envanter düzeltildi)
6. ~~`SERV.*` bilinmeyen fiil → log/SysMessage~~ ✅ (2026-06-09)
7. ~~Trigger P1 (ExpChange/ExpLevelChange) + kolay P2'ler (UserVirtue, UserKRToolbar)~~ ✅ (2026-06-09)
8. Custom housing devamı: foundation yerleştirme deed'i, commit sonrası multi component rebuild
9. Kalan P2 trigger'lar (User* butonları için paket handler'ları gerekiyor: 0xFA, 0xF4 vb.)
10. Global/legacy chat (0xB2/0xF9) — bilinçli erteleme; talep olursa ayrı dalga

---

## 10. Parity dokümanı düzeltmesi

`SOUND_VISUAL_MOVEMENT_PARITY_TR.md` içindeki bazı maddeler güncellenmeli:

- `0xC0` / `0xC7` / `0x23` artık `ExtendedPackets.cs` + `ObjBase` / `Inventory` ile mevcut.
- `ObjBase.SOUND` üst handler ile çalışıyor; switch içi stub ölü kod.
- `Character.FACE` → `OnFacingChanged` wired (`Program.EngineWiring.cs`).

Bu envanter dosyası o parity belgesinin **tamamlayıcısıdır**; ses/görüntü detayı için oraya bakın.
