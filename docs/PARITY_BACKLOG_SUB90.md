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
- [x] **T0.2 Verb guardrail testi kanıt sağlamıyor** — YAPILDI (Wave 217): upstream
  `.tbl` pinine ek olarak gerçek SphereNet dispatch kaynaklarını yüzey bazında tarayan
  `SourceXVerbSurfaces_AreRoutedBySphereNetImplementation` eklendi; ObjBase/Char/Item
  kalıtım zinciri, client-console köprüsü, interpreter meta-verb'leri ve SERV resolver
  birlikte doğrulanıyor. Bilinen açıklar explicit `KnownPartialOrDeferred` listesinde;
  yeni route kaybı da, implement edilip listeden düşülmeyen stale borç da testi kırıyor.
  Test: SourceXVerbInventoryGuardrailTests (3/3).

---

## TIER 1 — Scripting çekirdeği (en yüksek kaldıraç, script-güdümlü uzun kuyruk)

### 1.1 VAR/LIST/DEFNAME — 65 (en zayıf)
- [x] **LIST runtime mutasyon verb'leri** — YAPILDI (Wave 215). Source-X
  CListDefMap::r_LoadVal grammar'ı `GameWorld.MutateGlobalList` (clear/add/set/
  append/sort asc|i|desc|idesc/index-set/remove/insert) + read
  `GameWorld.ResolveGlobalListRead` (count/index/findelement). Interpreter
  `LIST.<name>[.op]=v` → `_SET_LIST.` bridge; ResolveServList delege.
  Test: ParityMatrixServTests (+4). NOT: Source-X'te "pop" YOK (ajan yanılmış).
- [x] **VarMap.cs** — YAPILDI (Wave 223). `CVarDefMap` karşılaştırmasıyla düz
  insertion-order string dictionary, case-insensitive `SortedDictionary` içindeki typed
  string/integer entry modeline geçirildi. `SetInt` tipi koruyor; `GetAllEntries` native
  tipi açıyor; `GetAll`/save/TAGAT enumerasyonu deterministik Source-X key sırasına sahip.
  Numeric-looking `Set(string)` değerleri geriye uyumluluk için string kalıyor.
  Test: SourceXVarMapWave223Tests (+3).

### 1.2 Obje verb & property — 78
- [x] Eksik `CChar_functions` verb'leri — YAPILDI (Wave 215): **UNEQUIP** (ItemBounce
  to pack), **WHERE** (konum mesajı), **SUMMONTO** (uid'e/SRC'ye ışınla), **CONTROL**
  (IClientContext.Character → TryAssignOwnership). Character.TryExecuteCommand.
  Test: ScriptCharVerbParityTests (+4).
- [x] Eksik prop'lar — YAPILDI/DOĞRULANDI (Wave 215): **CANMOVE <dir>** (dest tile
  IsPassable) + **NOTOGETFLAG <uid>** (ResolveNotoFlag hook → ComputeNotoriety) EKLENDİ.
  **STEPSTEALTH** zaten vardı; **RACE** gerçek CChar_props değil (ajan yanılmış).

### 1.3 Resource section tipleri — 78
- [x] **SPHERECRYPT / KRDIALOGLIST** — DOĞRULANDI (Wave 216): ajan yanılmış, `_ =>
  Unknown` DEĞİL — ResourceHolder:123-125'te zaten `ResType.Sphere`'e map'li
  (counted+skipped, warning yok). SPHERECRYPT = Source-X client crypt key TABLOSU
  ama SphereNet key'leri CCryptoKeyCalc ile HESAPLIYOR (CalcCryptLine,
  Program.Scripting:1656) → tablo gereksiz; login-crypto path'ine dokunmak
  yüksek-risk/düşük-değer. KRDIALOGLIST = EC/KR niche. Skip haklı. DÜŞÜK ÖNCELİK:
  mortechUO custom SphereCrypt.ini key-override senaryosu istenirse eklenir.
- [ ] STAT / TIMERF / SERVERS — no-op'a katlanıyor; tek-sunucu shard için kabul
  edilebilir (SERVERS=login server list, STAT≈ADVANCE, TIMERF section nadir). DÜŞÜK.

