# SphereNet Stub ve Eksik İşlev Envanteri

Bu doküman, kodda **tanımlı görünüp fiilen çalışmayan** veya **yalnızca uyumluluk için sessiz kabul eden** yolların listesidir. Sonraki parity çalışmalarında referans olarak kullanılmalıdır.

**Not — terminoloji:** Metinde *oyun karakteri* için `Character` sınıfı (`Character.cs`) kullanılır. Türkçe *karakter* kelimesi yazı kodlaması (UTF-8) anlamında değildir; bu dosya UTF-8 ile kaydedilmiştir.

İlgili test guardrail: `src/SphereNet.Tests/TriggerCoverageGuardrailTests.cs`, `src/SphereNet.Tests/SourceXVerbInventoryGuardrailTests.cs`
Ses/görüntü/hareket parity: `docs/SOUND_VISUAL_MOVEMENT_PARITY_TR.md`

Son güncelleme: 2026-07-03

Güncel doğrulama: `dotnet test .\sphereNet.sln --nologo` -> 1122/1122 başarılı.

Güncel kısa durum: `SERV.*`, arbitrary verb fallback ve `RETURN/ARGS/ARGN/ARGO/LOCAL` altyapısı temel olarak çalışıyor; bunlar "yok" kabul edilmemelidir. Kalan açıklar daha çok Source-X server/admin verb uzun kuyruğu, bazı indeksli server property erişimleri ve az sayıda altyapı gerektiren trigger'dır.

> **Tarama yapmadan önce oku:** Yeni bir "eksik var mı" taraması yapılacaksa
> önce bu dosya + `docs/PARITY.md` + `src/SphereNet.Tests/TriggerCoverageGuardrailTests.cs`
> okunmalıdır. Trigger backlog'unun tek otoritesi guardrail testidir (enum'a
> ateşlenmeyen üye eklenirse test kırılır). 2026-06-10 taramasındaki ajan
> raporlarında çıkan şu iddialar **yanlış pozitifti** — hepsi mevcut:
> `SERV.NEWITEM` / `SERV.LOG` / `SERV.ALLCLIENTS` / `SERV.NEWNPC(LASTNEWCHAR)`,
> `@DropOn_*`, `@PickUp_*`, `@TargOn_*`, `@SkillSuccess/Fail/Abort`,
> `@SpellSuccess/Fail`, `@RegPeriodic` / `@CliPeriodic`.

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
| P2 | Trigger backlog | Guardrail'e göre kalan gerçek backlog: char `NPCSeeWantItem`, `UserMailBag`; item `Level`, `Complete`, `AddRedCandle`, `AddWhiteCandle`, `DelRedCandle`, `DelWhiteCandle`. |
| ~~P2~~ ✅ | Region geçişinde client ışık paketi | **Zaten bağlıymış:** `Program.EngineWiring.cs` OnRegionChanged → 0x4F + sezon + bölgesel weather (envanter eskimişti) |

**Kalan açık alanlar (özet, hepsi bilinçli erteleme):** 0xB2 legacy text-in + 0xF9 (çok eski/KR varyantları; conference chat 0xB3/0xB5 ile çalışıyor), `UserMailBag` (paket yok), `NPCSeeWantItem` (`@NPCLookAtItem` ile örtüşük), item `Level`/`Complete` + candle trigger'ları (item-level / champion altar altyapısı yok), sector ambient ses (Source-X referansında yok — spekülatif maddeydi), `SERV.*` admin/maintenance uzun kuyruğu.

**2026-06-10 dalgası — kapatılanlar:**
- **Dialog sistemi:** `LOCAL.` atamaları artık string değerleri koruyor (virgüllü
  listeler, `.=` birleştirme); dialog expression parser'ına script `[FUNCTION]`
  çözücüsü bağlandı (`ARRAYCOUNT`/`ARRAY`/`FormatMinutes`… layout içinde çalışır);
  `FORINSTANCES` expansion desteği + FOR ailesi blok eşleştirmesi; `GUMPPIC`
  (çift P) alias'ı ve `TOOLTIP` render verb'i eklendi. Dialog layout verb
  kapsaması script setine göre artık tam.
