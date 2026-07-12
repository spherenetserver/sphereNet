# Sub-90 Parity Backlog (Source-X = 100)

Kod-tabanlı denetimden (2026-07) çıkan, 90'ın **altında** puan alan her kategori ve
somut eksik listesi. Kaynak: 5 paralel salt-kod karşılaştırma ajanı. Her madde
implement edilmeden önce gerçek kod karşılaştırmasıyla DOĞRULANIR (ajan iddiaları
yanlış olabilir). Tamamlanınca `[x]` işaretle + Wave numarası + kanıt satırı ekle.

Puan referansı: kategori adının yanındaki sayı = mevcut kod-fidelity tahmini.

---

## TIER 0 — Onaylı Buglar (önce bunlar)

- [x] **T0.1 Ship anchor-drop kontrol akışı** — YANLIŞ ALARM. `ShipEngine.cs:603-621`
  zaten düzgün brace'li; ANCHORDROP `ship.Anchored=true; Stop; TillerSpeak` doğru
  çalışıyor. Ajan yanlış okumuş (satır 593 sadece yorum).
- [ ] **T0.2 Verb guardrail testi kanıt sağlamıyor** —
  `SourceXVerbInventoryGuardrailTests.cs:83-113` sadece referans `.tbl` envanterini
  pinliyor, C# implementasyonuna assert etmiyor. Testi implementasyona karşı assert
  edecek şekilde güçlendir (aşağıdaki eksik verb'leri yakalasın).

---

## TIER 1 — Scripting çekirdeği (en yüksek kaldıraç, script-güdümlü uzun kuyruk)

### 1.1 VAR/LIST/DEFNAME — 65 (en zayıf)
- [x] **LIST runtime mutasyon verb'leri** — YAPILDI (Wave 215). Source-X
  CListDefMap::r_LoadVal grammar'ı `GameWorld.MutateGlobalList` (clear/add/set/
  append/sort asc|i|desc|idesc/index-set/remove/insert) + read
  `GameWorld.ResolveGlobalListRead` (count/index/findelement). Interpreter
  `LIST.<name>[.op]=v` → `_SET_LIST.` bridge; ResolveServList delege.
  Test: ParityMatrixServTests (+4). NOT: Source-X'te "pop" YOK (ajan yanılmış).
- [ ] **VarMap.cs** (84 satır) — typed num/str storage + sorted keys + r_WriteSave
  serialization eksik (şu an düz string dict). `CVarDefMap` paritesi. (düşük öncelik —
  fonksiyonel, sadece tipli-depolama eksik)

### 1.2 Obje verb & property — 78
- [x] Eksik `CChar_functions` verb'leri — YAPILDI (Wave 215): **UNEQUIP** (ItemBounce
  to pack), **WHERE** (konum mesajı), **SUMMONTO** (uid'e/SRC'ye ışınla), **CONTROL**
  (IClientContext.Character → TryAssignOwnership). Character.TryExecuteCommand.
  Test: ScriptCharVerbParityTests (+4).
- [x] Eksik prop'lar — YAPILDI/DOĞRULANDI (Wave 215): **CANMOVE <dir>** (dest tile
  IsPassable) + **NOTOGETFLAG <uid>** (ResolveNotoFlag hook → ComputeNotoriety) EKLENDİ.
  **STEPSTEALTH** zaten vardı; **RACE** gerçek CChar_props değil (ajan yanılmış).

### 1.3 Resource section tipleri — 78
- [ ] **SPHERECRYPT** ve **KRDIALOGLIST** section'ları eşlenmemiş (`_ => Unknown`).
  Anlamlı loader veya bilinçli-skip + log ekle.
- [ ] STAT / TIMERF / SERVERS / BLOCKIP section'ları `ResType.Sphere`/`ServerConfig`
  no-op'a katlanıyor — hangileri gerçek davranış gerektiriyor doğrula.