### 1.4 FILE/DB objeleri — 80
- [x] DB async formları **AQUERY / AEXECUTE** — YAPILDI (Wave 216): ScriptDbAdapter
  QueryAsync/ExecuteAsync (worker thread varsa enqueue, yoksa inline) + Program.Scripting
  verb switch case'leri. **NUMCOLS** — DbSession.NumCols + `<DB.NUMCOLS>` +
  `db.row.numcols`. Test: GameSystemTests.ScriptDbAdapter genişletildi.

### 1.5 Trigger — 86
- [x] Kalan char trigger backlog: **@UserMailBag** — YAPILDI (Wave 221). Source-X
  `CClient::Event_MailMsg` ve `PacketMailMessage` doğrulandı; eksik legacy `0xBB`
  (9 byte, iki serial) carrier hattı PacketHandler → NetState → Program → GameClient
  olarak kuruldu. Hedefte `@UserMailBag` gönderen `SRC` ile ateşleniyor; `RETURN 1`
  bildirimi iptal ediyor, self-drop sessiz, geçersiz hedef göndereni uyarıyor.
  Test: SourceXUserMailBagWave221Tests (+4).

### 1.6 Script okuma/lexing — 86
- [x] Write-side `WriteSection`/`WriteKeyStr` — YAPILDI/DOĞRULANDI (Wave 222).
  Ayrı katman zaten `ISaveWriter` + `TextSaveWriter` olarak vardı ve WorldSaver,
  account persistence ile export yollarınca kullanılıyordu. Source-X karşılaştırmasında
  iki gerçek fark düzeltildi: `[EOF]` artık her record'dan sonra değil dosyada yalnızca
  bir kez yazılıyor; `WriteKeyStr` gibi value CR/LF'de kesiliyor ve section/key tek-satır
  token olarak doğrulanıyor. Test: SourceXScriptWriteWave222Tests (+3).

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
- [x] **MAGICF_IGNOREAR + spell reflection zinciri** — YAPILDI (Wave 225).
  MAGICFLAGS düşük bitleri Source-X `MAGICFLAGS_TYPE` ile birebir hizalandı;
  SphereNet extension flag'leri çakışmayan yüksek bitlere taşındı. `IGNOREAR` magic
  damage için elemental resist/AR aşamasını bypass ediyor (skill Magic Resistance
  ayrı kalır). Magic Reflect artık tek-reflect, çift-reflect bounce-back ve
  `NOREFLECTOWN` + `DELREFLECTOWN` consume/absorb dallarını Source-X gibi çözüyor;
  SRC/damage attribution self-reflect'te korunuyor. Test:
  SourceXMagicFlagsWave225Tests (+4).
- [x] Cast timing **FCR/FC** property modeli — YAPILDI/DOĞRULANDI (Wave 224).
  Source-X `Spell_CastStart` yalnızca **FASTERCASTING** uygular: toplam char+equipped
  property değeri başına CAST_TIME'dan 2 tenth düşer, taban 1 tenth. SphereNet'in
  Source-X'te olmayan `max(400,1500-skill)` post-cast cooldown'u kaldırıldı.
  **FASTERCASTRECOVERY** upstream'de property/status yüzeyinde var fakat
  `CCPropsItemEquippable.cpp` açıkça *unimplemented*; davranış eklenmedi. Her iki key
  CHARDEF/ITEMDEF/Character/Item script yüzeyine eklendi. Test:
  SourceXCastTimingWave224Tests (+4).

### 2.3 Skills — 84
- [x] **Cartography** ve **Camping** — YANLIŞ ALARM (Wave 215 doğrulama):
  SkillHandlers.cs:760 (Cartography→OnCraftSkillUsed, doğru Source-X modeli =
  harita crafting), :825 (Camping→bedroll+campfire+safe-logout, zengin). İkisi de
  VAR ve fonksiyonel.
