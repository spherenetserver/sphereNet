# SphereNet İnceleme Doğrulama ve Uygulama Planı

Bu dosya, üç inceleme raporunun (`PROJE_GENEL_INCELEME_PLANI_TR.md`,
`HOUSE_SHIP_DEED_SISTEM_INCELEMESI_TR.md`, `PERFORMANS_LOG_INCELEMESI_TR.md`) her
somut iddiasının **güncel kodla doğrulanmış** halidir. Her madde 3 paralel salt-kod
doğrulama ajanıyla GERÇEK / BAYAT / YANLIŞ / KISMİ olarak sınıflandırıldı; sadece
**GERÇEK** çıkanlar aşağıda aksiyon maddesi olarak listeleniyor.

Sözleşme: bir madde bitince `[x]` işaretle + `(YAPILDI: <kısa kanıt / test>)` ekle.
Line numaraları doğrulama anındaki (2026-07-18) güncel koda göredir; koda dokunulunca
kayabilir — sembol adına güven, satıra değil.

Sonuç: HOUSE_SHIP ve PERFORMANS raporlarındaki **her** kod iddiası GERÇEK çıktı.
PROJE_GENEL'de 1 madde BAYAT (tarım, Wave 270'te düzeltilmiş), 1 madde KISMİ.

---

## ELENEN MADDELER (aksiyon YOK)