- **Ölüm görselleri:** GM kill yolları (`.kill` UID/imleç, guard instant-kill)
  `ProcessDeathWithEffects` ortak yoluna bağlandı — 0xAF, hayalet dönüşümü,
  @Kill/@Death trigger'ları artık her ölüm yolunda tutarlı.
- **Yeni trigger'lar:** `@DeathCorpse` (ProcessDeath, argo=ceset), `@Reveal`
  (Character.ClearHiddenState merkez yolu, RETURN 1 gizliliği korur),
  `@SpellEffectAdd` / `@SpellEffectRemove` (SpellEngine zamanlı efekt yaşam
  döngüsü; save-anı revert/reapply bilinçli olarak sessiz). `@SpellEffectTick`
  artık poison tick köprüsü üzerinden ateşlenir; `LOCAL.EFFECT/DELAY/CHARGES`
  seed edilir ve script değişiklikleri geri okunur.
- **Script tarafı:** `FormatDays` (sphere_functions_datetime.scp) ve
  `FamilyCount` (sphere_functions.scp; aile sistemi bu sete taşınmadığı için
  şimdilik 0 döner) fonksiyonları eklendi — helppage sayfa 4 artık çözülür.

**Custom housing tamamlandı (2026-06-09, dalga 3):** foundation yerleştirme — deed üzerinde `TAG.CUSTOMHOUSE` ile `PlaceHouse(customFoundation: true)` → `MultiCustom` item + boş tasarım (revision 1), gerçek bileşen item'ı üretilmez; commit edilen tasarım sunucu tarafında `WalkCheck.ResolveCustomDesign` → `CustomHousingEngine.GetCommittedTiles` (revision-anahtarlı concurrent önbellek) üzerinden sanal yürüme geometrisi olur (client karoları 0xD8'den çizdiği için çift render yok).

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
| `FIXWEIGHT` | ✅ View refresh (ağırlık zaten talep anında hesaplanıyor) |
| `COMMIT` (MultiCustom) | Tag yazar; client design sync editör Commit'inden (0xD7 0x04) geçiyor — script COMMIT hâlâ rebuild tetiklemez |

**Çalışan:** `DELETE`, `EMPTY`, `MOVE`, `FLIP` (flip çifti varsa), gemi komutları → `ShipEngine`, guild stone → `GuildManager`.

### 1.3 `Character` sınıfı (`Character.cs`)

| Komut | Durum |
|-------|--------|
| `BOUNCE` | ✅ DRAGGING tag'indeki item çantaya döner, 0x27 ile drag cursor iptal edilir |
| `DROP` | ✅ DRAGGING tag'indeki item ayak ucuna bırakılır |
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
| `BUY` | ✅ Dialog subject'i vendor ise onun, değilse 8 tile içindeki en yakın vendor'un buy listesi |
| `BYE` | ✅ Açık script dialog'larını kapatır + dialog subject'i temizler |
| `DIALOGCLOSE` | ✅ Açık dialog kaydı (`_openScriptDialogs`) + `0xBF 0x04` close gump |
| `ISDIALOGOPEN.*` | ✅ Gerçek değer (kayıttan) |
| Yakalanmayan `SERV.*` | Compat-safe: fiil başına bir kez warning log, sonrası debug; GM çağırana SysMessage. Bu crash'i önler ama Source-X fiilinin davranışını uygulamış sayılmaz. |
| `SERV.CHATFLAGS` | W-I1: `sphere.ini` -> `SphereConfig.ChatFlags` -> `SERV.CHATFLAGS`; artık sabit değil |
| `SERV.ACCOUNT.n` (sayısal) | `"0"` |
| `DEFMSG name=value` | W-I1: runtime override artık `ServerMessages` katmanına yazar ve `DEFMSG.*` lookup tarafından okunur; persist semantiği açık |
| `SERV.*` admin uzun kuyruğu | Kısmi: `SAVE`/`RESYNC`/`SHUTDOWN` ve yaygın bridge'ler var; `EXPORT`/`IMPORT`/`RESTORE`/`VARLIST` vb. Source-X maintenance fiilleri takip işi |