### 1.4 FILE/DB objeleri — 80
- [ ] DB async formları: **AQUERY / AEXECUTE** (`DBO_TYPE`) — write verb switch'te yok
  (`Program.Scripting.cs:444-465`).
- [ ] DB **NUMCOLS** property doğrulanmadı — ekle/doğrula.

### 1.5 Trigger — 86
- [ ] Kalan char trigger backlog: **@UserMailBag** fire-site (doğrula, gerekiyorsa kur).

### 1.6 Script okuma/lexing — 86
- [ ] Write-side `WriteSection`/`WriteKeyStr` (`CScript.cpp:856-976`) ayrı katmanda —
  script-objesi yazma yolunun tam olduğunu doğrula.

---

## TIER 2 — Combat / Skills / Magic

### 2.1 Poison — 72
- [x] OSI seviye modeli — YAPILDI (Wave 215): `CombatEngine.CalcOsiPoisonLevel(skill,
  dist, evilOmen)` skill-band (>650/850/1000 → std/greater/lethal, 1000'de 1/10 deadly
  bump) + mesafe-falloff (`-dist/2` @dist>=4) + Evil-Omen +1, SphereNet 1-5 ölçeği.
  Wire: 2 combat sitesi (weapon envenom + creature bite, `/200` yerine), Poison spell'e
  distance-falloff. Test: CombatWaveC5AosOnHitTests (+2). NOT: charge sayıları (8/6/6/3/3)
  DEĞİŞTİRİLMEDİ — SphereNet'in tick modeli (5/8/12/16/20) korundu (duration/save
  round-trip kırılmasın); Evil-Omen parametresi hazır ama necro spell'i yok → şimdilik
  false.

### 2.2 Magic / spells — 82
- [ ] Necromancy (101+) / Chivalry (201+) / Bushido / Ninjitsu / Spellweaving /
  Mysticism spell **efektleri** (şu an enum-only, `SpellTypes.cs:32-40`).
- [ ] MAGICF_IGNOREAR + spell reflection amplifier zinciri.
- [ ] Cast-recovery **FCR/FC** property modeli (şu an düz `1500-skill`).

### 2.3 Skills — 84
- [ ] **Cartography** ve **Camping** handler'ları yok (`Skill_Cartography` cpp:1756).
- [ ] Bushido/Ninjitsu/Chivalry/Necromancy/Focus/Imbuing/Mysticism/Spellweaving —
  enum var, native handler yok.

### 2.4 Combat flag & AOS on-hit — 85
- [x] **REFLECTPHYSICALDAM** — YAPILDI (Wave 215): defender'ın suit'i hasarın %'sini
  (cap 250) attacker'a geri yansıtır, Reactive spell'den ayrı, fixed damage (recursion
  yok). CombatEngine.ResolveAttack. Test: CombatWaveC5AosOnHitTests.
- [x] Era-2 def-side **INCREASEDEFCHANCE / INCREASEHITCHANCE** — YAPILDI (Wave 215):
  `(100 + min(HCI,45))` / `(100 + min(DCI,45))` çarpanları (eskiden düz 100).
  CombatEngine.CalcHitChanceCore era 2. Test: CombatWaveC5AosOnHitTests.
- [ ] Curse-Weapon leech add (cpp:2273-2275), Wraith-Form mana drain (cpp:2302-2305).
  ERTELENDİ → necro spell'lerine bağlı (2.2). SphereNet'te LAYER_SPELL_Curse_Weapon/
  Polymorph-Wraith buff-item modeli yok; önce necro efektleri gerekir.

### 2.5 Melee combat — 88
- [ ] SE (era3) / ML (era4) hız formülleri (`CResourceCalc.cpp:110-131`).
- [ ] Samurai-Empire / Bushido parry branch (cpp:250-296).
- [ ] PARRYERA_ARSCALING shield-AR + LAYER_SPELL_Protection AR (cpp:529-555).
- [ ] Horrific-Beast / gargoyle-berserk hasar amplifier (cpp:1223-1252).