- [ ] Bushido/Ninjitsu/Chivalry/Necromancy/Focus/Imbuing/Mysticism/Spellweaving —
  enum var, native handler yok. (necro spell backlog ile birlikte, büyük)

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
- [x] **SE (era3) / ML (era4) hız formülleri** — YAPILDI (Wave 226).
  `Calc_CombatAttackSpeed` birebir port edildi: SE, `SPEEDSCALEFACTOR` ve 0.25 sn
  tick dönüşümünü; ML, kendi weapon-speed formatı ve integer SSI çarpanını kullanır;
  ikisinde minimum 5 tick korunur. `INCREASESWINGSPEED` artık char + tüm equipped
  item/ITEMDEF aggregate'idir (era2 de aynı doğru aggregate'i kullanır) ve script
  yüzeylerinde tanınır. `Item.Speed`, instance/ITEMDEF `OVERRIDE.SPEED` fallback'ini
  uygular. Test: SourceXCombatSpeedWave226Tests (+4).
- [x] **Samurai-Empire / Bushido parry branch** — YAPILDI (Wave 227).
  Source-X `COMBATPARRYINGERA` bitmask'i (pre-SE/SE formula, shield/one-hand/
  two-hand izinleri, AR-scaling biti) config → Character runtime hattına eklendi.
  `Calc_CombatChanceToParry`; SE feature 0x02 kapısı, shield'da `(Parry-Bushido)/40`,
  one-hand `Parry*Bushido/48000`, two-hand `/41140`, legacy max karşılaştırması,
  GM +5 ve DEX<80 erozyonuyla port edildi. Başarılı SE weapon parry Bushido gain
  dener; Parrying attempt difficulty artık hesaplanan chance'dır. Test:
  SourceXBushidoParryWave227Tests (+5).
- [x] **PARRYERA_ARSCALING shield-AR + LAYER_SPELL_Protection AR** — YAPILDI
  (Wave 228). Shield AR legacy modda hands bölgesinin %7 coverage'ına katılır;
  AR-scaling bitinde `min(baseAR/2, (baseParry*baseAR)/2000+1)` ve %100 coverage
  uygulanır. Protection/Arch Protection spell-memory effect'i artık geçici AR ekler,
  ortak layer'da birbirini yeniler ve expiry/save-reapply yaşam döngüsünde geri alınır.
  Test: SourceXParryArmorScalingWave228Tests (+8).
- [x] **Horrific-Beast / gargoyle-berserk hasar amplifier** — YAPILDI (Wave 229).
  `FEATURE_AOS_UPDATE_B` altında Horrific Beast polymorph-memory, silahsız taban
  hasarı 5-15'e çeker ve cap'lenmiş INCREASEDAM sonrasına +25 ekler; expiry/save
  yaşam döngüsünde geri alınır. Source-X `RACIALFLAGS` config/runtime hattı eklendi;
  Gargoyle Berserk kayıp her 20 HP için +15, en çok +60 Damage Increase uygular.
  Test: SourceXHorrificBerserkWave229Tests (+10).

### 2.6 Crafting & gathering — 86
- [x] **Material/renk seçim menüsü (OSI craft gump)** — YAPILDI (Wave 230).
  Primary resource varyantları hue bazında ve yeterli stack miktarıyla listelenir;
  birden çok uygun material varsa isim, miktar, tile preview ve hue içeren sayfalı
  seçim gump'ı açılır. Seçilen hue completion re-check, success tüketimi, fail-loss
  ve crafted item rengine kadar korunur; stale/eksik seçim yeniden doğrulanır.
  Test: SourceXCraftMaterialWave230Tests (+5).
- [x] **Zorluk eğrisi resource-özel** — YAPILDI (Wave 231). REGIONRESOURCE
  `SKILL` artık tüm `CValueCurveDef` noktalarını script sırasıyla korur ve Source-X
  `m_vcSkill.GetRandom()/10` gibi her gather denemesinde 0..999 örnekle segment
  interpolasyonu yapar. Sabit midpoint ve upstream'de olmayan sert SkillMin kapısı
  kaldırıldı; normal skill S-curve sonucu belirler. Test:
  SourceXGatherDifficultyWave231Tests (+10).
