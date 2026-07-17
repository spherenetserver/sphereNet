# Sub-90 Parity Backlog (Source-X = 100)

Kod-tabanlı denetimden (2026-07) çıkan, 90'ın **altında** puan alan her kategori ve
somut eksik listesi. Kaynak: 5 paralel salt-kod karşılaştırma ajanı. Her madde
implement edilmeden önce gerçek kod karşılaştırmasıyla DOĞRULANIR (ajan iddiaları
yanlış olabilir). Tamamlanınca `[x]` işaretle + Wave numarası + kanıt satırı ekle.

Puan referansı: kategori adının yanındaki sayı = mevcut kod-fidelity tahmini.

---

## KAPANIŞ DURUMU (Wave 270 sonrası, 2026-07-18)

Sub-90 push'un yüksek/orta-değer maddeleri **Wave 270'e kadar kapatıldı**. Her madde
uygulanmadan önce gerçek Source-X kod karşılaştırmasıyla doğrulandı; birkaç "büyük sanılan"
madde aslında **Source-X'te de olmayan / yanlış-çerçevelenmiş** çıktı (kanıtla `[x]`
işaretlendi, kasıtlı yapılmadı):
- **Chivalry (201-210):** Source-X C++'ta 0 case → generic/script-driven, native handler yok → yapılmadı (2.3).
- **Per-element resist MAX cap:** Source-X damage'da enforce etmiyor (display-only) → sadece persistence fix (2.4).
- **Sysgump kütüphanesi:** Source-X de script-driven, tüm dedicated gump'lar zaten var → sadece 0xB0 fallback (5.5).

Wave 265-270 eklenenleri: BandageMacro+virtue transport (5.3), region-jail (4.5), HCI/LUCK
suit agregasyonu (2.4), server-list multi-server (5.6), 0xB0 gump fallback (5.5), plant-growth
(4.4).