### 2.6 Crafting & gathering — 86
- [ ] Material/renk seçim menüsü (OSI craft gump).
- [ ] Zorluk eğrisi resource-özel (şu an düz `(min+max)/2`).
- [ ] Çok-katmanlı BONUS resource verimi.
- [ ] Cartography harita-yapımı craft'a bağlı değil.

---

## TIER 3 — NPC / Dünya / Hareket

### 3.1 Weather / light / season — 68
- [ ] **Per-sector moon-phase ışık modeli** — `CSector::GetLightCalc`
  (`CSector.cpp:679-765`) Trammel+Felucca moon brightness tablosu; port düz
  time-of-day ramp (`GameWorld.cs:178`).
- [ ] **LightFlash** + IsMoonVisible.
- [ ] **@EnvironChange** (`CTRIG_EnvironChange`) weather/light/season değişiminde
  ateşlenmesi wired değil.

### 3.2 Sector — 75
- [ ] Sleep modeli: `SECF_NoSleep`/`SECF_InstaSleep` flag'leri, 8-komşu adjacency
  sweep, `_iSectorSleepDelay` timeout (`CSector::_CanSleep` cpp:1062). Port sadece
  `ClientCount==0`.
- [ ] `SECF_*` flag enum yok.
- [ ] GetLocalTime / IsMoonVisible / GetLightCalc / LightFlash sector metotları yok.

### 3.3 Movement & walking — 80
- [ ] **LOS tam bayrak modeli** — `CanSeeLOS_New` (`CCharLOS.cpp:112-470`):
  `LOS_NB_TERRAIN/STATIC/DYNAMIC/WINDOWS`, `LOS_FISHING`, `LOS_NC_MULTI`,
  `LOS_NO_OTHER_REGION`; dinamik in-world item, window/BLOCKLOS_HEIGHT, multi-region
  occlusion. Port Bresenham terrain+static ray (`TerrainEngine.cs:66`).

### 3.4 Spawn — 85
- [ ] Champion monster-list / candle-list tabloları full `CHAMPIONDEF_*` resource
  block'tan okunmalı (şu an InitializeLists heuristik).
- [ ] `ICHMPL_*`/`ICHMPV_*` script accessor genişliği.

### 3.5 Pet / taming / stable — 86
- [ ] Stable native container serialization (`LAYER_STABLE`), port custom TAG.
- [ ] Pet ekonomi sub-komutları (pet-sells-loot buy/sell/sample) mesaj-only.

### 3.6 Vendor / trade — 87
- [x] **NPC skill-eğitim-ücreti** — YAPILDI (Wave 215): `VendorTrainingEngine`
  (GetTrainMax=trainer skill% ∧ absolute cap ∧ student cap, CalcTrainableAmount
  sum-cap + DOWN-lock sacrifice, TrainSkill down-lock drain, TryPay proportional).
  Wire: "train/teach <skill>" speech → HandleTrainRequest quote+RememberOffer (NPC
  tag); gold-drop-on-NPC → VendorTrainingEngine.TryPay (ClientInventoryHandler, hire
  payment'ın yanında). NOT: SphereNet lock byte 0=up/**1=down**/2=locked (Source-X
  enum DEĞİL — client convention). Test: VendorTrainingEngineTests (+8).
- [ ] Region `RestockVendors`/`NoRestock` tag + game-time restock timer (şu an
  wall-clock `RESTOCK_TIME`). (düşük öncelik)

---

## TIER 4 — Item / Housing / Character

### 4.1 Housing — 72
- [ ] Custom-design **valid-item enforcement** (`LoadValidItems`/`IsValidItem`/
  `ValidItemsContainer`).
- [ ] Design packet streaming (`SendStructureTo` multi-plane / `SendVersionTo` /
  `GetPlane`/`GetPlaneZ`).

### 4.2 Party / guild — 72
- [ ] Party live networking: `SendAddList`/`SendRemoveList`/`AddStatsUpdate`/
  `UpdateWaypointAll` (`PartyManager.cs` yok).