- [x] **Çok-katmanlı BONUS resource verimi** — YAPILDI/DOĞRULANDI (Wave 232).
  Literal REGIONRESOURCE `BONUS=` yok; gerçek Source-X katmanları port edildi:
  node `AMOUNT.GetRandom()`, verim `REAPAMOUNT.GetRandomLinear(baseSkill)`, sıfır/
  tanımsız durumda `AMOUNT.GetRandomLinear(baseSkill)/2` fallback'i ve minimum 1.
  `RACIALF_HUMAN_WORKHORSE` ayrıca Felucca mining node'una +1 ore, Trammel tree
  node'una +2 log ekler. Tüm AMOUNT/REAPAMOUNT curve noktaları korunur. Test:
  SourceXGatherYieldWave232Tests (+12).
- [x] **Cartography harita-yapımı craft bağlantısı** — YANLIŞ ALARM/
  DOĞRULANDI (Wave 233). Source-X `IT_CARTOGRAPHY -> sm_cartography ->
  Skill_MakeItem` zincirinin SphereNet karşılığı zaten
  `CartographyTool -> OpenCraftingGump(Cartography) -> CraftingEngine` olarak
  mevcut; `SKILLMAKE=Cartography` harita tarifleri tool ve blank-map gereksinimiyle
  yükleniyor, normal stroke/tüketim/üretim hattına giriyor. Eksik kalan Source-X
  `0x249` çizim sesi tamamlandı. Test: SourceXCartographyCraftWave233Tests (+4).

---

## TIER 3 — NPC / Dünya / Hareket

### 3.1 Weather / light / season — 68
- [x] **Per-sector moon-phase ışık modeli** — YAPILDI (Wave 234).
  `CSector::GetLocalTime/GetLightCalc` modeli port edildi: harita kolonuna göre
  24 saatlik yerel saat ofseti, 105/840 dakikalık Trammel/Felucca fazları,
  moonrise/moonset görünürlük pencereleri, iki ayın brightness tabloları, hava ve
  dungeon ışığı uygulanıyor. Login/resync/rez/season/region/tick ışık paketleri
  artık karakter konumundaki sektör hesabını kullanıyor. Test:
  SourceXSectorMoonLightWave234Tests (+18).
- [x] **LightFlash + IsMoonVisible** — YAPILDI (Wave 235). Ay görünürlük
  pencereleri Wave 234'te port edildi. `CSector::LightFlash` hedef sektördeki
  canlı/online/NightSight'sız oyunculara önce tam parlaklık, ardından hesaplanan
  sektör ışığını gönderir; Lightning ve Chain Lightning effect hattına bağlandı.
  Test: SourceXLightFlashWave235Tests (+5).
- [x] **@EnvironChange** (`CTRIG_EnvironChange`) — YAPILDI (Wave 236).
  Character artık light+weather+season environment snapshot'ı tutar; login ilk
  baseline'ı kurar, sector/region ışık değişimi, weather callback'i ve season
  broadcast'ı üç alandan herhangi biri değiştiğinde trigger'ı bir kez ateşler.
  LightFlash snapshot'ı değiştirmediği için trigger üretmez. Test:
  SourceXEnvironChangeWave236Tests (+5).

### 3.2 Sector — 75
- [x] Sleep modeli — YAPILDI (Wave 239). Source-X `CSector::_CanSleep` portu:
  `Sector.CanSleep(nowMs, checkAdjacents)` — sleep-delay 0 veya SECF_NoSleep→asla,
  ClientCount>0→hayır, SECF_InstaSleep→hemen, 8-komşu adjacency sweep (komşu
  uyuyamıyorsa bu da uyuyamaz, non-recursive), yoksa `now-LastClientTimeMs>SleepDelayMs`
  timeout. `SleepDelayMs` static (default 10dk, Source-X g_Cfg._iSectorSleepDelay).
  `<CANSLEEP>` script prop artık tam modeli kullanıyor (eskiden sadece ClientCount==0).
  GameWorld: GetAdjacentSector callback (InitMap), RefreshActiveSectors player-sector'a
  LastClientTimeMs damgalıyor, SECF_NoSleep sektörler `_alwaysAwakeSectors` ile her
  zaman tick set'inde (OnNoSleepChanged callback). Test: SourceXSectorSleepWave239Tests
  (+5). NOT: fiili tick-uyku modeli spatial active-set olarak kalıyor (temporal timeout
  yerine oyuncu-etrafı pencere — geçerli alternatif); CanSleep predicate + SECF_NoSleep
  davranışsal olarak faithful, InstaSleep model/script-düzeyinde.