**KALAN (kasıtlı ertelenmiş — kapanış kararları):**
- 🔴 **BÜYÜK: Spell school'ları** — Bushido/Ninjitsu/Focus/Imbuing/Mysticism/Spellweaving
  (2.2/2.3). Necromancy gibi per-school C# effect handler'ları gerektirir (Source-X'te
  case'leri VAR, gerçek iş); her biri ayrı çok-wave'lik effort → ayrı proje olarak ertelendi.
  (Chivalry bu listeden çıktı — Source-X'te yok.)
- 🟢 **Düşük öncelik / non-gap (gerekçeli, inline dökümante):** STAT/TIMERF/SERVERS no-op (1.3),
  stable native container (fonksiyonel eşdeğer var, 3.5), pet ekonomi sub-komutları (3.5),
  per-IP guest/max-conn (DDoS-hardening, 4.3), IsCorpseSleeping (sleeping mekaniği yok, 4.6),
  CMenu paging (niş, 5.2), niche EC/display paketleri (inert-stub, 5.4), CCryptoKeyCalc
  key-table auto-detect (5.8), account bcrypt (harici NuGet — KULLANICI: gerek yok, 5.8),
  KR/EC 0xFFFFFFFF seed handshake (canlı KR client gerekir, 5.6).

Aşağıdaki tier'lar tam denetim geçmişini kanıt satırlarıyla korur.

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
- [x] Temiz CChar prop kuyruğu — YAPILDI (Wave 249): **STAMINA** (STAM alias'ı,
  CChar.cpp:3170), **STATPERCENT.<stat>** (havuz/adjusted-max %, GetStatPercent
  CCharStatus.cpp:482; Sphere STR→hits/DEX→stam/INT→mana), **BLOODCOLOR** (yeni
  _bloodHue field, FormatHex GET, CChar.cpp:2600), **FOLLOWERSLOTS** (ControlSlots'a
  per-char override; GET computed, SET override — SetDefNum semantiği, CChar.cpp:2353).
  BLOODCOLOR/FOLLOWERSLOTS persist. **TARGETCLOSE** ZATEN vardı (ClientScriptConsole
  Handler:1683, ajan doğruladı → REJECT). Test: SourceXWave249Tests (+5) + SaveFormat.

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
- [~] Necromancy (101+) / Bushido / Ninjitsu / Spellweaving /
  Mysticism spell **efektleri** (şu an enum-only, `SpellTypes.cs:32-40`). NOT: Chivalry
  (201+) bu listeden ÇIKARILDI — 2.3'e bak: Source-X'te C++ handler'ı yok, generic/script-
  driven → gerçek açık değil, kasıtlı yapılmadı. BAŞLADI
  (Wave 253 — necromancy foundation): recon gösterdi ki necro data plumbing'i ZATEN
  var (SpellRegistry, RESOURCES/MANAUSE/SKILLREQ generic, NECROMANCY/SpiritSpeak
  skill'leri, [SPELL 101-117] script def'leri); eksik olan effect handler'ları +
  state modeli. İlk iki spell: **CurseWeapon (104)** ve **WraithForm (116)** —
  `ApplySpecificSpell` case'leri, `ScheduleEffectExpiry` ile timed state (expiry/
  persist/revert bedava). WraithForm = HorrificBeast deseni (StatFlag.Polymorph +
  WraithFormActive, IsPolymorphLayerSpell'e eklendi); CurseWeapon = level'i eff'te
  saklar (ActiveSpellEffect.CurseWeaponLevel, serialize/deserialize'a append,
  ApplyDeltas/RevertDeltas state'i kurar/temizler). Save/load round-trip test'li.
  Wave 254 debuff tranche: **CorpseSkin (103)** + **MindRot (108)** — ikisi de
  spell-type-identified timed state (WraithForm deseni, yeni persist alanı YOK).
  CorpseSkin: ApplyCorpseSkinResists (ResFire/Poison −15, ResCold/Physical +10,
  Source-X PolyStr/PolyDex; sign=±1 apply/revert), ApplySpecificSpell case +
  ApplyDeltas/RevertDeltas simetrik (save-revert-reapply test'li). MindRot:
  Character.MindRotActive → EffectiveManaCost(caster,def) helper `+cost/10`
  (Source-X LOWERMANACOST −10 = +%10), CastStart mana-check + CastDoneCore
  deduction iki site de kullanır. Test: SourceXWave254Tests (+5: resist shift/
  revert, save simetri, mindrot state, +%10 mana, yetersiz-mana cast blok).
  Wave 255 DOT engine + tranche: **generic periodic-DOT** ActiveSpellEffect'e
  eklendi (DotCharges/IntervalMs/NextTickMs/DamagePerTick/Power/DamageType/Direct/
  Source), ProcessExpirations başında ProcessDotTicks pass'i (her iki tick path'i
  zaten çağırır → bedava wire). ComputeDotDamage/NextDotIntervalMs/ApplyDotDamage
  (resist gate + RecordAttack + broadcast + death); snapshot-based (tick victim'i
  öldürüp ClearAllEffectsOnDeath ile listeyi mutasyona uğratabilir). **PainSpike(109)**
  = 10 tick × total/10 direct (DAMAGE_GOD, resist bypass), total=max(10,(SS-MR)/100+18).
  **Strangle(111)** = power=max(4,SS/100) charges, rand(power-2..power+1)×(3-2*curStam/
  maxStam) poison, delay 5s→4→3→2→1. In-flight DOT persist EDİLMİYOR (kısa ömürlü,
  restart'ta biter — GetPersistedEffectRecords skip). Test: SourceXWave255Tests (+4:
  painspike fixed/resist-bypass/pre-tick, strangle damage+terminate, fatigue-scaling).
  Wave 256 damage-reaction tranche: **BloodOath(102)** + **EvilOmen(105)** —
  Source-X OnTakeDamage (CCharFight.cpp:687-703) victim-side. BloodOath: state
  caster'da (BloodOathEnemy uid + level, MagicResist/20+10 clamp 10-90), melee
  ResolveAttack'te bonded victim linked-enemy vurulunca +%10 kendine + reflect
  (100-level)% attacker'a (fixed, no-loop); Reactive/REFLECTPHYSICALDAM bloğunun
  yanına eklendi. Effect fields (BloodOathEnemy/Level) ApplyDeltas/RevertDeltas
  live save-cycle için; disk persist skip (kısa ömür + live enemy uid). EvilOmen:
  Character.EvilOmenActive + lazy-expiry ConsumeEvilOmen() (ActiveSpellEffect DEĞİL,
  one-shot); melee'de +%25 damage-amplify + consume, Poison spell'de +1 level
  (min 5) + consume. Test: SourceXWave256Tests (+6: reflect linked/unlinked, cast
  bond+expiry, evil omen melee amplify+consume, expired consume, poison +1).
  Wave 257 area-damage tranche: **PoisonStrike(110)** + **Wither(115)** — explicit
  ApplySpecificSpell case'leri (reference-script flag edit'i İSTEMEDEN). Yeni
  `DealSpellDamage(caster,victim,dmg,type)` helper (elemental resist + RecordAttack +
  broadcast + interrupt + death; Damage branch/ApplyDotDamage deseni). PoisonStrike:
  primary'e full poison + 2-tile splash'a yarım. Wither: caster çevresi 4-tile herkese
  cold. Effect base==scale → EvalInt scaling YOK (Damage flag'siz), deterministik.
  Test: SourceXWave257Tests (+3: primary/splash/distant, wither radius, resist).
  Wave 258 form tranche: **LichForm(107)** + **VampiricEmbrace(113)** — WraithForm
  deseni (StatFlag.Polymorph + form-bool + fixed resist deltas, IsPolymorphLayerSpell'e
  eklendi → form-swap). LichForm: ResFire-10/Poison+10/Cold+10. VampiricEmbrace:
  ResFire-10 + CombatEngine.ApplyAosOnHitEffects'te VampiricEmbraceActive → leechLife
  +20 (weapon'suz da). Test: SourceXWave258Tests (+4: lich resist/expiry, vamp resist,
  vamp leech unarmed, form-swap).
  Wave 259 summon: SummonCreature hardened (bodyId param + StatFlag.Conjured + return
  creature); **VengefulSpirit(114)** = Revenant (0x2EE) summon, owned+FightTarget=enemy,
  kısa süre. Test: SourceXWave259Tests (+1).
  Wave 260: **AnimateDead(101)** = CastDone corpse-item interception (Mark/DispelField
  deseni), humanoid corpse→zombie(0x3) else corpse.Amount creature body, owned summon,
  corpse consume. Test: SourceXWave260Tests (+3: humanoid/creature/non-corpse).
  Wave 261: **SummonFamiliar(112)** = Summon-flag dispatch'te default familiar body
  (0x13D); creature-selection menu ertelendi. Test: SourceXWave261Tests (+1).
  **NECROMANCY TAMAMLANDI: 17'den 16 spell** (106 HorrificBeast önceden vardı).
  KALAN sadece **Exorcism(117)** — Source-X'in KENDİSİ de implement ETMEMİŞ
  (CCharSpell.cpp:4147 commented `case SPELL_Exorcism:*/`, script FLAGS/EFFECT boş);
  eşleşecek referans yok → Source-X-parity gereği ertelendi. EvilOmen'in next-spell
  -%50 MR yarısı ertelendi (resist hook). Chivalry/Bushido/Ninjitsu/Spellweaving/
  Mysticism dokunulmadı (ayrı schools).
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
- [ ] Bushido/Ninjitsu/Focus/Imbuing/Mysticism/Spellweaving —
  enum var, native handler yok. (necro spell backlog ile birlikte, büyük)
- [x] **Chivalry (201-210)** — GEÇERSİZ MADDE / DOĞRULANDI (2026-07, 3 paralel recon):
  "native handler yok = açık" premisi YANLIŞ. Source-X `CCharSpell.cpp`'te chivalry'nin
  **HİÇ** case'i yok (grep: `case SPELL_(Cleanse_by_Fire|Close_Wounds|Consecrate_Weapon|
  Dispel_Evil|Divine_Fury|Enemy_of_One|Holy_Light|Noble_Sacrifice|Remove_Curse|
  Sacred_Journey):` = 0 eşleşme) — kontrast: necromancy'nin onlarca case'i VAR (Horrific_Beast
  :626, Curse_Weapon:950, Strangle:1920, Pain_Spike:1957 ... o yüzden C#'a taşındı). İki
  chivalry layer'ı (`LAYER_SPELL_Consecrate_Weapon`/`Divine_Fury`, uofiles_enums.h:633-634)
  tüm `.cpp` ağacında hiç okunmuyor — ölü enum slotu. Source-X chivalry'yi TAMAMEN generic
  flag (SPELLFLAG_HEAL/DAMAGE/AREA, LAYER buff-memory) + script pack ile sürüyor; aktif pack
  (`Scripts-X-main/spells/spells_chivalry.scp`) blokları `@Trigger`'sız düz FLAGS/EFFECT/
  LAYER/MANAUSE/TITHINGUSE. Consecrate/Divine Fury/Enemy of One combat efektlerini C# olarak
  yazmak = Source-X'te olmayan OSI semantiği uydurmak (parity ihlali) → KASITLI YAPILMADI,
  Exorcism gibi. SphereNet'in mevcut generic motoru bu flag'lerin ürettiğini zaten üretiyor.
  TEK gerçek Source-X-destekli açık: **tithing tüketimi** (`Calc_SpellTithingCost`,
  CCharSpell.cpp:2501; SphereNet `SpellDef.TithingCost` yükler ama kontrol/düşmez) —
  düşük değer, ayrı istenirse eklenir. Skf: SkillType.Chivalry=51, SpellTypes 201-210 zaten
  tanımlı; NPC AI beneficial-cast whitelist'inde (NpcAI.Magic.cs:190).

### 2.4 Combat flag & AOS on-hit — 85
- [x] **REFLECTPHYSICALDAM** — YAPILDI (Wave 215): defender'ın suit'i hasarın %'sini
  (cap 250) attacker'a geri yansıtır, Reactive spell'den ayrı, fixed damage (recursion
  yok). CombatEngine.ResolveAttack. Test: CombatWaveC5AosOnHitTests.
- [x] Era-2 def-side **INCREASEDEFCHANCE / INCREASEHITCHANCE** — YAPILDI (Wave 215):
  `(100 + min(HCI,45))` / `(100 + min(DCI,45))` çarpanları (eskiden düz 100).
  CombatEngine.CalcHitChanceCore era 2. Test: CombatWaveC5AosOnHitTests.
- [x] **Curse-Weapon leech add + Wraith-Form mana drain** — YAPILDI (Wave 253,
  necromancy grubu). LAYER_SPELL buff-item modeli GEREKMEDİ: Source-X combat
  hook'ları zaten hit-anında bir state flag OKUR (LayerFind), cast'e bağlı değil.
  SphereNet karşılığı: iki transient Character state — `WraithFormActive` (bool,
  HorrificBeastActive deseni) ve `CurseWeaponLevel` (int). `CombatEngine.
  ApplyAosOnHitEffects`: (1) `weapon != null && CurseWeaponLevel>0` → leechLife'a
  curse level eklenir (CCharFight.cpp:2272-2275, weapon-gated), (2) `WraithFormActive`
  → manaDrain'e `5 + 15*SpiritSpeak/1000` eklenir, target mana'sıyla cap'li
  (CCharFight.cpp:2299-2304). State'i Curse Weapon (104) / Wraith Form (116)
  spell case'leri kurar (aşağı bak). Test: SourceXWave253Tests (wraith drain
  deterministik + SpiritSpeak-scaled + cap, curse weapon-gated via OnLeechEffect).
- [x] **AOS suit-property agregasyonu — elemental resist slice** — YAPILDI (Wave
  262, "büyük mimari" grubunun kalan parçası; Wave 252 skill, bu resist). Recon
  kararı: **live-scan-on-read**, Source-X'in equip-time accumulation'ı DEĞİL —
  SphereNet'te Equip/Unequip recompute hook'u YOK + her mutation path'in delta
  back-out'u (equip/unequip/delete/decay/trade/death/loot/GM-remove/save) gerekirdi;
  kaçırılan path save'e phantom bonus yazar (persistence hazard). Live-scan =
  base field temiz, mutation yok, back-out yüzeyi yok (GetAdjustedSkill deseni).
  `CombatEngine.EffectiveResist(ch,type)` = base + `SumEquippedItemProperty`
  (OneHanded..Horse layer scan, item-tag→ITEMDEF fallback), clamp 0-100; EffRes
  Physical/Fire/Cold/Poison/Energy getter'ları. Tüketiciler: ApplyElementalResist
  (1570-82), iki split-resist site (720-24/1336-40), CharacterPoisonState tick,
  0x11 StatusFull paketi (paperdoll suit'i yansıtsın). Base setter/getter + script
  RESxxx prop + spell-form resist mutator'ları + CHARDEF seed DOKUNULMADI (base'de
  kalır, script get/set simetrik). Stat (STR/DEX/INT) + MaxHits/Mana/Stam bonusları
  ERTELENDİ (feedback-loop riski: stat→MaxHits türetimi). Test: SourceXWave262Tests
  (+5: no-equip=base, sum, clamp, damage reduction, base-field/script salt-base).
  KALAN: (yok — RESFIREMAX Wave 265'te, HITCHANCE/LUCK Wave 267'de çözüldü, aşağı bak).
- [x] **AOS suit STR/DEX/INT agregasyonu (stat slice)** — YAPILDI (Wave 263,
  resist slice'ın (Wave 262) stat karşılığı; aynı live-scan-on-read deseni).
  `CombatEngine.EffectiveStr/Dex/Int(ch)` = base stat + `SumEquippedItemProperty`
  (BONUSSTR/BONUSDEX/BONUSINT), `Math.Max(0,...)` floor. Tüketiciler (bonus'u
  GÖRMELİ): 0x11 StatusFull paperdoll stat'ları, melee STR-damage (CalcWeaponDamage
  unarmed dmgMax + era0/1/2 STR% bonus'ları), carry weight (`Character.MaxWeight`
  effective STR'ye taşındı, MovementEngine overload + PacketHelpers maxWeight tek
  kaynağa dedup), CanEquip REQSTR gate, GetAdjustedSkill stat term. Base'de KALIR
  (feedback/persistence hazard): MaxHits/Mana/Stam türetimi (Str/Dex/Int setter
  max-pool sync), Login/SpawnComponents seed, stat-gain/stat-drain path'leri,
  script STR/DEX/INT get/set (simetrik base). MaxHits/Mana/Stam bonus'ları (BONUSHITS
  vb.) ERTELENDİ (max-pool türetim feedback'i). Test: SourceXWave263Tests (+9:
  no-equip=base, çok-parça sum, negatif clamp, base-field/script salt-base,
  max-pool değişmez, carry weight artışı, melee damage artışı, REQSTR gate,
  skill stat term). KALAN: BONUSHITS/MANA/STAM max-pool bonus'ları.
- [x] **AOS suit max-pool agregasyonu (BONUSHITSMAX/MANAMAX/STAMMAX)** — YAPILDI
  (Wave 264). Source-X tag adları BONUSHITSMAX/BONUSMANAMAX/BONUSSTAMMAX (BONUSHITS
  Source-X'te AYRI bir regen alanı, max değil — düzeltildi). Recon (Explore) kritik
  bulgular: (1) current hits/mana/stam SAKLANAN+mutable, stat slice'tan farklı;
  (2) Str/Dex/Int setter'ı _maxHits field'ını sync'ler (mevcut spell-buff mekanizması);
  (3) WorldSaver current'ı MAXHITS'ten ÖNCE, current save-order clamp'e tabi; (4)
  load-order: stat parse ÖNCE, equipment restore SONRA → over-base current load'da
  zaten base'e clamp'lenir (phantom persist YOK); (5) unequip clamp'i YOK (Source-X
  var: Stat_AddMaxMod). Karar: getter-override (base field vs effective read split,
  Source-X m_val+m_maxMod analojisi) — stat slice'ın helper-routing'inden farklı
  çünkü max-pool tüketicileri (display + heal ceiling + ratio) tutarlı olmalı, aksi
  halde `Hits>MaxHits` (120/100) edge'leri (negatif lostHits, >100% AI ratio).
  Uygulama: `CombatEngine.EffectiveMaxHits/Mana/Stam(ch)` = BaseMax + SumEquipped;
  MaxHits/Mana/Stam getter'ları effective döner (setter base yazar); yeni public
  `BaseMaxHits/Mana/Stam` (field); Hits/Mana/Stam setter cap'i effective (heal bonus
  havuza dolar); Unequip current'ı yeni effective max'e clamp'ler (Source-X parity);
  WorldSaver MAXHITS artık BaseMaxHits yazar (inflation guard — TEK persistence-yazan
  yer, recon doğruladı). Eski-save uyumu: getter 0'da taban (Login `<=0` backfill
  korunur, 1 DEĞİL). Over-base current save/load'da base'e clamp'lenir (dökümante
  minor loss, Str-reset de truncate eder — SphereNet Str↔MaxHits field coupling).
  Test: SourceXWave264Tests (+8: no-equip=base, çok-parça sum, heal bonus havuza
  dolar, base-field/script salt-base, unequip clamp, mana/stam paralel, stat/carry
  DOKUNULMAZ, negatif floor) + SaveFormatTests.Roundtrip_MaxPoolSuitBonus (base 100
  persist, effective 120 DEĞİL). Full suite 1701.
- [x] **HITCHANCE (HCI) + LUCK suit agregasyonu** — YAPILDI (Wave 267, recon ile
  DOĞRULANDI). Source-X ikisini de equip-time cache'e toplar (`ModPropNum` @Equip/
  @UnEquip, CCharAct.cpp:3398/3403); SphereNet live-scan ile eşdeğer. **HCI:** era-2
  to-hit okuması (`CombatEngine.cs:285`) `GetOnHitPropertyValue(attacker, null, ...)`
  kullanıyordu → yalnız char-tag + talisman, **armor/jewelry suit'i kaçırıyordu**
  (weapon=null olduğu için weapon'ı bile saymıyordu). Full-suit `GetEquipmentPropertyValue`
  (char tag + tüm layer'lar OneHanded..Horse, talisman dahil=9) ile değiştirildi; DCI de
  hedefte aynı. Cap 45 (Source-X `minimum(HCI,45)`, CResourceCalc.cpp:220) korundu, DCI
  ile simetrik. **LUCK:** yeni `CombatEngine.EffectiveLuck` = base + suit sum; status
  paketi (`GameClient.PacketHelpers.cs`) artık effective luck gönderiyor (eskiden ham
  `ch.Luck`). KRİTİK parity notu: Source-X Luck'ı **hiçbir yerde tüketmiyor** (loot/
  damage/spawn okumuyor; UNLUCKY bile Unimplemented) → SADECE display için toplandı,
  loot'a BAĞLANMADI (bağlamak sapma olurdu). Unclamped (UNLUCKY negatife iter, Source-X
  gibi). Base field/script getter/persistence DOKUNULMADI (Wave 262/263 invariant).
  Test: SourceXWave267Tests (+6: luck no-equip/sum/unlucky-negatif/base-salt, HCI+DCI
  equipped-armor era-2 regresyon).
- [x] **Per-element resist MAX cap (RESFIREMAX/…)** — DOĞRULANDI + PERSISTENCE FIX
  (Wave 265, recon). Premis ("resist cap eksik") YANLIŞ çerçevelenmiş: Source-X cap'i
  damage'da **ENFORCE ETMİYOR** — RES*MAX yalnızca saklama+display property'si (CCPropsChar
  ADDPROP + send.cpp SA-blok display; damage yolu CCharFight.cpp:724-728 base resist'i
  doğrudan okur, cap clamp'i YOK; 70 default'u da Source-X'te yok — SphereNet icadı). Yani
  cap'i enforce etmek = Source-X'ten SAPMA → KASITLI YAPILMADI (gameplay zaten parity:
  SphereNet de enforce etmiyor, 0-100 clamp). Status-paket display'i de eklenMEDİ:
  SphereNet 0x11 (`PacketStatusFull`) ClassicUO-uyumlu gerçek OSI layout'unu izler ve o
  layout'ta RES*MAX alanı yok; Source-X'in kendi SA-blok serileştirmesini (send.cpp:330-334)
  eklemek çalışan/tested paketi kaydırıp bozar, sıfır oyun değeri. GERÇEK boşluk (kapandı):
  `RESPHYSICALMAX` WorldSaver'da yazılmıyordu (diğer 4 yazılıyordu) → eklendi
  (WorldSaver.cs). Test: SaveFormatTests.Roundtrip_PreservesPerElementResistMaxCaps (+1,
  5 element round-trip). Eğer ileride SphereNet'e özgü FONKSİYONEL cap istenirse ayrı
  opt-in (Source-X-parity değil).

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
- [~] Stable native container serialization (`LAYER_STABLE`) — TAG modeli ZATEN
  FONKSİYONEL EŞDEĞER (triage Wave 243): pet'ler save/load'ı geçiyor, owner doğru,
  limitler uygulanıyor (VendorStableParityTests + GameSystemTests kanıtlı). Native
  container port'u sadece yapısal sadakat, davranışsal fayda yok → düşük değer,
  YAPILMADI. GERÇEK küçük boşluk KAPANDI (Wave 243): Source-X `CClientTarg.cpp`
  paketi boş olmayan peti stable'a almayı reddeder (item'lar snapshot'la kaybolurdu);
  `StableEngine.StablePet` artık `pet.Backpack.ContentCount > 0` ise reddediyor.
  Test: VendorStableParityTests.StablePet_RejectsPetWithNonEmptyPack. Kalan minor
  (ertelendi, düşük değer): limit formülü (base 5 + sum/600 vs Source-X eşik tablosu),
  claim-all vs claim-one, tam WorldSaver round-trip testi.
- [ ] Pet ekonomi sub-komutları (pet-sells-loot buy/sell/sample) mesaj-only.

### 3.6 Vendor / trade — 87
- [x] **NPC skill-eğitim-ücreti** — YAPILDI (Wave 215): `VendorTrainingEngine`
  (GetTrainMax=trainer skill% ∧ absolute cap ∧ student cap, CalcTrainableAmount
  sum-cap + DOWN-lock sacrifice, TrainSkill down-lock drain, TryPay proportional).
  Wire: "train/teach <skill>" speech → HandleTrainRequest quote+RememberOffer (NPC
  tag); gold-drop-on-NPC → VendorTrainingEngine.TryPay (ClientInventoryHandler, hire
  payment'ın yanında). NOT: SphereNet lock byte 0=up/**1=down**/2=locked (Source-X
  enum DEĞİL — client convention). Test: VendorTrainingEngineTests (+8).
- [x] Region `RestockVendors`/`NoRestock` tag + restock timer — DOĞRULANDI +
  BİRİM DÜZELTİLDİ (Wave 242): `NpcAI.ActVendor` zaten region `RESTOCKVENDORS`/
  `NORESTOCK` (region ∨ NPC) tag'lerini + wall-clock `RESTOCK_TIME` interval'i
  okuyordu. GERÇEK boşluk **birim uyuşmazlığı**: değer dakika olarak parse
  ediliyordu, oysa Source-X `NPC_Vendor_Restock` (CCharNPCAct_Vendor.cpp:55)
  `MSECS_PER_TENTH` = onda-bir-saniye kullanır (legacy script uyumu). Fix:
  `intervalMs = Clamp(tenths,1,…) * 100`. Böylece legacy `RESTOCKVENDORS=6000`
  = 600sn = 10dk (eskiden yanlışça 6000dk).
- [x] **Owned-vendor CASH (PC_CASH) + restock faucet fix** — YAPILDI (Wave 251,
  ekonomi-riskli grubun GÜVENLİ alt kümesi). Source-X `NPC_VendorGetChkVerb`
  PC_CASH sahibin vendor kasasını (`m_Check_Amount`) sahibine boşaltır. ÖNCE
  faucet bug'ı: `VendorEngine.RestockVendor` HER vendor'ın kasasını her restock'ta
  `RestockGold`(2000)'e dolduruyordu → sahipli (player/pet) vendor'da CASH =
  sonsuz-altın exploit'i. Fix: top-up artık `!vendor.OwnerSerial.IsValid` ile
  gate'li (sadece sahipsiz dükkâncı sonsuz alım-fonu alır; sahipli vendor SADECE
  gerçek kazanç biriktirir). Yeni `VendorEngine.DispenseVendorGold(vendor,owner)`
  kasayı sahibin sırt çantasına (`GiveGoldToPack`, ProcessSell'in altın-yığma
  mantığından dedup) döker + kasayı 0'lar. `ApplyPetVerb` (ClientItemUseHandler)
  `cash` case'i owner-gate (`CanAcceptPetCommandFrom`) + Vendor-brain kontrolü
  sonrası dispense. `bought/samples/stock/cash` `IsPetTargetVerb`'ten ÇIKARILDI
  (bogus target cursor'ı yoktu artık); `bought/samples/stock` dürüst mesaj (SphereNet
  vendor stock'u template-driven/virtual, owner-managed envanter DEĞİL → drag-out
  dup riski, kasıtlı ertelendi). Test: SourceXWave251Tests (+4: owned no-topup,
  unowned topup, dispense→owner+zero, empty-purse no-op).

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
- [~] Per-IP guest limit, account aging, max-conn-per-IP. **account aging YAPILDI**
  (Wave 250): `ACCOUNT UNUSED <days> [DELETE]` admin komutu (Source-X Cmd_ListUnused,
  CAccount.cpp:321) — AdminCommandProcessor'da, LastLogin/CreateDate ZATEN vardı;
  idle hesabı listeler/siler, staff (PLEVEL>Player) hiç yaşlandırılmaz, online hesap
  recent LastLogin ile eşleşmez. Manual/komut-tetikli (Source-X asla otomatik silmez).
  Test: SourceXWave250Tests (+2). ClientMaxIP ZATEN vardı (Wave triage doğruladı).
  KALAN (ertelendi, bağlantı-kabul path'i / DDoS-hardening): GuestsMax guest-pool
  (GUEST0..n login yolu yok), ConnectingMaxIP + MaxConnectRequestsPerIP (IP-history
  decay yapısı gerektirir).

### 4.4 Item type davranışları — 80
- [x] **Plant-growth sistemi** (`CItemPlant.cpp`) — YAPILDI (Wave 270, recon).
  KRİTİK kapsam: Source-X'te OSI plant-bowl minigame'i (seed→bowl→9 stage→health/water→
  pollination) YOK — `CItemPlant.cpp` bir **crop-id-chain regrow** sistemi (grep: `IT_PLANT`/
  `PlantGrowth`/`pollinat` = 0 eşleşme). Model: stage'ler ITEMDEF TDATA zinciriyle bağlı —
  TDATA1=reset-id, TDATA2=grow-into-id, TDATA3=fruit-id (m_ttCrops, CItemBase.h:184). Port:
  `Item.PlantOnTick` (CItemPlant.cpp:104) — timer tick'te crop bir stage ilerler; container'a
  girerse ölür; olgun stage (TDATA2 yok) TDATA3 fruit'i yere düşürüp TDATA1 stage-1'e reset +
  invis (regrow); invis stage sonraki tick'te tekrar görünür. `PlantCropReset` (CItemPlant.cpp:176),
  `PlantArmTimer` (MORE1 respawn-sec override, else 10dk default; Source-X lunar-cycle
  SADELEŞTİRİLDİ), `PlantStartGrowth`. Item.OnTick timerFired switch'ine Crops/Foliage case'i
  eklendi. Invis-regrow fazı MEVCUT attr modeliyle sadık: `ObjAttributes.Invis|Move_Never`
  (defs.scp `[DEFNAME attr_flags]` attr_invis=0x80/attr_move_never=0x10 ile birebir), HUE_RED_DARK
  staff marker; ClientViewUpdater zaten Invis'i respect ediyor. Mevcut harvest/seed (2.x) KORUNDU
  + entegre: `PlantSeed`→`PlantStartGrowth` (crop büyümeye başlar), `HarvestPlant` REAP_TIME 60s
  cooldown yerine `PlantCropReset` (Source-X Plant_Use faithful, invis-regrow). Test:
  SourceXWave270Tests (+4: chain advance, mature→fruit+reset+invis, invis→reappear, container→die).
  SADELEŞTİRME/ertelendi (düşük değer): fruit t_food olarak düşer (Source-X t_fruit), lunar-cycle
  interval fixed, pre-placed worldgen crop'lar timer olmadan armlenene kadar statik, fruit→seed
  bıçak dönüşümü (CClientTarg.cpp:1939).
- [x] CommCrystal messaging — YAPILDI (Wave 245). Triage: speech relay ZATEN çalışıyor
  (Program.EngineWiring.cs:701 OnItemHear → linked crystal'e BroadcastNearby). GERÇEK
  boşluk oyuncu **linkleme akışı**: crystal'e çift-tık artık target cursor açıyor
  (Source-X CItemCommCrystal CClientUse.cpp:417 + CClientTarg.cpp:1734), hedef
  CommCrystal olmalı → `item.Link = partner.Uid` + "Linked" (eskiden sadece "The
  crystal hums softly."). Relay dest-type doğrulaması eklendi (generic .LINK bir
  kristali kristal-olmayana bağlasa relay etmez). Test:
  GameSystemTests.GameClient_CommCrystalDoubleClick_LinksToTargetedCrystal.
  tooltip 1060742/3/5 (aktif/pasif/broadcast) — YAPILDI (Wave 250): SendAosTooltip
  item switch'ine `case ItemType.CommCrystal` (Source-X CClientMsg_AOSTooltip.cpp:696):
  link'li+link-de-crystal → 1060742 (active) yoksa 1060743 (inactive), her zaman + 1060745
  (broadcast). KALAN (düşük değer): per-crystal SPEECH bloğu — atlandı.

### 4.5 Character core — 82
- [x] `CCharStat` regen tabloları — YAPILDI (Wave 246, KULLANICI ONAYLADI "Source-X'e geç").
  Source-X CServerConfig m_iRegenRate: REGENx = **saniye**/puan (CServerConfig.cpp:1075
  ×MSECS_PER_SEC), STAT sırası REGEN0=STR/hits(40s), REGEN1=INT/mana(20s), REGEN2=DEX/
  stam(10s), REGEN3=food(60dk). ÜÇ bug düzeltildi: (1) BİRİM — SphereNet onda-bir-saniye
  (×100) okuyordu → ×1000 (RegenTenthsToMs→RegenSecondsToMs); default HP regen 4sn→40sn.
  (2) MAPPING — REGEN1/REGEN2 SWAP'liydi (Regen1→stam, Regen2→mana) → düzeltildi
  (Regen1→mana, Regen2→stam), mortechUO Source-X ini uyumu için kritik. (3) DEFAULT
  değerler Source-X per-pool'a (hits40/mana20/stam10). Ek: food decay 10dk→60dk; Human
  racial +2 HP regen (CCharStat.cpp:520, IsHuman). RegenSecondsToMs internal. ResetEngineStatics'e
  3 alan eklendi. Test: SourceXRegenWave246Tests (+3). Per-char m_regenRate override —
  YAPILDI (Wave 247): CChar REGENHITS/MANA/STAM/FOOD (+ D tenths varyantları) char
  property surface + persistence. CChar::Stats_GetRegenRate semantiği (CCharStat.cpp:586):
  per-char ms alanı 0=global rate, >0=per-char, <0=never regen. OnTick her stat için
  `ResolveRegenRateMs(perChar, global, fallback)` çözer (<0 → gate skip). Save tenths (D)
  olarak yazılır (loss-less roundtrip). RegenFoodSeconds static eklendi (config REGEN3).
  Test: SourceXWave247Tests +3, SaveFormat Roundtrip_PreservesPerCharRegenOverrides.
  m_regenVal (REGENVAL* — regen MİKTARI) override — YAPILDI (Wave 248): CChar
  REGENVALHITS/MANA/STAM/FOOD, per-event iyileşme miktarı (Stats_GetRegenVal →
  max(1, stored), CCharStat.cpp:609). OnTick her stat'ın taban miktarını override
  eder (>0 → val, else default; human +2 racial override ÜSTÜNE eklenir). Persist +
  get/set. Test: SourceXWave248Tests (+2).
- [x] **Region-jail makinesi** — YAPILDI (Wave 266). Recon: SphereNet'in zamanlı
  auto-release + reboot-safe + `@Jailed` trigger'ı ZATEN var (Source-X'te timer bile YOK —
  o eksen fazlasıyla mevcut). GERÇEK boşluk = hücre konumu **hardcoded** koordinattı
  (`(1476,1604,20)`), Source-X ise data-driven `[AREADEF]` `jail`/`jailN` bölgelerinden
  okur (`GetRegionPoint`, CServerConfig.cpp:2719). Eklenenler: `GameWorld.FindRegionByName`
  (NAME∨DEFNAME, Source-X `GetRegion` aynası) + `GameWorld.GetJailPoint(cell)` (`jail{cell}`
  ∨ `jail` region anchor'ı; region yoksa legacy koordinata fallback → jail AREADEF'siz
  shard çalışmaya devam eder). JAIL speech komutu artık `JAIL <uid> [dk] [cell]` — cell'i
  `JAIL_CELL` tag'ine yazar, `GetJailPoint` ile ışınlar. Login re-teleport da `JAIL_CELL`'den
  doğru numaralı hücreye (Source-X login'de `Jail()`'i JailCell tag'iyle yeniden çalıştırır).
  `.forgive`/`.pardon` release: eskiden object-verb `.forgive` yalnız ölü `JAIL`/`JAIL_EXPIRE`
  tag'lerini temizliyordu (gerçek release ETMİYORDU) — artık jailliyse `OnJailReleaseRequested`
  hook'unu çağırıyor (unfreeze+teleport+resync); ayrıca `FORGIVE`/`PARDON` speech komutları
  (UNJAIL ile ortak release path) eklendi. Release hook + speech release `JAIL_CELL`'i de
  temizler. Confinement = mevcut `StatFlag.Freeze` KORUNDU (recon: geçerli sadeleştirme;
  region-containment hareket kısıtı ertelendi — Freeze zaten hapsediyor, düşük değer/yüksek
  regresyon riski). Test: SourceXWave266Tests (+4: jail region anchor, numaralı hücre +
  base fallback, no-region legacy fallback, FindRegionByName NAME/DEFNAME).
- [x] STATF_INVUL townsfolk — YAPILDI (Wave 242): invul flag ölümde
  (`DeathEngine:71`) kontrol ediliyordu ama **hasar uygulamada değil** → invul
  karakter yine Hits kaybediyordu. Source-X `CChar::OnTakeDamage` (CCharFight.cpp)
  her darbeyi geri sektirir (return 0) — DAMAGE_GOD hariç. SphereNet'te tek choke
  yok, o yüzden ortak `CombatEngine.IsDamageImmune(target, type)` helper'ı
  (`IsStatFlag(Invul) && !type.HasFlag(God)`) tüm hasar noktalarına bağlandı:
  ApplyScriptDamage, melee (reflect dahil), SpellEngine, container/step trap'leri
  (ClientItemUseHandler×2, MovementEngine×2), field hasarı. Test:
  SourceXInvulRezWave242Tests (+3).

### 4.6 Death / corpse — 82
- [x] `IsCorpseResurrectable` healer-rez yolu — DOĞRULANDI + TOP-LEVEL GATE
  EKLENDİ (Wave 242): player Healing/Vet corpse-rez (`ActiveSkillEngine.Healing`)
  ve NPC healer ghost-rez (`NpcAI.ActHealer`) zaten mevcut, `Character.Resurrect`
  antimagic/HP/murderer-penalty içeriyor, `RestoreFromCorpse`=`RaiseCorpse`. GERÇEK
  boşluk: Source-X `CItemCorpse::IsCorpseResurrectable` (CItemCorpse.cpp:63-67)
  corpse'un **top-level** (bir kabın içinde değil) olmasını şart koşar. Eklendi:
  heal-rez yolu artık `corpse.ContainedIn.IsValid` ise `HealingCorpseg` ile
  reddediyor (self-rez `RestoreFromCorpse:675` zaten uyguluyordu). Test:
  SourceXInvulRezWave242Tests.Healing_CorpseInsideContainer_RejectsAsNotTopLevel.
- [x] Resurrection-via-gump menü + corpse instalist packet — N/A (Wave 242):
  Source-X'te resurrection her zaman doğrudan aksiyon (`Spell_Resurrection`), gump
  DEĞİL; `RaiseCorpse` corpse'u siler, "instalist" içerik-listesi paketi yok.
  Upstream'de karşılığı olmayan madde — uygulanabilir bir boşluk değil.
- [ ] `IsCorpseSleeping` — SphereNet'te sleeping mekaniği yok; rez akışının parçası
  DEĞİL (Source-X'te Forensics/steal callers). Kapsam dışı.

### 4.7 Item core — 85
- [x] **Efektif-skill katmanı (Skill_GetAdjusted SkillMod terimi)** — YAPILDI
  (Wave 252, "büyük mimari" grubu — recon ile RİSK ÇÖKERTİLDİ). Source-X recon'u
  gösterdi ki korkulan HOT-PATH yeniden yazımı GEREKMİYOR: Source-X'te combat/
  crafting/skill-gain zaten `Skill_GetBase` (ham base) çağırır; equipment bonusu
  SADECE `Skill_GetAdjusted`'ın okuduğu `SkillMod<n>` char key'inden gelir
  (CCharSkill.cpp:171-174, script-maintained @Equip/@UnEquip). SphereNet'te
  `GetSkill`=Skill_GetBase (ham) DOKUNULMADI; `SkillEngine.GetAdjustedSkill` ZATEN
  vardı (base + BONUS_STATS stat-katkısı) ama SkillMod terimi eksikti. Fix:
  `GetSkillModBonus(ch,skill)` = `TryGetTag("SkillMod<n>")` (signed) eklendi,
  GetAdjustedSkill'e `+= SkillMod<n>` (final clamp≥0). Consumer: `SendSkillList`
  (0x3A) artık value=adjusted, rawValue=base gönderiyor (Source-X send.cpp:1146
  Skill_GetAdjusted-as-value); skill penceresinde equipment bonusu görünür. Legacy
  uyum: Character get/set'e `SKILLMOD<n>` property case'i (bare key, `SRC.SkillMod5=100`
  → tag; 0 → temizle) — hem bare hem `TAG.` yolu çalışır, tag olarak otomatik persist.
  Combat/crafting/skill-gain/cap DEĞİŞMEDİ (GetSkill/ResolveSkillCap ham base'de
  kalır, Source-X `Skill_GetMax` base'e clamp eder). SkillMod default 0 → mevcut
  içerikte SIFIR davranış değişimi (opt-in). Test: SourceXWave252Tests (+5).
  KALAN (ertelendi): first-class BONUSSKILL1-5/AMT item property parse'ı + @Equip
  otomatik SkillMod maintenance (Source-X'te bile script-driven, ayrı iş); AOS
  suit stat/resist/hitchance agregasyonu (Stat_AddMod/ModPropNum equip-time cache,
  ayrı dalga — hakiki hot-path, CCharAct.cpp:3382/514).
- [x] **BASEWEIGHT** (per-item weight override) — YAPILDI (Wave 249): Source-X
  CItem::m_weight (birim-başına onda-bir-stone, CItem.cpp:2777). Item.Weight getter'a
  nullable `_weightOverride` eklendi (set → tüm ağırlık hesabını sürer: TotalWeight/
  CanCarry); GET raw tenths döner (WEIGHT case tam-stone'a böler). Persist. Test:
  SaveFormat Roundtrip_PreservesItemBaseWeightOverride + SourceXWave249Tests.
- [x] **MAXAMOUNT** — YAPILDI (Wave 250). Item.IsStackable helper'ı CanStackWith'ten
  extract edildi (CItemBase::IsStackableType: CAN_I_PILE veya tiledata Generic);
  Item.MaxAmount getter'ı `IsStackable ? (override ?? ItemsMaxAmount) : 0` (Source-X
  GetMaxAmount, CItemBase.cpp:127). Static Item.ItemsMaxAmount config'den wire'landı
  (SphereConfig.ItemsMaxAmount ZATEN vardı=60000 ama unused; Program.cs bağladı,
  ResetEngineStatics reset). Per-item _maxAmountOverride (GET/SET MAXAMOUNT, persist).
  İki stacking site'ı (Item.TryAddItemWithStack, ClientInventoryHandler:1198) artık
  ushort.MaxValue yerine existing.MaxAmount ile cap'liyor (60000 default). Test:
  SourceXWave250Tests (+3: default/non-stackable, override, merge enforcement).

### 4.8 Ships — 85 (T0.1 dışındaki kalanlar)
- [x] Multi-tick hız ölçekleme — YAPILDI (Wave 243). Source-X `CCMultiMovable::
  SetNextMove` (CCMultiMovable.cpp:119/124): slow (one-tile) mod tam period'da,
  continuous ("normal") sailing fast modda **interval'i yarıya indirir** →
  sürekli yelken tık-tık'tan 2× hızlı. SphereNet `SpeedMode` ölü state'ti (hiç
  set/read edilmiyordu) ve tüm modlar düz `SpeedPeriod` kullanıyordu → normal
  sailing slow ile aynı hızdaydı. Fix: `ShipEngine.SetMoveDir` moveType==Normal→
  Fast / OneTile→Slow set eder; ortak `GetMoveDelay` (Slow→period, else→period/2)
  hem SetMoveDir hem OnShipTick'te. Tiles-per-tick zaten doğruydu (dokunulmadı).
  ShipMovementType enum'u zaten Source-X SMT_* ile hizalı (Stop/OneTile=SLOW/
  Normal). Test: SourceXShipSpeedWave243Tests (+2). NOT: SHIPSPEED script birim
  dönüşümü (Source-X tenths vs SphereNet ham ms) ayrı — ship .scp değerleri
  doğrulanmadan dokunulmadı.

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
- [~] `CMenu` paging helper'ları + standart menü builder'ları — KISMEN (Wave 247).
  "Paging" premisi YANLIŞ: Source-X eski-stil menü (0x7C) MAX_MENU_ITEMS=64 ile flat/
  sayfasız, paging yok (ikisi de truncate). 0x7C/0x7D pair + genel MENU/SKILLMENU/GM
  builder'ları ZATEN var. İki gerçek delta düzeltildi: (1) TESTIF= gating no-op stub'dı
  (entry hep görünür) → ScriptInterpreter.EvaluateConditionForTarget (private
  EvaluateConditionWithResolver public wrapper) ile değerlendiriliyor, false → gizle
  (Source-X CClientUse TESTIF). (2) >255 entry PacketMenuDisplay.Build'de count byte'ı
  clamp ediyordu ama TÜM listeyi yazıyordu → client read cursor desync; artık sadece
  count kadar yazılır + builder'lar MAX_MENU_ITEMS-1=63'te truncate. Test: SourceXWave247
  Tests (+2). KALAN (niş, ertelendi): nested SKILLMENU recursion, MAKEITEM menü-build filter.

### 5.3 Incoming packet — 78
- [~] 0xBF/0xD7 extended subcommand allow-list — BÜYÜK KISIM YAPILDI (Wave 243-244).
  **Guild/Quest button transport düzeltmesi (Wave 244)**: Source-X'te bu ikisi
  `0xD7 0x28`/`0xD7 0x32` (EXTAOS uzayı, PacketGuildButton/QuestButton
  receive.cpp:4161/4194, CPacketManager registerEncoded) — SphereNet 0xBF'te
  yanlış transport'taydı, gerçek client'tan HİÇ ateşlenmiyordu. Fix: GameClient.
  HandleEncodedCommand'a 0x28→@UserGuildButton, 0x32→@UserQuestButton (0x19
  special-move gibi design-gate öncesi) eklendi; yanlış 0xBF 0x28 kaldırıldı, 0xBF
  0x32 GargoyleFly'a repoint edildi (aşağı). GameSystemTests guild/quest artık 0xD7
  enjekte ediyor. **GargoyleFly (Wave 244)**: 0xBF 0x32 (Source-X EXTDATA_GargoyleFly,
  receive.cpp:3329) — canlı gargoyle + RACIALF_GARG_FLY racial → STATF_HOVERING +
  BI_GARGOYLEFLY buff toggle; uçuş durumu MobileFlags 0x04 biti ile self+observer'a
  yansır (BuildMobileFlags'e Hovering→0x04 wire edildi, eskiden ölü biteti). Test:
  GameSystemTests.GameClient_GargoyleFly_TogglesHoveringAndBuff + NonGargoyle_NoOp.
  KALAN — TAMAMLANDI (Wave 248 + 265): `0xBF 0x1C` NewSpellSelect, `0xD7 0x1E`
  EquipLastWeapon, `0xBF 0x2E` TargetedSkill (Wave 248, aşağı); `0xBF 0x2C`
  BandageMacro + virtue repoint (Wave 265, aşağı).
- [x] **EquipLastWeapon (0xD7 0x1E) + TargetedSkill (0xBF 0x2E) + spell-select repoint
  (0xBF 0x1C)** — YAPILDI (Wave 248). (1) EquipLastWeapon: Character.LastWeaponUid
  (runtime, Source-X m_uidWeaponLast, Equip'te set — CCharAct.cpp:314); GameClient.
  HandleEncodedCommand 0x1E design-gate öncesi ItemPickup-then-Equip (pack'ten çıkar,
  HandleItemEquip guard'ları). (2) TargetedSkill: ClientSkillsHandler.BeginTargetedSkill
  (cursor'suz, @SkillPreStart/Start zinciri + ResolveActiveSkillTarget extract);
  HandleTargetedSkill CanUse gate'leri, skillId 0=last-skill desteklenmez. (3) 0xBF 0x1C
  ESKİDEN viewport-size'a map'liydi ama gerçek client (ClassicUO >= 6.0.1.42) burayı
  SPELL-CAST için kullanır (Send_CastSpell, [0x1C][0x02][spell]; viewport 0x05/0xC8'de)
  → HandleSpellSelect'e repoint (protokol-doğru bug fix, eski handler dead-code'du).
  Item.IsWeaponType eklendi. Test: SourceXWave248Tests (+4). DeferredParity viewport
  testinin 0x1C bölümü kaldırıldı (artık spell-cast).
- [x] WrestleDisarm/Stun — YAPILDI (Wave 243): `0xBF 0x09`/`0x0A` (Source-X
  EXTDATA_Wrestle_DisArm/Stun, boş slot'lardı) eklendi → `Event_CombatAbilitySelect`
  gibi `@UserSpecialMove` trigger'ını N1=0x05 (disarm)/0x0B (paralyzing blow) ile
  ateşler (0xD7 0x19 special-move ile aynı sink). ExtendedCommandRegistry'ye
  0x0009/0x000A eklendi (drift guard). Test:
  GameSystemTests.GameClient_WrestleMacros_FireSpecialMoveTrigger.
- [x] GargoyleFly (0xBF 0x32) — YAPILDI (Wave 244, yukarı bak).
- [x] **BandageMacro (0xBF 0x2C) + virtue repoint (0x12/0xF4)** — YAPILDI (Wave 265,
  recon ile transport DOĞRULANDI). SphereNet 0xBF 0x2C'yi YANLIŞ olarak virtue'a
  map'lemişti; Source-X'te 0x2C = **EXTDATA_BandageMacro** (`sphereproto.h:266`,
  `receive.cpp:3196`): `[dword bandageUID][dword targetUID]` → bandage'ı dclick edip
  cursor'suz hedefe uygular. Yeni `HandleBandageMacro` (ClientWorldFeaturesHandler):
  UID'leri parse eder, item `ItemType.Bandage` mi + not-busy + `CanUse(Healing)` gate'ler
  (Source-X `IsType(IT_BANDAGE)` aynası), sonra `BeginTargetedSkill(Healing, target)`
  (0xBF 0x2E TargetedSkill deseni; Healing pipeline hedefi doğrular + pack'ten bandage
  tüketir). Virtue gerçekte **EXTCMD_INVOKE_VIRTUE=0xF4**, 0x12 text-command paketinde
  (`CClientEvent.cpp:3127`); `OnTextCommand` case 0xF4'e taşındı — mevcut SphereNet-içi
  "SKILLLOCK" funnel'ı (`LoginPackets` 0x3A→0xF4 re-emit) ile prefix-branch ile uzlaştı:
  `SKILLLOCK ...` → skill lock, tek virtue digit'i → `HandleVirtueInvoke` (@UserVirtueInvoke,
  N1=virtueId, Source-X m_iN1=iVirtueID). ExtendedCommandRegistry 0x2C etiketi düzeltildi
  (drift guard korunur). Test: SourceXWave265Tests (+3: bandage→Healing cursor'suz,
  non-bandage no-op, virtue N1) + GameSystemTests virtue 0x2C→HandleVirtueInvoke repoint.
  NOT: virtue-gump select (@UserVirtue / Event_VirtueSelect, 0xB1 dialog) AYRI path,
  kapsam dışı.

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
- [x] **Built-in sysgump kütüphanesi** — PREMİS YANLIŞ / DOĞRULANDI + 0xB0 fallback
  (Wave 269, recon). "Source-X hardcoded C++ sysgump kütüphanesi shipler, SphereNet script'e
  dayanıyor" premisi YANLIŞ: Source-X'in `d_*`/prop/travel/help/admin dialog'ları da SCRIPT
  pack'ten (`RES_DIALOG`, CClientDialog.cpp:15) gelir — SphereNet gibi (`bin/data/scripts/
  core/dialogs/`, 52 dosya/187 [DIALOG]). SphereNet'te CDialogDef-eşdeğeri tam layout builder
  (`GumpBuilder.cs`, tüm GUMPCTL_*), compressed send (0xDD) + response routing (0xB1/0xAC),
  script-[DIALOG] render (subject-binding dahil), VE Source-X'in C++'ta kurduğu HER
  dedicated-packet system gump'ı (skill/status/paperdoll/spellbook/bank/vendor/book/bboard/
  map/menu/trade/value-input/party/healthbar/house-design) ZATEN var. Tek gerçek delta:
  Source-X'in çok-eski (pre-3.0) client'lara gönderdiği **uncompressed 0xB0** gump
  (send.cpp:3713); SphereNet hep 0xDD gönderiyordu. Eklendi: `PacketGumpDialogStandard`
  (0xB0, plain-ASCII layout + Unicode text), `SendGump` version'a göre dallanır — SADECE
  `ver != 0 && ver < 3.0.0` client'lar 0xB0 alır (modern + tespit-edilmemiş 0xDD'de kalır,
  mevcut yol dokunulmaz). Test: SourceXWave269Tests (+1, 0xB0 byte-yapısı). Düşük değer
  (her modern/Sphere-era client 0xDD destekler) ama parity-complete.

### 5.6 Login flow — 85
- [x] **Server-list multi-server** — YAPILDI (Wave 268). SphereNet 0xA8'i hardcoded tek
  `"SphereNet"/127.0.0.1` gönderiyordu ve config'i (ServName/ServIP) YOK SAYIYORDU. Source-X
  send.cpp:3289 = self ilk, sonra config `[SERVERS]` shard'ları, cap 32. Uygulama:
  `PacketServerList` artık `IReadOnlyList<ServerListEntry>` alıp N entry (sıralı index) yazar,
  32'de cap'ler; `SphereConfig.ServerList` (SERVERLIST ini key: `name,ip,port;...` parse) +
  `GameClient.ServerListProvider` hook'u (host wire eder) config-driven self + extra shard'ları
  kurar; `ResolveAdvertisedIp` self/relay için ortak (0.0.0.0→local-endpoint→127.0.0.1,
  Source-X local-addr subst); `OnServerSelect` artık list index'ini onurlandırıp doğru shard'ın
  ip/port'una relay eder. Test: SourceXWave268Tests (+4: N-entry+index, 32-cap, SERVERLIST
  parse, boş-liste). NOT (ertelendi, gerekçeli): **KR/EC 0xFFFFFFFF seed edge** — Source-X
  `seed==0xFFFFFFFF && kalan-yok ⇒ KR encryption gönder+bekle` (CNetworkInput.cpp:645) SphereNet'te
  yok (KR client stall olur). Outgoing KR-crypto handshake paketi gerektiriyor ve **canlı KR/EC
  client olmadan doğrulanamaz** (crypto blast-radius yüksek) → güvenli-scope dışı, KR client
  temin edilince eklenir. Tek-process shard'da multi-entry list zaten düşük değer (recon),
  gerçek kazanım config-driven self entry + honor-index.

### 5.7 Persistence — 86
- [x] `WORLDMULTI` — GEÇERSİZ/DOĞRULANDI (Wave 245 triage): premis YANLIŞ. Source-X'te
  `WORLDMULTI` section YOK — multi'ler (ev/gemi/custom) `CItemMulti::r_Write` ile
  `WORLDITEM` + ekstra key'ler olarak yazılır (CItem.cpp:2449). SphereNet de aynı:
  WORLDITEM + HOUSE.*/SHIP.*/DESIGN_* tag'leri, tam house/ship state round-trip'li
  (owner/access/lockdown/secure/component/custom-design/decay). Veri kaybı YOK.
  WORLDMULTI yazıcı eklemek Source-X'ten SAPMA olurdu → kapatıldı.
- [x] GMPAGE persistence — YAPILDI (Wave 245). GERÇEK boşluktu: GM page kuyruğu
  Program._scriptGmPages'te in-memory'di, restart'ta ölüyordu + import edilen classic
  GMPAGE section'ları ResourceHolder'da skip ediliyordu (data-loss). Fix: kuyruk
  `GameWorld.GmPages` (GmPageRecord)'e taşındı (Source-X g_World.m_GMPages gibi world'e
  ait); WorldSaver spheredata.scp'ye `[GMPAGE n]` section-per-page yazıyor, WorldLoader
  okuyor; Program SERV.GMPAGES/_GMPAGE artık world'e delege. Test:
  SaveFormatTests.Roundtrip_PreservesGmPages.
- [x] Guild persistence — DOĞRULANDI + comma-fix (Wave 245): tam round-trip (üye/rütbe/
  title/houses/ships/war/ally) ZATEN vardı ("ince" yanlış nitelendirme). GERÇEK
  integrity edge: GUILD.MEMBERS tek tag'de virgülle paketleniyor, title'daki `:`
  escape'liydi ama `,` (kayıt ayracı) DEĞİLDİ → title'da virgül olan üye load'da
  split'i bozup sonraki üyeleri düşürüyordu. Fix: title'da `,`→`\m` escape (symmetric,
  `:`→`\c` gibi). Test: GeneralGameplayIntegrityTests.GuildMemberTitleWithComma_Survives.

### 5.8 Encryption — 88
- [ ] `CCryptoKeyCalc` client-version key-table auto-detect (şu an client key'leri
  dışarıdan).
- [!] Account bcrypt (`CBCrypt`) — TRIAGE EDİLDİ (Wave 245), HARİCİ BAĞIMLILIK GEREKİR.
  Account-storage ZATEN Source-X core ile eşdeğer: Source-X core CheckPassword sadece
  plain+MD5 (CAccount.cpp:936), bcrypt DEĞİL — SphereNet PasswordHelper plain+MD5+SHA256
  (bonus). Gerçek gap sadece scripting fn'leri BCRYPTHASH/BCRYPTVALIDATE (CScriptObj.cpp:
  1096). AMA .NET'te yerleşik bcrypt YOK → harici NuGet (BCrypt.Net-Next) gerekir; proje
  hiç bcrypt paketi referanslamıyor. Legacy mortechUO account'ları plain/32-hex-MD5
  (60-char $2*$ ile çakışmaz, güvenli). KULLANICI: bcrypt NuGet ekleyelim mi?