- [ ] Guild `r_Verb` / stone-gump verb yüzeyi tam eşlenmemiş.

### 4.3 Account / login — 78
- [ ] Per-IP guest limit, account aging, block/unblock scheduling, max-conn-per-IP
  (`AccountManager.cs` 187 satır, ince).

### 4.4 Item type davranışları — 80
- [ ] **Plant-growth sistemi** (`CItemPlant.cpp` 197 satır) — karşılığı yok.
- [ ] CommCrystal messaging shallow.

### 4.5 Character core — 82
- [ ] `CCharStat` regen tabloları + `CCharStatus` (2073 satır) derinliği kısmi.
- [ ] Jail flag yerine tam region-jail makinesi.
- [ ] STATF_INVUL townsfolk modellenmiyor.

### 4.6 Death / corpse — 82
- [ ] `IsCorpseResurrectable`/`IsCorpseSleeping` healer-rez menü yolu.
- [ ] Resurrection-via-gump menü + corpse instalist packet detayı.

### 4.7 Item core — 85
- [ ] `CItemBase` bonus-skill 1-5 + crafting-bonus key'leri (`IC_BONUSSKILL1..5AMT`,
  `IC_BONUSCRAFTING*`) first-class item key değil.

### 4.8 Ships — 85 (T0.1 dışındaki kalanlar)
- [ ] Multi-tick hız ölçekleme belirsiz — doğrula.

---

## TIER 5 — Network / Persistence / UI

### 5.1 Speech / command — 70
- [ ] GM/ON verb genişliği: script `r_Verb` keytable (data-güdümlü, sınırsız) vs 75
  hardcoded C# verb. En azından eksik yüksek-değerli verb'leri ekle.

### 5.2 Menu & prompt — 74
- [ ] `CMenu` paging helper'ları + standart menü builder'ları (ince).

### 5.3 Incoming packet — 78
- [ ] 0xBF/0xD7 extended subcommand: 14-girişlik allow-list
  (`ExtendedCommandRegistry.cs:12-27`) genişlet.
- [ ] BandageMacro, Equip/UnEquipItemMacro, GargoyleFly, WrestleDisarm/Stun.

### 5.4 Outgoing packet — 82
- [x] **PacketHealthBarStatus (0x17) + PacketHealthBarStatusNew (0x16)** — YAPILDI
  (Wave 215): poison=yeşil / freeze=sarı health-bar tint. `OnHealthBarStatusChanged`
  hook CharacterPoisonState Apply/Cure'dan tetiklenir → EngineWiring observers'a
  ForEachClientInRange broadcast (SA+/KR client gate). Test: HealthBarPacketTests (+4).
- [ ] DÜŞÜK ÖNCELİK (niche EC/display, inert-stub değer katmaz): `PacketBondedStatus`
  (0xBF.0x19), `PacketStatueAnimation`, `PacketToggleHotbar`, `PacketSignGump`,
  `PacketGameTime`, `PacketGlobalChat`(out), `PacketQueryClient`, `PacketDisplayPopup`,
  `PacketCharacterListUpdate`(0x86). Wire edilmeden eklemek anlamsız → gerçek kullanım
  gerektiğinde ekle.

### 5.5 Dialog / gump — 84
- [ ] Built-in standart sysgump kütüphanesi ince (script'e dayanıyor) — layout motoru
  tam, eksik olan hazır gump'lar.

### 5.6 Login flow — 85
- [ ] Server-list multi-server + KR/EC seed edge handling.

### 5.7 Persistence — 86
- [ ] `WORLDMULTI` yazıcı yok (evler WORLDITEM olarak persist — kabul edilebilir ama
  doğrula).
- [ ] GMPAGE / guild persistence ince.

### 5.8 Encryption — 88
- [ ] `CCryptoKeyCalc` client-version key-table auto-detect (şu an client key'leri
  dışarıdan).
- [ ] Account bcrypt (`CBCrypt`) — MD5/plain var, bcrypt yok.