### Çalışan örnekler

`SERV.NEWITEM`, `SERV.ALLCLIENTS`, `SERV.LOG`, `DIALOG`/`SDIALOG`, `MENU`, `GO`, `GONAME`, `BANKSELF`, `FILE.*`, `DB.*`, `SERV.MAP*`, `SERV.GMPAGE` (script bağlı), `UID.*.DIALOG` (online oyuncu).

---

## 3. Gelen paketler — kabul, işlem yok

| Opcode | Açıklama |
|--------|----------|
| `0xB2` | Legacy chat text-in — sessiz ignore (modern client 0xB3 gönderir; ✅ conference chat 0xB3/0xB5 üzerinden çalışıyor) |
| `0xD7` | ✅ Custom house design — `GameClient.HandleEncodedCommand` (Build/Delete/Stairs/Roof/Level/Commit/Revert/Backup/Restore/Sync/Close) |
| `0xD8` | (giden paket — bkz. bölüm 4) |
| `0xF9` | Global chat — handler yok (bilinçli erteleme, chat-room sistemi) |
| `0xA5` | (giden paket — ✅ `PacketWebLink`) |
| `0xB5` | ✅ Kayıtlı — `@UserGlobalChatButton` ateşler |
| `0xFA` | ✅ Kayıtlı — `@UserUltimaStoreButton` ateşler |
| `0xD9` / `0xBE` | Hardware/system — kabul only |
| `0xE3` | KR encryption seed — kabul only |
| `0xF4` | ✅ Log + `@UserBugReport` ateşler |
| `0xBF.0x24` | ✅ `@UserKRToolbar` ateşler |
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
| Custom housing UI | ✅ House sign gump "Customize House" → `CustomHousingEngine` oturumu; 0xD7 komutları + 0xD8 stream + DESIGN_n tag persistence. Custom foundation deed ve commit sonrası sanal yürüme geometrisi bağlı; fiziksel component rebuild bilinçli olarak kullanılmıyor. |
| Guild/ittifak kanal | ✅ `OnChannelMessage(speaker, recipient, text, mode)` EngineWiring'de subscribe; karşılıklı ittifak (mutual ally) filtresiyle |
| Global/legacy chat | ✅ **Conference chat uygulandı (2026-06-10):** `ChatEngine` (kanallar/üyelik), 0xB3 talk/join/create/leave, 0xB2 giden mesajlar (ClassicUO parser'ına karşı doğrulandı), 0xB5 açılışta kanal listesi + otomatik isim kabulü. W-I1 ile `SERV.CHATFLAGS` config değeri görünür oldu. Kalan: 0xB2 legacy text-in (çok eski clientlar) ve flag'lerin tüm davranış etkileri — bilinçli erteleme |
| In-client web | ✅ 0xA5 `PacketWebLink` + `WEBLINK` verb |
| Help stuck/page | ✅ Stuck güvenli noktaya taşır (jail/combat reddi), page `.PAGE` yoluna bağlı, page list gump'ı |
| XP / level | ✅ `Character.ChangeExperience` pipeline: `@ExpChange` (ARGN1 ayarlanabilir/iptal), level eşiğinde `@ExpLevelChange`; NPC kill'de kurbanın EXP'i ödül; `LevelNextAt`/`LevelModeDouble` ayarları |
| Memory inspection | `MEMORYFINDTYPE` kısmi; guild stone pozisyonu master'a fallback |
| Region ışık | ✅ Region geçişinde `PacketGlobalLight` + sessiz `PacketSeason` + bölgesel `PacketWeather`; `@EnvironChange` de yeni ışık seviyesini alır |
| Sector ambient ses | Source-X `CSector` rüzgar sesi yok (bilinçli erteleme — kozmetik) |
| Item drop sesi | ✅ `GetDropSound`: altın için miktara göre 0x2E4-0x2E6, diğerleri 0x42 |
| `GenericSounds` | W-I1: `sphere.ini` karşılığı var ve `SERV.GENERICSOUNDS` olarak görünür; body/action bazlı generic sound table paritesi ayrı ses işi |

---

## 6. Trigger backlog (CI guardrail)

Kaynak: `TriggerCoverageGuardrailTests.cs`

### Oyun karakteri trigger'ları — ateşlenmiyor

**Bilinçli erteleme (guardrail yorumunda gerekçeli):** `NPCSeeWantItem` (`@NPCLookAtItem` ile örtüşüyor), `UserMailBag` (taşıyıcı client paketi yok)

**Ateşleniyor (2026-06-10, dalga 5):** `UserSpecialMove` (0xD7 sub 0x19 combat ability, N1 = ability index — ClassicUO `Send_UseCombatAbility` doğrulandı), `NPCLostTeleport` (leash'in 3 katından uzağa düşen NPC eve ışınlanır — seri ApplyDecision fazından ateşlenir, RETURN 1 ışınlanmayı iptal eder)

**Ateşleniyor (2026-06-09):** `HouseDesignCommit`, `HouseDesignExit` (0xD7 Commit/Close), `ExpChange`, `ExpLevelChange` (`ChangeExperience` pipeline), `UserVirtue` (0xBF 0x2C), `UserKRToolbar` (0xBF 0x24), `UserQuestArrowClick` (0xBF 0x07), `UserBugReport` (0xF4), `UserUltimaStoreButton` (0xFA), `UserGlobalChatButton` (0xB5), `UserExWalkLimit` (walk token tükenmesi, gated), `ToolTip` (single click, gated), `Targon_Cancel` (hedef iptali), `HitIgnore` (ATTACKER.n.IGNORE bayraklı saldırgan vuruşu).

### Item trigger'ları — ateşlenmiyor (hepsi P2)

`Level`, `Complete`, candle trigger'ları (`AddRedCandle`, `AddWhiteCandle`, `DelRedCandle`, `DelWhiteCandle`) — item-level / champion altar altyapısı yok.

**Ateşleniyor (2026-06-09/10+):** `Tooltip` (single click, IsTrigUsed gate ile, `@Click`'ten önce), `Start`/`Stop` (spawner START/STOP verb'leri), `Smelt` (ore -> ingot path), `SpellEffect` (item-targeted spell path), `RegionEnter`/`RegionLeave` (movable multi/ship region boundary; RETURN 1 hareketi bloklar).

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
8. ~~Custom housing devamı: foundation yerleştirme deed'i, commit edilen tasarımın sunucu yürüme geometrisi~~ ✅ (2026-06-09)
9. Kalan P2 trigger'lar: `UserMailBag`, `NPCSeeWantItem`, item `Level`/`Complete` ve candle trigger'ları.
10. Global/legacy chat (0xB2 text-in / 0xF9) — bilinçli erteleme; talep olursa ayrı dalga
11. `SERV.*` admin/maintenance uzun kuyruğu: native `SAVESTATICS` map çıktısı, script-safe `BLOCKIP`/`UNBLOCKIP`, `CALCCRYPT`, `CONSOLE` ve güvenlik yüzeyi yüksek admin komutları.

---

## 10. Parity dokümanı ilişkisi

`SOUND_VISUAL_MOVEMENT_PARITY_TR.md` güncel ses/görüntü/hareket durumunu tutar:

- `0xC0` / `0xC7` / `0x23` artık `ExtendedPackets.cs` + `ObjBase` / `Inventory` ile mevcut.
- `ObjBase.SOUND` üst handler ile çalışıyor; switch içi stub ölü kod.
- `Character.FACE` → `OnFacingChanged` wired (`Program.EngineWiring.cs`).
- Region geçişinde `PacketGlobalLight`, sessiz `PacketSeason` ve bölgesel `PacketWeather` gönderiliyor.

Bu envanter dosyası o parity belgesinin **tamamlayıcısıdır**; ses/görüntü detayı için oraya bakın.