- [x] `SECF_*` flag enum — YAPILDI (Wave 239): `Core.Enums.SectorFlag` (NoSleep=0x1,
  InstaSleep=0x2, Source-X SECF_* birebir). Script yüzeyi: FLAGS/NOSLEEP/INSTASLEEP
  read+write (Sector.TryGet/SetProperty).
- [x] GetLocalTime / IsMoonVisible / GetLightCalc / LightFlash sector metotları
  Wave 234-235 ile tamamlandı.

- [x] **LOS bayrak modeli** — YAPILDI (Wave 240+241). Source-X CanSeeLOS_New'in
  fonksiyonel olarak anlamlı tüm pass'leri portlandı:
  - Wave 240: **LOS_NB_DYNAMIC** dinamik in-world item occlusion (eskiden placed
    wall'dan ateş edilebiliyordu) — TerrainEngine.DynamicOccluderAt callback +
    GameWorld.HasDynamicLosOccluder. **LOS_NB_WINDOWS** window exception
    (TileFlag.Window artık kesmiyor, statik+dinamik).
  - Wave 241: **LOS_NB_MULTI** multi/custom-house wall occlusion — placed
    house/ship component'leri (MUL multi.mul def) VE committed custom-house design
    tile'ları artık LOS'u kesiyor (GameWorld.HasMultiLosOccluder, WalkCheck'in
    virtual-geometry taramasını aynalar, WalkCheck.ResolveCustomDesign hook'unu
    paylaşır) — eskiden ev/gemi duvarından ateş edilebiliyordu. **BLOCKLOS_HEIGHT**
    (CAN_I_BLOCKLOS_HEIGHT) — TerrainEngine.GraphicBlocksLos ortak helper'ında
    (statik+dinamik+multi üç yol). **LOS_FISHING** — LosFlags enum + flag-parametreli
    CanSeeLOS(from,to,flags); dist>=2'de terrain su (IsWet) değilse blok (engine-level;
    fishing skill'e wiring ayrı opt-in, davranış regresyonu önlemek için ertelendi).
  Test: SourceXLosDynamicWave240Tests(+2) + SourceXLosRemainingWave241Tests(+2:
  custom-house wall bloke + fishing su-yolu). ERTELENEN (deep, per-cell region modeli
  gerektirir, minimal gameplay etkisi): LOS_NO_OTHER_REGION, LOS_NC_MULTI (multi-region
  crossing), LOS_NB_LOCAL_* region-local varyantları, GM-combat-pass (caller-bağımlı).

### 3.4 Spawn — 85
- [x] Champion list/accessor parity — YAPILDI (Wave 216): denetim "def block'tan
  okunmalı" sandı ama Source-X `CCChampion::InitializeLists` de tamamen HESAPLAMALI
  (def'te MONSTERS/CANDLES listesi YOK) — o kısım zaten doğruydu. GERÇEK bug: candle
  listesi Source-X'te her değeri BAŞA insert eder, bizim port SONA ekliyordu →
  LEVELMAX=7'de `[3,2,2,3,3,3]` (yanlış) vs `[3,3,3,3,2,2]` (doğru), seviye başına
  kırmızı-mum eşiği hatalı. Düzeltildi (Insert(0,c)). Ayrıca eksik ICHMPL/ICHMPV
  accessor'lar eklendi: `ADDREDCANDLE`/`ADDWHITECANDLE` set-key (uid ile re-link),
  `NPCGROUP<n>[.<i>]` read (g_Cfg.ResourceGetName reverse-lookup) + write override,
  `MULTICREATE` no-op verb (Source-X stub). Test: ChampionSystemTests (+3).

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
- [x] Custom-design **valid-item enforcement** — YAPILDI (Wave 238). Source-X
  `CItemMultiCustom::IsValidItem` gate: designer artık 0xD7 stream'inde keyfi
  grafik yerleştiremiyor. `HouseDesignValidItems.IsValidBuildTile(id, isGm)` —
  range check (id 0<id<0x4000, ITEMID_MULTI altı; multi/0 reddedilir, GM dahil
  herkese uygulanır), sonra GM whitelist'i bypass eder, sıradan designer opsiyonel
  whitelist'e (`RegisterValidItems`, Source-X ValidItemsContainer; boşsa range-only
  — client CSV'leri olmadan çalışır) uyar. Build/Stairs/Roof'a bağlandı. Whitelist
  static → ResetEngineStatics.ClearValidItems. Test: CustomHouseDesignTests (+2,
  range reddi + whitelist + GM bypass). NOT: Source-X'in nihai whitelist'i client
  house-design CSV'lerinden (doors/walls/floors/roof/stairs.txt) gelir; garanti
  olmadığı için range-enforcement default, whitelist opsiyonel hook.
- [x] Design packet streaming — DOĞRULANDI (Wave 238): zaten var —
  `PacketHouseDesignDetailed` (0xD8 multi-plane, MaxTilesPerPlane=750 mode-0 split,
  ClassicUO-parse round-trip test'li) = SendStructureTo; `PacketHouseDesignVersion`
  (0xBF sub 0x1D, GameClient.Housing:170 wired) = SendVersionTo. Plane-by-Z bölme
  (GetPlane) yerine tile-count mode-0 split kullanılıyor (planeZ=0 client'ta yok
  sayılır) — kabul edilebilir sadeleştirme, client doğru parse ediyor.

### 4.2 Party / guild — 72
- [x] Party live networking — YAPILDI (Wave 216): denetim `PartyManager.cs`'e
  bakıp eksik sandı ama server tarafı zaten vardı — `SendAddList`
  (`PacketPartyMemberList`), `SendRemoveList` (`PacketPartyRemoveMember`,
  `BroadcastPartyUpdate`), `AddStatsUpdate` (`PushPartyStats`, Program.Tick.cs)
  mevcut. GERÇEK boşluk `UpdateWaypointAll` idi (waypoint paketleri yalnızca corpse
  için kullanılıyordu). Eklendi: PushPartyStats her tick üye pin'ini yayar
  (`PacketWaypointAdd` type:2, `SupportsMapWaypoints` gate); ayrılma/kick/disband'de
  `BroadcastPartyUpdate` + disband yolu `PacketWaypointRemove` ile pin'leri temizler.
  Test: PartyWaypointPacketTests (+2, add type:2 + remove byte-layout).
- [x] Guild `r_Verb` / stone-gump verb yüzeyi — YAPILDI (Wave 237): denetim "tam
  eşlenmemiş" sandı ama yüzey ~%90 zaten vardı — `r_Verb` alt kümesi (DECLAREWAR/
  PEACE/INVITEWAR/APPLYTOJOIN/JOINASMEMBER/ELECTMASTER/CHANGEALIGN/RESIGN/
  TOGGLEABBREVIATION/ALLMEMBERS/ALLGUILDS), gump-button yüzeyi (accept/title/war/
  peace/charter/dismiss/ally/rename/abbrev/fealty, HandleGuildGumpResponse buttons
  1-21) ve STC_* prop'ları (ABBREV/ALIGN/MASTER*/CHARTER/WEBPAGE/MEMBER.COUNT)
  mevcut. GERÇEK boşluk **guild-owned houses/ships**: Source-X `CItemStone`
  ADDHOUSE/ADDSHIP (STC_ write→AddMulti), DELHOUSE/DELSHIP (ISV_ verb→DelMulti),
  HOUSES/SHIPS (GetHouseCountReal/GetShipCountReal), MAXHOUSES/MAXSHIPS eklendi.
  GuildDef `_houses`/`_ships` HashSet (uid tek tip), AddHouse/AddShip/DelMulti +
  GUILD.HOUSES/SHIPS/MAXHOUSES/MAXSHIPS tag persistence (ölü multi reload'da düşer).
  Test: GameSystemTests.GuildStone_HouseShipSurface +
  GeneralGameplayIntegrityTests.GuildHousesShips_SurviveSaveLoad. NOT: REFUSECANDIDATE
  explicit gump button yok (master accept-etmeyerek reddediyor), LOYALTO read SRC-bağımlı
  (kabul edilebilir sınır).

### 4.3 Account / login — 78
- [x] **block/unblock scheduling** — YAPILDI (Wave 216): IPBlockList artık zamanlı
  blok destekliyor (expiry Unix-ms, 0=kalıcı, lazy-expire), NowMsProvider injectable
  (test edilebilir). HandleServBlockIp `,seconds` decay'ini artık onore ediyor (eskiden
  yok sayılıyordu). Test: IpBlockListTests (+4).
- [ ] Per-IP guest limit, account aging, max-conn-per-IP — bağlantı-kabul path'ini
  değiştiren daha büyük özellikler. ERTELENDİ.

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
- [~] `CItemBase` BONUSSKILL1-5(+AMT) / BONUSCRAFTING* — KAPSAM NETLEŞTİRİLDİ
  (Wave 216): key'ler tag olarak saklanabilir; ASIL boşluk = **efektif-skill
  katmanı**. SphereNet `GetSkill` ham base döner (equipment bonus agregasyonu yok);
  giyilen item'in skill'i yükseltmesi için base-vs-efektif ayrımı + equip/unequip
  recompute gerekir — combat/crafting/skill-gain'e yayılan HOT-PATH mimari ekleme,
  temiz key-add DEĞİL. ERTELENDİ (riskli, ayrı dalga). AOS suit-property agregasyonu
  genel eksiğiyle birlikte ele alınmalı.

### 4.8 Ships — 85 (T0.1 dışındaki kalanlar)
- [ ] Multi-tick hız ölçekleme belirsiz — doğrula.

---

## TIER 5 — Network / Persistence / UI

### 5.1 Speech / command — 70
- [x] GM/ON verb genişliği: script `r_Verb` keytable (data-güdümlü, sınırsız) vs 75
  hardcoded C# verb. Wave 218: ObjBase **CLICK/MESSAGEUA/MOVENEAR/MOVETO/MSG/USEITEM**
  (world/sector-safe hareket, gerçek single-click ve item-use köprüleri); Char
  **DUPE/SKILL/SYSMESSAGELOCEX** (state+skill clone, SkillHandlers hook, 0xCC affix);
  Item **DROP/UNEQUIP/USEDOOR** (container/equip detach, source pack bounce, locked-door
  bypass) YAPILDI. Test: SourceXObjBaseVerbWave218Tests (+10) + verb guardrail (3/3).
  Wave 219: kalan Client **ADD/ADDCHAR/ADDITEM/CLOSEPAPERDOLL/CTAGLIST/GMPAGE/
  INFORMATION/RESEND/SAVE/SELF/SKILLSELECT/VERSION** route'ları YAPILDI. `ADD*`
  mevcut target/Create/CreateLoot hattını kullanıyor ve stack amount target cevabına
  kadar korunuyor; SAVE/GMPAGE CommandHandler'a, SELF aktif target callback'ine,
  SKILLSELECT normal client skill pipeline'ına delege. Test:
  SourceXClientVerbWave219Tests (+10) + verb guardrail (3/3). Wave 220: son ObjBase
  **DAMAGE** route'u YAPILDI — target-only `@GetHit` rewrite/cancel, explicit
  physical/fire/cold/poison/energy split+resist, attacker kaydı, action/spell interrupt,
  damage/health paketleri+death engine ve item `@Damage`/durability-break zinciri.
  Test: SourceXDamageVerbWave220Tests (+4). Wave 217'deki 26 doğrulanmış route borcu
  **0**; guardrail backlog'u boş. SERV **B/SECURE** zaten interpreter'dan
  `_BROADCAST`/`_SECUREMODE` handler'larına route ediliyor (yanlış alarm).

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