- **BAYAT — Tarım büyüme döngüsü.** Rapor "PlantSeed son Crops'u doğrudan kuruyor,
  HarvestPlant 60s REAP_TIME, büyüme evresi yok" diyordu; **Wave 270'te tam büyüme
  zinciri eklendi** (`Item.PlantOnTick/PlantCropReset/PlantStartGrowth/PlantDropFruit`,
  `ClientItemUseHandler.PlantSeed`→`PlantStartGrowth`, `HarvestPlant`→`PlantCropReset`,
  `REAP_TIME` kaldırıldı). Aksiyon yok. (Sulama/toprak hâlâ yok ama Source-X'te de yok.)
- **KISMİ — MapViewSize.** Network view range config-driven değil (client 0xC8, clamp
  4-24), AMA `MapViewSize` combat/witness radar için tüketiliyor (`Character.MapViewRadarTiles`,
  `Program.cs:689`). Tamamen kullanılmıyor değil → sadece "view range config'e bağlı değil"
  kısmı P2 olarak C-grubuna eklendi.

---

## A. Başlangıç bağlantı hataları (P0/P1 — sessiz runtime bug, testlerin yakalamadığı)

Ortak sebep: `Program.EngineWiring` çok uzun ve sıraya duyarlı; motorlar oluşturulmadan
önce bağlanıyor. Öneri: tüm nesneler kurulduktan sonra tek "finalize/wiring" aşaması +
zorunlu bağlantılar için fail-fast doğrulama.

- [x] **A1 (P1) — MovementEngine.SpellEngine null bağlanıyor.** (YAPILDI: atama SpellEngine
  oluşturulduktan sonraya taşındı, EngineWiring.) `Program.EngineWiring.cs:472`
  `_movement.SpellEngine = _spellEngine;` ama `_spellEngine` ancak `:1195`'te oluşturuluyor
  (`Program.cs:92` `null!`). `MovementEngine.cs:150` `SpellEngine?.TryInterruptFromMovement`
  null-conditional → **hareket ederek cast-interrupt gerçek sunucuda hiç çalışmıyor.** Fix:
  atamayı SpellEngine oluşturulduktan sonraya taşı.
- [x] **A2 (P1) — NpcAI gece ışığı bağlantısı komple atlanıyor.** (YAPILDI: `GetLightLevel`
  ataması WeatherEngine oluşturulduktan sonraya taşındı, koşulsuz.) `Program.EngineWiring.cs:1716`
  `if (_weatherEngine != null) _npcAI.GetLightLevel = ...` ama `_weatherEngine` `:2040`'ta
  kuruluyor → guard false → atama **hiç yapılmıyor**. `NpcAI.cs:370` `GetLightLevel?.Invoke()
  ?? 0` → hep 0 → **NPC'ler her zaman gündüz varsayıyor** (gece ışık yak/söndür çalışmıyor).
  Fix: WeatherEngine oluşturulduktan sonra bağla.
- [x] **A3 (P1) — `.SHUTDOWN` / `.BROADCAST` no-op.** (YAPILDI: `_commands.OnShutdownCommand`
  → main-loop `_running=false`, `_commands.OnBroadcastCommand` → tüm in-world client'lara
  SysMessage. NOT: event'ler `CommandHandler`'da, SpeechEngine'de değil.) `SpeechEngine.cs:1127/1137` event
  invoke ediyor ama `OnShutdownCommand`/`OnBroadcastCommand`'a **hiçbir yerde `+=` yok**
  (sadece `OnSaveCommand`/`OnResyncCommand` wire'lı). Komut Admin/GM'de kabul edilip sessizce
  hiçbir şey yapmıyor — operasyonda yanıltıcı. Fix: server-side handler bağla.
- [x] **A4 (P1) — Normal shutdown'da save yok.** (YAPILDI: shutdown bloğu `SaveOnShutdown`
  config'ine göre `PerformSave()` yapıyor, default açık, try/catch. Test:
  ConfigRegressionTests.SphereConfig_SaveOnShutdown_DefaultsOnAndParses.) `Program.Tick.cs:261` "Auto-save on shutdown
  is disabled" logluyor; shutdown bloğu (`:257-283`) save yapmıyor. Planlı kapatmada son
  periyodik save'den sonraki değişiklikler kaybolabilir. Fix: güvenli-varsayılan-açık
  configlenebilir shutdown-save.
- [ ] **A5 (P0-fixture) — Composition-root entegrasyon testi yok.** Testler `EngineWiring`'i
  elle taklit ettiği için A1/A2 gibi sıra hataları yakalanmıyor. Fix: port açmadan gerçek
  motor grafiğini kuran fixture + zorunlu-bağlantı fail-fast doğrulaması.

---

## B. House / Ship / Deed sistemi (P0 bloklayıcı + P1)

- [x] **B1 (P0 — BLOKLAYICI) — MultiReader sadece 12-byte okuyor, format tespiti yok.**
  (YAPILDI: `MultiReader` artık 12/16-byte'ı auto-detect ediyor — index blok uzunluklarının
  strict-divisibility oyu (608%16=0/608%12=8 → HS), tie'da offset-plausibility tiebreak; HS'de
  trailing `ShipAccess` dword okunuyor (`MultiComponent.ShipAccess` eklendi). `ComponentSize`
  property expose edildi. Test: MultiReaderTests (+3: HS16 detect+bounds+shipAccess, orig12
  detect, ambiguous→plausibility→HS).)
  `MultiReader.cs:14` `ComponentSize = 12`, `:60` `dataLength / 12`, `:64` 12-byte read;
  hiçbir 16-byte/High Seas tespiti yok. Source-X iki formatı da modelliyor + auto-detect
  ediyor (`CUOMultiItemRec.h:28/39`, `CUOInstall.cpp:115`, `CServerMap.cpp:618`). 16-byte HS
  veride component offset'leri kayıyor → `MaxY` şişiyor → `PlaceHouse` (`HousingEngine.cs:567`)
  / `PlaceShip` (`ShipEngine.cs:90`) harita-sınırı kontrolünde **terrain'e bakmadan red** →
  "Cannot place here". Fix: `MultiFormat` (Auto/Original12/HighSeas16) + çok-kayıtlı sağlam
  auto-detect (tiledata boyutu + `%12`/`%16` + bounds plausibility) + `shipAccess` dword.
- [x] **B2 (P1) — `ID=i_deed` type inheritance yok.** (YAPILDI: `ResolveDupeItemInheritance`
  artık DisplayIdRef (`ID=<defname>`) için de child Type==Normal ise base TYPE'ı devralıyor —
  Source-X IBC_ID'nin typed base'i dupe etmesinin (IsDupedItem/DUPELIST) aynası. TYPE-only
  (grafik-only ID= referansı Layer/TData sürüklemesin). Source-X `CItemBase.cpp:1659` +
  aktif pack `i_deed_stone_and_plaster_house` (ID=i_deed, TYPE'sız) doğrulandı. Test:
  SourceXDeedInheritanceTests (inherit + no-over-inherit). Tam suite 1836.) `DefinitionLoader.cs:433-457` ID/DISPID
  ref zinciri sadece `DisplayIdRef`/`DispIndex` set ediyor, `Type`'ı kopyalamıyor
  (`ItemDefHelper.cs:51`, `ItemDef.cs:13` default Normal). DUPEITEM'de type inheritance VAR
  (`:925-953`) ama ID= zincirinde yok → scriptten üretilen deed `ItemType.Normal` kalıp deed
  handler'a hiç girmeyebilir. Fix: ID= zincirinde Type/TDATA/Layer devral.
- [~] **B3 (P1) — `[MULTIDEF]` metadata placement registry'ye merge edilmiyor.** (KISMEN
  YAPILDI: `MultiRegistry.MergeScriptMetadata` eklendi — MULTIDEF resource'larının StoredKeys'ini
  paylaşılan multi id ile geometriye overlay ediyor (NAME/TYPE/BaseStorage/BaseVendors);
  `MultiDef`'e bu alanlar eklendi; EngineWiring LoadFromMapData sonrası çağırıyor; PlaceHouse
  artık `def.BaseStorage`'ı house'a uyguluyor (B10'un storage kısmı da kapandı — 400 default
  yerine script değeri). Test: MultiRegistryMetadataTests (+2: merge, no-geometry skip). KALAN
  (büyük, Aşama 4 ile ortak): COMPONENT (dinamik fixture: kapı/sign/tillerman/plank world-item
  üretimi), MULTIREGION/REGIONFLAGS (script region), TSPEECH — bunlar ayrı geometry/fixture
  ayrımı işi.)
  `MultiRegistry.LoadFromMapData` (`HousingEngine.cs:470`) sadece geometri okuyor;
  `EngineWiring.cs:2308` MULTIDEF'i birleştirmiyor → type/name/component/storage/vendor/
  ship-speed placement'ta kayboluyor. (KISMİ: MULTIDEF script resource olarak TSPEECH/
  COMPONENTCOUNT için okunuyor.) Fix: raw multi ID altında binary geometri + script metadata
  merge.
- [x] **B4 (P1) — Yapısal placement result yok.** (YAPILDI: `PlacementFailure` enum
  (PlayerLimit/AccountLimit/MultiMissing/OutOfMap/LocationBlocked/ScriptVeto); PlaceHouse/
  PlaceShip `out failure` overload'u eklendi (eski 3-arg imza korundu → testler kırılmadı),
  her `return null` bir neden set ediyor; deed handler `PlacementFailureMessage` ile neden-özel
  mesaj gösteriyor. Test: SourceXPlacementResultTests. KALAN (ince): su/eğim/overlap ayrımı
  LocationBlocked altında toplu — CanPlace* içinde ayrıştırmak ayrı iş.) House/Ship motorları hep `null` dönüyor
  (`HousingEngine.cs:559-578`, `ShipEngine.cs:77-95`) → tek genel "Cannot place"
  (`ClientItemUseHandler.cs:1219`). Fix: neden-enum'u (limit/format/su/zemin/overlap/LOS/...)
  + ayrı oyuncu mesajı + structured log.
- [x] **B5 (P1) — WorldSaver item TYPE yazmıyor; house/ship raw BaseId'den kurulamaz.**
  (YAPILDI: WorldSaver artık structure item'lar (Multi/MultiCustom/Ship) için numeric `TYPE`
  yazıyor — BaseId raw multi index'i ITEMDEF'siz olduğu için loader def'ten türetemiyordu →
  restart'ta t_normal olup Housing/Ship DeserializeFromWorld bulamıyordu. Loader zaten `TYPE`
  restore ediyor (`case "TYPE"`) + `MaterializeDefinitionType` restore edileni ezmez (`_type==
  Normal` gate). Gerçek `WorldSaver→dosya→WorldLoader` roundtrip test'i eklendi. Test:
  SaveFormatTests.Roundtrip_PreservesMultiStructureType (Multi+Ship). Geriye-uyum: eski save
  TYPE'sız → MaterializeDefinitionType/legacy yolu korunur.)
  `WorldSaver.cs:535-593` ID/TDATA/MORE yazıyor ama instance `ItemType` (Multi/MultiCustom/
  Ship) yok; load'da raw index (`0x64`) normal ITEMDEF sanılabilir. Gerçek `WorldSaver→dosya→
  WorldLoader` roundtrip testi yok (mevcut testler aynı canlı world'de re-read). Fix: STRUCTURE.
  KIND/MULTIID/MULTIDEF persist + gerçek process-boundary roundtrip testi.
- [x] **B6 (P1) — Raw multi ID `0` redeed'de reddediliyor.** (YAPILDI: `TryParseDeedMultiId`'ye
  `allowZero` parametresi eklendi; `SHIP_MULTI_BASEID` branch'i `allowZero: true` geçiyor —
  explicit ship tag'i 0'ı (small ship north raw index) meşru kabul ediyor, dry-dock deed'i tekrar
  açılabiliyor. Ambiguous More1/BaseId fallback hâlâ 0'ı reddediyor.) `TryParseDeedMultiId`
  (`ClientItemUseHandler.cs:3028` `id != 0`) + fallback (`:3013` `targetId==0` fail) →
  dry-dock'tan üretilen classic small ship deed'i (`SHIP_MULTI_BASEID=0`/`More1=0`) tekrar
  açılamıyor. (İlk scripted `MORE=m_small_ship_n` yolu çalışıyor.) Fix: `0`'ı geçerli değer
  say, "yok" için nullable/explicit result.
- [ ] **B7 (P1) — RawMultiId vs ClientMultiArtId ayrımı yok.** `HousingEngine.cs:582`
  `BaseId=multiId`, `ShipEngine.cs:99` `BaseId=orientedId` — raw multi.idx index ile client
  art ID aynı alanda. Fix: RawMultiId/ClientMultiArtId/StructureKind/ScriptMultiDef ayrımı +
  tek encode/decode katmanı.
- [ ] **B8 (P1) — 0x99 preview paketi + Source-X anchor-Y düzeltmesi yok.**
  `PacketDefinitions.cs:116` sadece uzunluk tablosunda; outgoing sınıfı yok, deed normal
  `PacketTarget` kullanıyor. Source-X tıklanan Y'yi footprint'e göre düzeltiyor
  (`CItemMulti.cpp:3288`), SphereNet ham X/Y/Z kullanıyor (`ClientItemUseHandler.cs:1194`).
- [ ] **B9 (P1) — Target callback güvenlik yeniden-doğrulaması yok.** Cursor cevabında sadece
  `deedItem.IsDeleted` kontrol ediliyor (`ClientItemUseHandler.cs:1193`); deed takas/mesafe/
  LOS/map/ölü-jailed/limit yeniden doğrulanmıyor. Fix: placement'ı atomik transaction yap.
- [ ] **B10 (P2) — House storage sabit 400.** `HousingEngine.cs:119` `_baseStorage=400`;
  MULTIDEF `BaseStorage` placement'ta uygulanmıyor. Küçük ev/keep/castle farkı yok.
- [ ] **B11 (P2) — 0xF6 ship-move paketi component/yolcu listesi taşımıyor.**
  `GamePackets.cs:755` `PacketBoatSmoothMove` sadece serial/speed/dir/x/y/z.
- [ ] **B12 (P2) — Ship su kontrolü land-only.** `ShipEngine.cs:836` `IsWaterAt` sadece land
  `IsWet`; wet static/surface, dock/rock, su-Z, diğer hull, HS access bakılmıyor.
- [x] **B13 (P2) — Custom foundation deed tag'inden algılanıyor.** (YAPILDI: `HousingEngine.
  IsCustomFoundation(multiId)` = MULTIDEF `MultiTypeName=="t_multi_custom"` (B3'ten); deed handler
  `customFoundation = CUSTOMHOUSE tag ∨ IsCustomFoundation(multiId)` → ilk foundation deed'i (tag'siz)
  artık MultiCustom açılıyor. Test: SourceXPlacementResultTests.IsCustomFoundation.)
  `ClientItemUseHandler.cs:1175` sadece `CUSTOMHOUSE` deed tag'i; ilk foundation deed'inde tag
  yoksa custom yerine klasik multi açılıyor. Fix: resolved MULTIDEF `t_multi_custom` type'ından
  belirle.

---

## C. Config sözleşmesi (P1/P2 — okunuyor ama uygulanmıyor)

Öneri: her ayarı sınıflandır (uygulanıyor / alias / metadata / deprecated / bağlantısı-eksik);
desteklenmeyen ayar başlangıç uyarısı üretsin.

- [x] **C1 (P2) — TICKPERIOD/ServerTickMs/README uyuşmazlığı.** (YAPILDI: `<TICKPERIOD>` script
  okuması gerçek `ServerTickMs`'i döndürüyor; `TICKPERIOD` ini legacy alias (ServerTickMs yoksa);
  sphere.ini `TICKPERIOD=250`→`ServerTickMs=100` (efektif değere eşit, sürpriz yok). README
  perf-benchmark paragrafı o ölçümün config'i olabilir → dokunulmadı. Test: ConfigRegressionTests
  alias.) `sphere.ini:516 TICKPERIOD=250`
  (yorum "250ms" diyor, yanlış), runtime `ServerTickMs` (default **100**, `Program.Tick.cs:86`),
  `README-TR.md:145` "**50ms**". `TICKPERIOD` ini→ServerTickMs'e hiç parse edilmiyor; script var
  hardcoded "100" (`Program.Scripting.cs:82`). Fix: tek kanonik isim + alias + doc düzelt.
- [x] **C2 (P1) — GameMinuteLength inert.** (YAPILDI: Program.cs `_world.GameMinuteLengthMs =
  GameMinuteLength*1000`; config default 60→20 (mevcut 20s'e hizalı, default sürpriz yok);
  sphere.ini GAMEMINUTELENGTH=8 artık uygulanıyor.) `SphereConfig.cs:498` okuyor, runtime
  `GameWorld.GameMinuteLengthMs` sabit `20_000` (`:1093`), config akmıyor. sphere.ini'deki değer
  (8sn) uygulanmıyor.
- [x] **C3 (P2) — DistanceWhisper/Talk/Yell hardcoded.** (YAPILDI: SpeechEngine const'ları instance
  property'ye çevrildi, EngineWiring config'ten set ediyor (Say←DistanceTalk); config Yell default
  60→48 (mevcut efektife hizalı).) `SpeechEngine.cs:45` const
  Say=18/Whisper=3/Yell=**48**; config (`:408`, Yell **60**) yok sayılıyor (48≠60 kanıt).
- [x] **C4 (P2) — MaxFame/MaxKarma/MinKarma hardcoded clamp.** (YAPILDI: DeathEngine static config
  alanları (default'lar eşit → sürpriz yok), Program.cs config'ten set; clamp literalleri
  değiştirildi.) `DeathEngine.cs:459` literal
  `0,10000`; config (`:222`) referans edilmiyor (varsayılanlar eşit olduğu için şimdilik zararsız).
- [ ] **C5 (P2) — MinCharDeleteTime yok sayılıyor.** (ERTELENDİ: per-char CreateDate alanı YOK →
  altyapı gerekir (yeni persist edilen alan). Ayrı küçük iş.) `GameClient.Handlers.cs:62` sadece
  şifre+online kontrol edip hemen `ch.Delete()` (`:91`); yaş/oluşturma-zamanı kontrolü yok.
- [x] **C6 (P2) — UseHttp yok sayılıyor.** (YAPILDI: Program.AdminPanel web status `if (_config.UseHttp)`
  ile gate'lendi; config default false→true (mevcut koşulsuz davranış korunur, UseHttp=0 kapatır).) `SphereConfig.cs:716` okunuyor ama tüketen yok;
  `Program.AdminPanel.cs:175` web status'u koşulsuz `Start()` ediyor.
- [x] **C7 (P2) — MapReadId yerine MapSendId ile MUL okunuyor.** (YAPILDI: `_mapData.InitMap`
  artık `MapReadId` kullanıyor (hangi map*.mul); `_world.InitMap` MapSendId (client id). Default
  ikisi de 0 → değişim yok.) `Program.cs:713/716`
  `InitMap(mapDef.MapSendId,...)`; MapReadId sadece validation'da. İkisi farklıysa yanlış MUL
  okunur.
- [x] **C8 (P2) — MapViewSize network view range'e uygulanmıyor.** (YAPILDI: `NetState.DefaultViewRange`
  static'i eklendi, `ViewRange` init'i ondan; Program.cs `MapViewSize`'tan set ediyor. Client 0xC8
  max clamp'ine (24) dokunulmadı — 24→18 sürprizi olmasın. Test: NetStateViewRangeTests.) (KISMİ maddenin aksiyon
  kısmı) `NetState.ViewRange` default 18, sadece client 0xC8 ile değişiyor (clamp 4-24);
  `MapViewSize`/`MapViewSizeMax` view range'e bağlı değil. (Radar için kullanılıyor, dokunma.)

---

## D. Yarım mekanikler (P2)

- [ ] **D1 (P2) — Işık kaynakları yaşam döngüsü yok.** `ClientItemUseHandler.cs:1234` yakınca
  tek `LIGHT_CHARGES` düşüyor + `LightOut→LightLit` toggle; **yanma süresi/otomatik sönme/
  zaman-tabanlı charge tüketimi/persistent burn-time yok** (`Item.OnTick` switch'inde LightLit
  case'i yok, `:2705`). Fix: SetTimeout ile burn timer + charge tick + auto-extinguish + save.
- [ ] **D2 (P2 — BÜYÜK, ayrı proje) — Spell school'ları.** `ApplySpecificSpell` (`:1628`) sadece
  Magery + Necromancy case'leri; Chivalry (Source-X'te C++ yok → yapılmadı, ayrı karar) /
  Bushido/Ninjitsu/Spellweaving/Mysticism generic damage/heal'e düşüyor. Risk: eksik spell
  "desteklenmiyor" demek yerine mana/reagent tüketip etkisiz. (PARITY_BACKLOG_SUB90.md ile
  ortak — necromancy gibi per-school çok-wave'lik iş.) Fix: eksik spell'i açıkça pasif işaretle.

---

## E. Performans (P0/P1/P2 — hepsi kod düzeyinde GERÇEK)

Not: canlı log analizi ayrıca host/scheduler baskısına da işaret ediyor (yield/net_in
gecikmeleri); aşağıdakiler onu **büyüten** kesin kod borçları.

- [x] **E1 (P0 — YÜKSEK etki / DÜŞÜK efor, EN UCUZ KAZANIM) — StateRecorder her tick full
  `ToArray`.** (YAPILDI: `Tick` imzası `Func<IEnumerable<Character>>` lazy provider'a çevrildi;
  roster SADECE move-scan/snapshot due olduğunda (2s/15s) materialize ediliyor. Caller
  `GetAllObjects().OfType<Character>()` yerine char-only `GetAllCharactersSnapshot` metod
  grubunu geçiyor. Idle tick'te sıfır tahsis. Test: StateRecorderTests.
  Tick_InvokesRosterProviderOnlyWhenScanIsDue.) `Program.Tick.cs:566/788` `_stateRecorder?.Tick(..., _world.GetAllObjects()
  .OfType<Character>())`; `GetAllObjects()` (`GameWorld.cs:1468`) `_objects.Values.ToArray()`
  — argüman interval kontrolünden ÖNCE değerlendiği için ~52K obje dizisi **her tick** (10×/sn,
  ~4MB/s) kopyalanıyor, recorder içinde 2s/15s'de bir tarasa bile. Fix: interval kontrolünü öne
  al **veya** mevcut `GetAllCharactersSnapshot()` (`:1473`) / players-only kullan.
- [ ] **E2 (P0 — YÜKSEK etki / YÜKSEK efor) — Save tamamen senkron main-loop'ta.**
  `Program.Tick.cs:191` inline `PerformSave()`; snapshot+serialize tek thread (`WorldSaver.cs:208`),
  sadece shard disk-yazımı `Task.WaitAll` ile paralel (`:358`) → oyuncu thread'i serbest kalmıyor
  (~483ms stall). `SAVEBACKGROUND`/`SAVESECTORSPERTICK` **okunuyor ama kullanılmıyor** (dead config,
  yanıltıcı). Fix: main thread'de immutable snapshot yakala → serialize/write'ı background worker'a
  ver, ikinci save'i kuyruğa al, atomik temp+commit koru.
- [x] **E3 (P1 — MED-YÜKSEK / MED) — TIMERF her tick full-world scan.** (YAPILDI: `_objectsWithTimerF`
  active-set — `ObjBase.AddTimerF` mevcut `ResolveWorld` üzerinden `GameWorld.TrackTimerFObject`'e
  kaydediyor (YENİ static YOK; tüm ObjBase world erişimiyle aynı resolver → cross-world riski yok).
  `TickTimerF` artık sadece timer taşıyan objeleri (küçük set) geziyor, tick sırasında boş/silinmiş
  olanları prune ediyor; `DeleteObject` set'ten çıkarıyor. Due-time heap yerine active-set seçildi —
  TIMERF nadir, set küçük, invalidation tek funnel (AddTimerF). Test: PerfIndexTests (ground + contained
  item fire-once + prune; contained case sector-only index'in kaçıracağını kanıtlıyor).)
- [x] **E4 (P2 — MED / MED) — Decay catch-up 5sn'de full-world scan.** (YAPILDI: `_groundItems`
  superset index'i — tek `sector.AddItem` choke point'inden (`PlaceItem`) besleniyor, bu yüzden her
  ground item garantili içeride (load dahil). `CollectExpiredGroundItems` artık `_objects.Values`
  yerine bu set'i geziyor; alınıp cebe konan item'lar (`IsOnGround==false`) tarama sırasında lazy
  prune ediliyor — DecayTime'ın 22 call-site'ına hook GEREKMEDİ. Maliyet obje sayısıyla değil loose
  ground item sayısıyla ölçekleniyor. Test: PerfIndexTests (expired-only collect + picked-up prune).)
- [ ] **E5 (P1 — MED / MED) — Sleeping-sector maintenance 3dk'da tek tick'te toplu.**
  `GameWorld.cs:1226` her item-içeren sektörü tek tick'te `OnMaintenanceTick`; per-tick bütçe/
  cursor yok → periyodik `snapshot` spike. Fix: tick-başına süre/obje bütçesi + resume cursor.
- [ ] **E6 (P1 — MED / MED-YÜKSEK) — Network tamamen main-loop'ta.** `USEASYNCNETWORK`
  **hiç okunmuyor** (SphereConfig'te field yok), `NETWORKTHREADS` okunup kullanılmıyor. Accept
  döngüsü unbounded (`NetworkManager.cs:207`), her accept'te full `_states` IP taraması (`:228`),
  1100-slot iterasyon; login/unknown send bloklayan `Socket.Send` (`NetState.cs:695`). Fix:
  accept bütçesi + sayaç-tabanlı IP tally + non-blocking login output.
- [ ] **E7 (P2 — DÜŞÜK-MED / DÜŞÜK) — Auto worker = ProcessorCount.** `Program.Tick.cs:609`
  `MulticoreWorkerCount>0 ? ... : Environment.ProcessorCount`; sektör parallel eşiği 50
  (`GameWorld.cs:1286`). Paylaşımlı/overcommit VM'de ana thread starvation. Fix: "en az bir core
  ana thread'e" varsayılanı + VPS profilleri.
- [ ] **E8 (P1 — telemetri) — `snapshot` yanlış-etiket + apply↔flush arası ölçülmeyen işler.**
  `_telemetrySnapshotUs` (`Program.Tick.cs:613-631`) tüm world-tick grubunu (OnTickParallel +
  spell + wake + wheel + client-snapshot) ölçüyor; replay/StateRecorder/macro/reschedule
  (`:785-807`) apply↔flush arası phase alanı olmadan çalışıyor → yanlış dominant. Fix: alt-fazlara
  böl (active_sector_tick/timerf_scan/sleeping_maintenance/... + scheduler_gap = wall-cpu).

---

## F. Atıl altyapı temizliği (P2/P3)

> **Batch 5 kararı (doğrulama sonrası):** F grubu körlemesine "sil" değil — her biri referans
> sayımı + niyet analiziyle incelendi. Gerçekten **redundant/divergence riski** olanlar silindi
> (F3, F4); **kasıtlı ama henüz bağlanmamış** (doğru + testli) scaffolding ise silinmeyip doğru
> sınıflandırıldı (F1, F2, F5) — parity-testli/tutarlı kodu atmak bu projenin "doğrula, uydurma"
> ilkesine aykırı olurdu. Git geçmişi silinenleri korur.

- [~] **F1 (P3) — BotPerformanceGate CI'a bağlı değil → KASITLI (tutuldu).** Doğrulama: bot
  diagnostics alt sistemi CANLI (`BotEngine`, `TickHistogram`, `Program.cs`/`Program.Tick.cs`
  kullanıyor); yalnızca `BotPerformanceGate` (CI-gate eşik değerlendirici) henüz bot-report çıktısına
  ve CI exit-code'una bağlı değil. Doğru + tutarlı bir parça, eksik olan tek şey CI hookup'ı (kapsam
  dışı). Kaldırılmadı; STUB_INVENTORY'de "kasıtlı-bağlanmamış" olarak işaretlendi.
- [~] **F2 (P3) — Fast-walk stack paketleri → KASITLI (tutuldu).** `PacketFastWalkStackInit/Push`
  hiç construct/send edilmiyor AMA `DeferredParityTests.PacketFastWalkStackInit_BuildsExpectedPayload`
  ile wire-format PARITY TESTİ var — kasıtlı, doğrulanmış protokol scaffolding'i. SphereNet zaman
  tabanlı hareket kısıtı kullanıyor; bu paketler ileride era-uyumlu key rotasyonu için hazır. Testli
  kod silinmedi; STUB_INVENTORY'de belgelendi.
- [x] **F3 (P3) — ExpansionInfo tablosu → SİLİNDİ.** `ExpansionInfo.GetInfo` hiç çağrılmıyordu;
  feature mask'leri `GameClient.Login`/`NetState` bağımsız kuruyor. İkinci (divergence riskli) doğruluk
  kaynağıydı → `ExpansionInfo.cs` kaldırıldı. `Expansion`/`FeatureFlags` enum'ları (başka yerde
  kullanılıyor) korundu.
- [x] **F4 (P3) — ExpressionGlobals / ConditionalEvaluator → SİLİNDİ.** İkisi de hiç instantiate/call
  edilmeyen Source-X-şekilli redundant kabuk: gerçek fonksiyon zaten canlı motorda (global VAR/list
  state VarMap/ListMap'te, koşul değerlendirme `ExpressionParser.EvaluateConditional`'da). Bağlanırsa
  ikinci global-state/expression sistemi olurlardı (review'ın uyardığı divergence) → her iki dosya da
  kaldırıldı.
- [~] **F5 (P3) — OnSpeedHackDetected tüketicisi yok → KASITLI (tutuldu).** Doğrulama: speedhack
  ALGILAMA aktif (`ClientCombatHandler:362-379` — verdict, LogWarning, Kick→MarkClosing hepsi çalışıyor).
  `OnSpeedHackDetected` event'i operatörler için deliberate bir extensibility/audit hook'u (yorumu bunu
  açıkça belirtiyor); güvenlik açığı değil, kick yolu bağımsız. Gözlemlenebilirlik zaten log'da mevcut.
  API'den çıkarmak yerine STUB_INVENTORY'de "kasıtlı extensibility hook" olarak belgelendi.

---

## G. Test & doküman boşlukları (P2)

- [x] **G1 (P2) — Üç map diagnostic testi `return` ile "fake pass".** (YAPILDI: xUnit v2'de
  dynamic `Assert.Skip` yok → 3 diagnostic `[Fact]`'e static `[Fact(Skip=...)]` eklendi; artık
  gerçek **Skipped** raporlanıyor (suite 1838 geçti + 3 atlandı, eskiden 3 fake-pass). Local
  çalıştırmak için Skip kaldırılır.)
  `StairThrowDiagnosticTests.cs:29/111/309` `C:\mortechUO\mul` yoksa `WriteLine("SKIP")` + `return`
  → xUnit'te erken return = **passed** (skipped değil). "0 skipped" üç çalışmayan testi gizliyor.
  Fix: gerçek `Skip` (Assert.Skip / SkippableFact) + "Skips cleanly" yorumunu düzelt.
- [x] **G2 (P2) — Trigger dokümanları bayat (NPCSeeWantItem).** (YAPILDI: TRIGGERS.md,
  STUB_INVENTORY_TR.md, PARITY_MATRIX.md güncellendi — `NPCSeeWantItem` artık ateşleniyor
  (EngineWiring); güncel tek not-fired char trigger `UserVirtue` (virtue-gump, gump yok).) Trigger artık wire'lı+ateşleniyor
  (`Program.EngineWiring.cs:1660/1681`) ama docs hâlâ eksik gösteriyor: `STUB_INVENTORY_TR.md:37/40/
  202/252`, `TRIGGERS.md:23/245`, `PARITY_MATRIX.md:75`. Fix: docs güncelle.

---

## Önerilen sıra

1. **A1–A4** (başlangıç bağlantı bug'ları + shutdown-save) — sessiz, testsiz, düşük efor, yüksek
   güven kazanımı. **A5** (composition-root fixture) bunları kalıcı kılar.
2. **E1** (StateRecorder ToArray) — tek satırlık mantık, yüksek etki. **B1** (MultiReader 12/16) —
   house/ship'i modern veride açan bloklayıcı.
3. **E2** (save main-loop dışına) + **E3/E5/E6** (full-world scan / maintenance / network bütçe).
4. **C-grubu** config sözleşmesi (uygulanmayan ayarları bağla/işaretle).
5. **B2–B9** house/ship parity (deed inheritance, MULTIDEF, structured result, save TYPE, raw-0).
6. **D1** ışık yaşam döngüsü; **B10–B13** house/ship P2.
7. **F/G** atıl altyapı temizliği + test/doküman düzeltmeleri.
8. **D2** spell school'ları — ayrı büyük proje (PARITY_BACKLOG ile ortak).
